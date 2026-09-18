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
    private readonly Queue<SignalingEnvelope> _pendingViewerSignaling = new();
    private ISignalingConnection? _connection;
    private bool _isOwner;
    private bool _watchHooked;
    private bool _watchReady;

    public SessionSnapshot Snapshot { get; private set; } = SessionSnapshot.Idle;

    public IReadOnlyList<SignalingDiagnosticEntry> SignalingDiagnostics => _signalingDiagnostics.Entries;

    public event Action<SessionSnapshot>? Changed;

    public event Action<SignalingDiagnosticEntry>? SignalingDiagnosticAdded
    {
        add => _signalingDiagnostics.Added += value;
        remove => _signalingDiagnostics.Added -= value;
    }

    public async Task StartSharingAsync(MonitorInfo monitor, int maxViewers, CancellationToken ct)
    {
        RequireIdle();
        Publish(Snapshot with { Phase = SessionPhase.Preparing, Error = null });
        try
        {
            var created = await api.CreateScreenShareAsync(maxViewers, ct);
            _isOwner = true;
            await AttachAsync(created.SessionId, ct);

            if (publishHost is not null)
            {
                try
                {
                    await publishHost.StartAsync(monitor, ct);
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

            var quality = VideoQuality.Default;
            Publish(new SessionSnapshot(SessionPhase.Sharing, created.Code, created.SessionId, 0,
                _connection!.State, null, publishHost?.EncoderName, quality.FramesPerSecond,
                quality.ScaleFor(monitor.Width, monitor.Height).Height));
        }
        catch (SessionApiFailure failure)
        {
            await FailAsync(failure.Code);
        }
    }

    public async Task StartWatchingAsync(string code, CancellationToken ct)
    {
        RequireIdle();
        Publish(Snapshot with { Phase = SessionPhase.Joining, Error = null });
        try
        {
            var sessionId = await api.JoinAsync(code, ct);
            _isOwner = false;
            await AttachAsync(sessionId, ct);

            if (watchHost is not null)
            {
                if (!_watchHooked)
                {
                    watchHost.WatchStateChanged += OnWatchState;
                    _watchHooked = true;
                }

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

            Publish(new SessionSnapshot(SessionPhase.Watching, null, sessionId, 0, _connection!.State, null,
                Watching: watchHost is null ? null : WatchState.Waiting,
                DecoderName: watchHost?.DecoderName));
        }
        catch (SessionApiFailure failure)
        {
            await FailAsync(failure.Code);
        }
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (Snapshot.Phase == SessionPhase.Idle) return;
        Publish(Snapshot with { Phase = SessionPhase.Ending });

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
    }

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

    private async Task AttachAsync(Guid sessionId, CancellationToken ct)
    {
        var connection = connectionFactory();
        connection.FrameReceived += OnFrame;
        _connection = connection;
        await connection.StartAsync(sessionId, ct);

        // Subscribed only after the initial connect: the state change that connecting itself
        // produces is already reflected in the snapshot the caller is about to publish, and
        // reacting to it here would emit a redundant intermediate snapshot to the UI.
        connection.StateChanged += OnSignalingState;
    }

    private async Task DetachAsync()
    {
        if (_connection is null) return;
        _connection.FrameReceived -= OnFrame;
        _connection.StateChanged -= OnSignalingState;
        await _connection.DisposeAsync();
        _connection = null;
        _isOwner = false;
        ClearPendingViewerSignaling();
    }

    private void OnFrame(SignalingEnvelope envelope)
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
                Publish(Snapshot with { ViewerCount = Snapshot.ViewerCount + 1 });
                AddViewer(envelope);
                break;

            case SignalingMessageTypes.ParticipantReconnected when sharing:
                Publish(Snapshot with { ViewerCount = Snapshot.ViewerCount + 1 });
                AddViewer(envelope);
                break;

            case SignalingMessageTypes.SessionLeft when sharing:
                Publish(Snapshot with { ViewerCount = Math.Max(0, Snapshot.ViewerCount - 1) });
                if (publishHost is not null && TryReadParticipant(envelope) is { } left)
                    _ = publishHost.RemoveViewerAsync(left);
                break;

            case SignalingMessageTypes.ParticipantDisconnected when sharing:
                // "Transiently unreachable", not "gone": the server holds the participant for
                // its grace period, and tearing the peer down here would force a full
                // renegotiation for a viewer that is about to come back.
                Publish(Snapshot with { ViewerCount = Math.Max(0, Snapshot.ViewerCount - 1) });
                break;

            case SignalingMessageTypes.WebRtcAnswer when sharing:
            case SignalingMessageTypes.WebRtcIceCandidate when sharing:
                if (publishHost is not null)
                    _ = publishHost.HandleSignalingAsync(envelope, CancellationToken.None);
                break;

            case SignalingMessageTypes.SessionEnded:
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

    private void OnSignalingState(SignalingState state) => Publish(Snapshot with { Signaling = state });

    /// <summary>
    /// A media stall changes what the viewer is seeing, never what the session is. Writing it
    /// into <see cref="SessionSnapshot.Phase"/> would claim the connection had dropped when it
    /// had not.
    /// </summary>
    private void OnWatchState(WatchState state)
    {
        if (Snapshot.Phase != SessionPhase.Watching) return;
        Publish(Snapshot with { Watching = state });
    }

    private async Task FailAsync(string code)
    {
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
        Snapshot = snapshot;
        Changed?.Invoke(snapshot);
    }
}
