using System.Text.Json;
using SonicDesktopRelay.Media;
using SonicDesktopRelay.Signaling;

namespace SonicDesktopRelay.Presentation;

/// <summary>
/// The single answer to "what is this app doing right now". One machine covers both roles,
/// because in this phase a device shares or watches, never both — and one machine is what
/// keeps the Diagnostics page honest instead of inventing a second version of the truth.
/// </summary>
public sealed class SessionRuntime(
    ISessionApi api,
    Func<ISignalingConnection> connectionFactory,
    IVideoPublishHost? publishHost = null,
    IVideoWatchHost? watchHost = null,
    SignalingDiagnosticBuffer? signalingDiagnostics = null)
{
    private const int PendingViewerSignalingCapacity = 128;

    private readonly SignalingDiagnosticBuffer _signalingDiagnostics = signalingDiagnostics ?? new();
    private readonly object _pendingViewerSignalingGate = new();
    private readonly object _captureTargetGate = new();
    private readonly object _snapshotGate = new();
    private readonly SemaphoreSlim _stopGate = new(1, 1);
    private readonly Queue<SignalingEnvelope> _pendingViewerSignaling = new();
    private ISignalingConnection? _connection;
    private Action<SignalingEnvelope>? _frameHandler;
    private Action<SignalingState>? _signalingStateHandler;
    private Action<string>? _captureTargetClosedHandler;
    private Action<WatchState>? _watchStateHandler;
    private Action<string>? _watchNegotiationHandler;
    private long _sessionGeneration;
    private bool _isOwner;
    private bool _watchReady;
    private bool _publishStartInProgress;
    private bool _captureTargetClosedDuringStart;
    private bool _captureTargetCloseHandled;

    public SessionSnapshot Snapshot { get; private set; } = SessionSnapshot.Idle;

    public IReadOnlyList<SignalingDiagnosticEntry> SignalingDiagnostics => _signalingDiagnostics.Entries;

    public event Action<SessionSnapshot>? Changed;

    /// <summary>Applies a sample only while the current session can own live media.</summary>
    public void UpdateMetrics(SessionMediaMetrics? metrics)
    {
        lock (_snapshotGate)
        {
            if (Snapshot.Phase is not (SessionPhase.Sharing or SessionPhase.Watching)) return;
            if (Equals(Snapshot.Metrics, metrics)) return;
            Publish(Snapshot with { Metrics = metrics });
        }
    }

    public event Action<SignalingDiagnosticEntry>? SignalingDiagnosticAdded
    {
        add => _signalingDiagnostics.Added += value;
        remove => _signalingDiagnostics.Added -= value;
    }

    public Task StartSharingAsync(MonitorInfo monitor, int maxViewers, CancellationToken ct) =>
        StartSharingAsync(new CaptureTarget.Monitor(monitor), VideoPublishProfile.Default, maxViewers, ct);

    public Task StartSharingAsync(
        MonitorInfo monitor,
        VideoPublishProfile profile,
        int maxViewers,
        CancellationToken ct) =>
        StartSharingAsync(new CaptureTarget.Monitor(monitor), profile, maxViewers, ct);

    public async Task StartSharingAsync(
        CaptureTarget target,
        VideoPublishProfile profile,
        int maxViewers,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(profile);
        long generation;
        lock (_snapshotGate)
        {
            RequireIdle();
            generation = ++_sessionGeneration;
            Publish(Snapshot with { Phase = SessionPhase.Preparing, Error = null });
        }
        try
        {
            var created = await api.CreateScreenShareAsync(maxViewers, ct);
            _isOwner = true;
            await AttachAsync(created.SessionId, generation, ct);

            if (publishHost is not null)
            {
                lock (_captureTargetGate)
                {
                    _publishStartInProgress = true;
                    _captureTargetClosedDuringStart = false;
                    _captureTargetCloseHandled = false;
                }
                _captureTargetClosedHandler = reason => OnCaptureTargetClosed(reason, generation);
                publishHost.CaptureTargetClosed += _captureTargetClosedHandler;

                try
                {
                    await publishHost.StartAsync(target, profile, ct);
                }
                catch (Exception e) when (e is InvalidOperationException or PlatformNotSupportedException)
                {
                    // The backend session is already up but nothing can be sent over it. Ending
                    // it and landing in Failed is the only honest outcome: leaving the runtime
                    // in Preparing would wedge it, because RequireIdle refuses every later start.
                    await EndOwnedSessionAsync(created.SessionId, ct);
                    await FailAsync("media_unavailable");
                    return;
                }
            }

            var sourceDimensions = CaptureDimensions(target);
            var quality = VideoQuality.InitialFor(profile);
            var sharingSnapshot = new SessionSnapshot(SessionPhase.Sharing, created.Code, created.SessionId, 0,
                _connection!.State, null, publishHost?.EncoderName, quality.FramesPerSecond,
                quality.ScaleFor(sourceDimensions.Width, sourceDimensions.Height).Height);

            var targetClosedDuringStart = false;
            lock (_captureTargetGate)
            {
                _publishStartInProgress = false;
                targetClosedDuringStart = _captureTargetClosedDuringStart;
                if (!targetClosedDuringStart) Publish(sharingSnapshot);
            }

            if (targetClosedDuringStart)
            {
                if (publishHost is not null) await publishHost.StopAsync();
                await EndOwnedSessionAsync(created.SessionId, ct);
                await DetachAsync();
                Publish(new SessionSnapshot(
                    SessionPhase.Failed, null, null, 0, SignalingState.Disconnected, "capture_target_closed"));
            }
        }
        catch (SessionApiFailure failure)
        {
            await FailAsync(failure.Code);
        }
    }

    public Task StartWatchingAsync(string code, CancellationToken ct) =>
        StartWatchingCoreAsync(token => api.JoinAsync(code, token), ct);

    public Task StartWatchingSessionAsync(Guid sessionId, CancellationToken ct) =>
        StartWatchingCoreAsync(token => api.JoinByIdAsync(sessionId, token), ct);

    private async Task StartWatchingCoreAsync(Func<CancellationToken, Task<Guid>> join, CancellationToken ct)
    {
        long generation;
        lock (_snapshotGate)
        {
            RequireIdle();
            generation = ++_sessionGeneration;
            Publish(Snapshot with { Phase = SessionPhase.Joining, Error = null });
        }
        try
        {
            var sessionId = await join(ct);
            _isOwner = false;
            await AttachAsync(sessionId, generation, ct);

            if (watchHost is not null)
            {
                _watchStateHandler = state => OnWatchState(state, generation);
                _watchNegotiationHandler = failure => OnWatchNegotiationFailed(failure, generation);
                watchHost.WatchStateChanged += _watchStateHandler;
                watchHost.NegotiationFailed += _watchNegotiationHandler;

                try
                {
                    await watchHost.StartAsync(ct);
                    await MarkWatchHostReadyAsync(ct);
                }
                catch (Exception e) when (e is InvalidOperationException or PlatformNotSupportedException)
                {
                    // Same reasoning as the publishing side: the socket is up but nothing can
                    // be rendered over it, and leaving the runtime in Joining would wedge it.
                    ClearPendingViewerSignaling();
                    await watchHost.StopAsync();
                    await FailAsync("media_unavailable");
                    return;
                }
            }

            Publish(new SessionSnapshot(SessionPhase.Watching, null, sessionId, 0, _connection!.State, Snapshot.Error,
                Watching: watchHost is null ? null : WatchState.Waiting,
                DecoderName: watchHost?.DecoderName));
        }
        catch (SessionApiFailure failure)
        {
            await FailAsync(failure.Code);
        }
    }

    public async Task StopAsync(CancellationToken ct) => await StopCoreAsync(ct, null);

    private async Task<long?> StopCoreAsync(CancellationToken ct, long? expectedGeneration)
    {
        await _stopGate.WaitAsync(ct);
        try
        {
            long stoppedGeneration;
            lock (_snapshotGate)
            {
                if (Snapshot.Phase == SessionPhase.Idle
                    || expectedGeneration is { } expected
                       && (expected != _sessionGeneration || Snapshot.Phase != SessionPhase.Sharing))
                    return null;
                stoppedGeneration = ++_sessionGeneration;
                Publish(Snapshot with { Phase = SessionPhase.Ending });
            }

            // Capture stops before the session ends: the last thing a viewer should see is the
            // screen going away, not frames arriving for a session the server has already closed.
            if (publishHost is not null) await publishHost.StopAsync();
            if (watchHost is not null)
            {
                ClearPendingViewerSignaling();
                await watchHost.StopAsync();
            }

            // Only the publishing device may end a session for everyone; a viewer leaving simply
            // drops its own connection, and calling end as a viewer would be a 403 at best.
            if (_isOwner && Snapshot.SessionId is { } sessionId) await EndOwnedSessionAsync(sessionId, ct);

            await DetachAsync();
            Publish(SessionSnapshot.Idle);
            return stoppedGeneration;
        }
        finally
        {
            _stopGate.Release();
        }
    }

    private void OnCaptureTargetClosed(string reason, long generation)
    {
        lock (_captureTargetGate)
        {
            lock (_snapshotGate)
            {
                if (generation != _sessionGeneration) return;
                if (_publishStartInProgress)
                {
                    _captureTargetClosedDuringStart = true;
                    return;
                }

                if (Snapshot.Phase != SessionPhase.Sharing || _captureTargetCloseHandled) return;
                _captureTargetCloseHandled = true;
            }
        }

        _ = StopAfterCaptureTargetClosedAsync(reason, generation);
    }

    private async Task StopAfterCaptureTargetClosedAsync(string reason, long generation)
    {
        try
        {
            var stoppedGeneration = await StopCoreAsync(CancellationToken.None, generation);
            lock (_snapshotGate)
            {
                if (stoppedGeneration is null || stoppedGeneration != _sessionGeneration
                    || Snapshot.Phase != SessionPhase.Idle) return;
                Publish(new SessionSnapshot(
                    SessionPhase.Failed, null, null, 0, SignalingState.Disconnected, "capture_target_closed"));
            }
        }
        catch (Exception e) when (e is InvalidOperationException or TaskCanceledException)
        {
            // Target closure is terminal; a concurrent user stop may already have ended the session.
        }
    }

    private static (int Width, int Height) CaptureDimensions(CaptureTarget target) => target switch
    {
        CaptureTarget.Monitor monitor => (monitor.Info.Width, monitor.Info.Height),
        CaptureTarget.Window window => (window.Info.Width, window.Info.Height),
        _ => throw new ArgumentOutOfRangeException(nameof(target))
    };

    private async Task EndOwnedSessionAsync(Guid sessionId, CancellationToken ct)
    {
        try
        {
            await api.EndAsync(sessionId, ct);
        }
        catch (SessionApiFailure)
        {
            // The session may already be over. Stopping locally must still succeed.
        }
    }

    private async Task AttachAsync(Guid sessionId, long generation, CancellationToken ct)
    {
        var connection = connectionFactory();
        _frameHandler = envelope => OnFrame(envelope, generation);
        _signalingStateHandler = state => OnSignalingState(state, generation);
        connection.FrameReceived += _frameHandler;
        _connection = connection;
        await connection.StartAsync(sessionId, ct);

        // Subscribed only after the initial connect: the state change that connecting itself
        // produces is already reflected in the snapshot the caller is about to publish, and
        // reacting to it here would emit a redundant intermediate snapshot to the UI.
        connection.StateChanged += _signalingStateHandler;
    }

    private async Task DetachAsync()
    {
        if (publishHost is not null && _captureTargetClosedHandler is not null)
            publishHost.CaptureTargetClosed -= _captureTargetClosedHandler;
        _captureTargetClosedHandler = null;
        if (watchHost is not null)
        {
            if (_watchStateHandler is not null) watchHost.WatchStateChanged -= _watchStateHandler;
            if (_watchNegotiationHandler is not null) watchHost.NegotiationFailed -= _watchNegotiationHandler;
        }
        _watchStateHandler = null;
        _watchNegotiationHandler = null;
        var connection = _connection;
        if (connection is null) return;
        if (_frameHandler is not null) connection.FrameReceived -= _frameHandler;
        if (_signalingStateHandler is not null) connection.StateChanged -= _signalingStateHandler;
        _frameHandler = null;
        _signalingStateHandler = null;
        _connection = null;
        _isOwner = false;
        ClearPendingViewerSignaling();
        await connection.DisposeAsync();
    }

    private void OnFrame(SignalingEnvelope envelope, long generation)
    {
        lock (_snapshotGate)
        {
            if (generation != _sessionGeneration) return;
            HandleFrame(envelope);
        }
    }

    private void HandleFrame(SignalingEnvelope envelope)
    {
        var phase = Snapshot.Phase;
        var signaling = _connection?.State ?? Snapshot.Signaling;
        var sharing = phase == SessionPhase.Sharing;
        var watching = phase == SessionPhase.Watching;

        _signalingDiagnostics.Add(new SignalingDiagnosticEntry(
            DateTimeOffset.Now,
            SignalingDirection.RX,
            envelope.Type,
            phase,
            signaling,
            envelope.From,
            envelope.To,
            IsHandled(envelope.Type, phase)));

        switch (envelope.Type)
        {
            // One machine covers both roles, so the two negotiation halves have to be kept
            // apart: a sharing device that answered its own offers would negotiate with itself.
            case SignalingMessageTypes.PublisherReady when watching:
            case SignalingMessageTypes.WebRtcOffer when watching:
            case SignalingMessageTypes.WebRtcIceCandidate when watching:
                if (watchHost is not null)
                    _ = watchHost.HandleSignalingAsync(envelope, CancellationToken.None);
                break;

            case SignalingMessageTypes.PublisherReady when phase == SessionPhase.Joining:
            case SignalingMessageTypes.WebRtcOffer when phase == SessionPhase.Joining:
            case SignalingMessageTypes.WebRtcIceCandidate when phase == SessionPhase.Joining:
                BufferViewerSignaling(envelope);
                break;

            case SignalingMessageTypes.SessionJoined when sharing:
                if (TryUpdateSnapshot(SessionPhase.Sharing,
                        snapshot => snapshot with { ViewerCount = snapshot.ViewerCount + 1 }))
                    AddViewer(envelope);
                break;

            case SignalingMessageTypes.ParticipantReconnected when sharing:
                if (TryUpdateSnapshot(SessionPhase.Sharing,
                        snapshot => snapshot with { ViewerCount = snapshot.ViewerCount + 1 }))
                    AddViewer(envelope);
                break;

            case SignalingMessageTypes.SessionLeft when sharing:
                if (TryUpdateSnapshot(SessionPhase.Sharing,
                        snapshot => snapshot with { ViewerCount = Math.Max(0, snapshot.ViewerCount - 1) })
                    && publishHost is not null && TryReadParticipant(envelope) is { } left)
                    _ = publishHost.RemoveViewerAsync(left);
                break;

            case SignalingMessageTypes.ParticipantDisconnected when sharing:
                // "Transiently unreachable", not "gone": the server holds the participant for
                // its grace period, and tearing the peer down here would force a full
                // renegotiation for a viewer that is about to come back.
                TryUpdateSnapshot(SessionPhase.Sharing,
                    snapshot => snapshot with { ViewerCount = Math.Max(0, snapshot.ViewerCount - 1) });
                break;

            case SignalingMessageTypes.WebRtcAnswer when sharing:
            case SignalingMessageTypes.WebRtcIceCandidate when sharing:
            case SignalingMessageTypes.WebRtcRenegotiate when sharing:
            case SignalingMessageTypes.VideoReceiverStats when sharing:
                if (publishHost is not null)
                    _ = publishHost.HandleSignalingAsync(envelope, CancellationToken.None);
                break;

            case SignalingMessageTypes.SessionEnded:
                ++_sessionGeneration;
                if (publishHost is not null) _ = publishHost.StopAsync();
                if (watchHost is not null) _ = watchHost.StopAsync();
                _ = DetachAsync();
                Publish(SessionSnapshot.Idle);
                break;
        }
    }

    private bool IsHandled(string type, SessionPhase phase)
    {
        var sharing = phase == SessionPhase.Sharing;
        var watching = phase == SessionPhase.Watching;

        return type switch
        {
            SignalingMessageTypes.PublisherReady when watching || phase == SessionPhase.Joining => watchHost is not null,
            SignalingMessageTypes.WebRtcOffer when watching || phase == SessionPhase.Joining => watchHost is not null,
            SignalingMessageTypes.WebRtcIceCandidate when watching || phase == SessionPhase.Joining => watchHost is not null,
            SignalingMessageTypes.SessionJoined when sharing => true,
            SignalingMessageTypes.ParticipantReconnected when sharing => true,
            SignalingMessageTypes.SessionLeft when sharing => true,
            SignalingMessageTypes.ParticipantDisconnected when sharing => true,
            SignalingMessageTypes.WebRtcAnswer when sharing => publishHost is not null,
            SignalingMessageTypes.WebRtcIceCandidate when sharing => publishHost is not null,
            SignalingMessageTypes.WebRtcRenegotiate when sharing => publishHost is not null,
            SignalingMessageTypes.VideoReceiverStats when sharing => publishHost is not null,
            SignalingMessageTypes.SessionEnded => true,
            _ => false
        };
    }

    private void BufferViewerSignaling(SignalingEnvelope envelope)
    {
        if (watchHost is null) return;

        lock (_pendingViewerSignalingGate)
        {
            if (_watchReady)
            {
                _ = watchHost.HandleSignalingAsync(envelope, CancellationToken.None);
                return;
            }

            if (_pendingViewerSignaling.Count == PendingViewerSignalingCapacity)
                _pendingViewerSignaling.Dequeue();

            _pendingViewerSignaling.Enqueue(envelope);
        }
    }

    private async Task MarkWatchHostReadyAsync(CancellationToken ct)
    {
        if (watchHost is null) return;

        while (true)
        {
            SignalingEnvelope? next;
            lock (_pendingViewerSignalingGate)
            {
                if (_pendingViewerSignaling.Count == 0)
                {
                    _watchReady = true;
                    return;
                }

                next = _pendingViewerSignaling.Dequeue();
            }

            await watchHost.HandleSignalingAsync(next, ct);
        }
    }

    private void ClearPendingViewerSignaling()
    {
        lock (_pendingViewerSignalingGate)
        {
            _watchReady = false;
            _pendingViewerSignaling.Clear();
        }
    }

    private void AddViewer(SignalingEnvelope envelope)
    {
        if (publishHost is null) return;
        if (TryReadParticipant(envelope) is not { } participantId) return;
        _ = publishHost.AddViewerAsync(participantId, CancellationToken.None);
    }

    // The server names the participant in the payload; `from` is only set on relayed
    // peer-to-peer frames, so both are checked before giving up.
    private static Guid? TryReadParticipant(SignalingEnvelope envelope)
    {
        if (envelope.Payload is { } payload
            && payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("participantId", out var element)
            && element.ValueKind == JsonValueKind.String
            && Guid.TryParse(element.GetString(), out var participantId))
        {
            return participantId;
        }

        return envelope.From;
    }

    private void OnSignalingState(SignalingState state, long generation) =>
        TryUpdateSnapshot(
            generation,
            snapshot => snapshot.Phase is SessionPhase.Preparing or SessionPhase.Joining
                or SessionPhase.Sharing or SessionPhase.Watching,
            snapshot => snapshot with { Signaling = state });

    /// <summary>
    /// A media stall changes what the viewer is seeing, never what the session is. Writing it
    /// into <see cref="SessionSnapshot.Phase"/> would claim the connection had dropped when it
    /// had not.
    /// </summary>
    private void OnWatchState(WatchState state, long generation)
    {
        TryUpdateSnapshot(generation, snapshot => snapshot.Phase == SessionPhase.Watching,
            snapshot => snapshot with { Watching = state });
    }

    private void OnWatchNegotiationFailed(string failure, long generation)
    {
        TryUpdateSnapshot(
            generation,
            snapshot => snapshot.Phase is SessionPhase.Joining or SessionPhase.Watching,
            snapshot => snapshot with { Error = failure });
    }

    private bool TryUpdateSnapshot(SessionPhase phase, Func<SessionSnapshot, SessionSnapshot> update) =>
        TryUpdateSnapshot(snapshot => snapshot.Phase == phase, update);

    private bool TryUpdateSnapshot(
        Func<SessionSnapshot, bool> accepts,
        Func<SessionSnapshot, SessionSnapshot> update) =>
        TryUpdateSnapshot(_sessionGeneration, accepts, update);

    private bool TryUpdateSnapshot(
        long generation,
        Func<SessionSnapshot, bool> accepts,
        Func<SessionSnapshot, SessionSnapshot> update)
    {
        lock (_snapshotGate)
        {
            var current = Snapshot;
            if (generation != _sessionGeneration || !accepts(current)) return false;
            Publish(update(current));
            return true;
        }
    }

    private async Task FailAsync(string code)
    {
        lock (_snapshotGate) ++_sessionGeneration;
        await DetachAsync();
        Publish(new SessionSnapshot(SessionPhase.Failed, null, null, 0, SignalingState.Disconnected, code));
    }

    private void RequireIdle()
    {
        if (Snapshot.Phase is SessionPhase.Idle or SessionPhase.Failed) return;
        throw new InvalidOperationException(
            $"Cannot start a session while the runtime is {Snapshot.Phase}. Stop the current one first.");
    }

    private void Publish(SessionSnapshot snapshot)
    {
        lock (_snapshotGate)
        {
            Snapshot = snapshot.Phase is SessionPhase.Sharing or SessionPhase.Watching
                ? snapshot
                : snapshot with { Metrics = null };
            Changed?.Invoke(Snapshot);
        }
    }
}
