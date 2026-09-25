using System.Text.Json;
using SonicDesktopRelay.Media;
using SonicDesktopRelay.Signaling;

namespace SonicDesktopRelay.Rtc;

/// <summary>
/// The viewer half of negotiation: one publisher, one peer connection, and independent video
/// and audio decode pipelines. Where <see cref="VideoPublisher"/> fans one encode out to many
/// peers, this owns exactly one — a viewer watches a single publisher.
/// </summary>
public sealed class VideoSubscriber(
    ScreenWatchPipeline pipeline,
    AudioWatchPipeline? audioPipeline,
    IViewerPeerConnectionFactory peers,
    ISignalingConnection signaling,
    TimeProvider? time = null) : IAsyncDisposable
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly CancellationTokenSource _statsCancellation = new();
    private Task _statsTask = Task.CompletedTask;
    private int _statsStarted;
    private int _statsDisposed;
    private const int PendingCandidatesPerParticipant = 64;
    private const int PendingCandidateParticipants = 8;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, Queue<PendingIceCandidate>> _pendingCandidates = [];

    private IViewerPeerConnection? _peer;
    private ViewerNegotiationDiagnosticEntry? _lastPeerDiagnostic;
    private bool _remoteDescriptionReady;
    private bool _keyFrameHooked;
    private bool _disposed;
    private Guid? _negotiationId;
    private Guid? _expectedFallbackId;
    private bool _everConnected;
    private bool _fallbackStarted;
    private CancellationTokenSource? _fallbackCancellation;

    public VideoSubscriber(ScreenWatchPipeline pipeline, IViewerPeerConnectionFactory peers,
        ISignalingConnection signaling, TimeProvider? time = null)
        : this(pipeline, null, peers, signaling, time) { }


    private async Task SendStatsPeriodicallyAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2), _time);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                if (PublisherId is not { } publisher) continue;
                var stats = pipeline.TakeStatsSnapshot();
                stats = stats with { IntervalMilliseconds = Math.Clamp(stats.IntervalMilliseconds, 1000, 5000) };
                await signaling.SendAsync(SignalingMessageTypes.VideoReceiverStats, publisher, stats, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    public VideoSubscriber(
        ScreenWatchPipeline pipeline,
        IViewerPeerConnectionFactory peers,
        ISignalingConnection signaling)
        : this(pipeline, null, peers, signaling)
    {
    }

    /// <summary>
    /// The one participant this viewer will accept media from. Learned from the authenticated
    /// <c>from</c> field, never from a payload: a session can hold other viewers, and none of
    /// them may drive this connection.
    /// </summary>
    public Guid? PublisherId { get; private set; }

    /// <summary>
    /// Raised when offer/answer negotiation has definitively failed. The message contains only
    /// an actionable stage/reason; SDP, candidates and credentials are deliberately excluded.
    /// </summary>
    public event Action<string>? NegotiationFailed;

    public event Action<ViewerNegotiationDiagnosticEntry>? Diagnostic;

    public event Action<RtcTransportDiagnostics>? TransportDiagnosticsChanged;

    public RtcTransportDiagnostics? TransportDiagnostics { get; private set; }

    public async Task HandleAsync(SignalingEnvelope envelope, CancellationToken ct)
    {
        if (Volatile.Read(ref _statsDisposed) != 0) return;
        if (Interlocked.Exchange(ref _statsStarted, 1) == 0)
        {
            _statsTask = SendStatsPeriodicallyAsync(_statsCancellation.Token);
        }
        if (envelope.From is not { } from) return;

        switch (envelope.Type)
        {
            case SignalingMessageTypes.PublisherReady:
                await LearnPublisherAsync(from, ct);
                if (PublisherId != from) return;
                await signaling.SendAsync(SignalingMessageTypes.ViewerReady, from, new { }, ct);
                return;

            case SignalingMessageTypes.WebRtcOffer:
                EmitDiagnostic("viewer.offer.received", from: from);
                if (ReadString(envelope, "sdp") is not { } offerSdp) return;
                await LearnPublisherAsync(from, ct);
                if (PublisherId != from) return;
                var offerGeneration = ReadGuid(envelope, "negotiationId");
                await AnswerAsync(from, offerSdp, offerGeneration, ct);
                return;

            case SignalingMessageTypes.WebRtcIceCandidate:
                if (ReadString(envelope, "candidate") is not { } candidate) return;
                await ReceiveIceCandidateAsync(
                    from,
                    new PendingIceCandidate(candidate, ReadString(envelope, "sdpMid"), ReadIndex(envelope), ReadGuid(envelope, "negotiationId")),
                    ct);
                return;
        }
    }

    private async Task LearnPublisherAsync(Guid participant, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_disposed) return;
            if (PublisherId is { } known && known != participant) return;

            PublisherId ??= participant;

            // ICE can race ahead of the offer. Once the authenticated publisher is known,
            // anything buffered for some other participant is provably unrelated to this peer.
            foreach (var id in _pendingCandidates.Keys.Where(id => id != participant).ToArray())
                _pendingCandidates.Remove(id);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ReceiveIceCandidateAsync(Guid from, PendingIceCandidate candidate, CancellationToken ct)
    {
        IViewerPeerConnection? readyPeer = null;

        await _gate.WaitAsync(ct);
        try
        {
            if (_disposed) return;
            if (PublisherId is { } publisher && publisher != from) return;
            if (_expectedFallbackId is { } expected && candidate.NegotiationId != expected) return;
            if (_expectedFallbackId is null && _negotiationId is { } current && candidate.NegotiationId is { } received && received != current) return;

            if (PublisherId == from && _peer is { } peer && _remoteDescriptionReady)
            {
                readyPeer = peer;
            }
            else
            {
                BufferCandidate(from, candidate);
            }
        }
        finally
        {
            _gate.Release();
        }

        if (readyPeer is not null)
            await readyPeer.AddIceCandidateAsync(candidate.Candidate, candidate.SdpMid, candidate.SdpMLineIndex, ct);
    }

    private void BufferCandidate(Guid from, PendingIceCandidate candidate)
    {
        if (!_pendingCandidates.TryGetValue(from, out var queue))
        {
            if (_pendingCandidates.Count >= PendingCandidateParticipants) return;
            queue = new Queue<PendingIceCandidate>(PendingCandidatesPerParticipant);
            _pendingCandidates.Add(from, queue);
        }

        // Keep the earliest candidates and preserve their order. A pathological sender cannot
        // grow memory without bound or evict candidates belonging to another participant.
        if (queue.Count >= PendingCandidatesPerParticipant) return;
        queue.Enqueue(candidate);
    }

    private async Task AnswerAsync(Guid publisher, string offerSdp, Guid? negotiationId, CancellationToken ct)
    {
        IViewerPeerConnection peer;

        await _gate.WaitAsync(ct);
        try
        {
            if (_disposed) return;
            if (PublisherId != publisher) return;
            if (_expectedFallbackId is { } expected && negotiationId != expected) return;
            if (_expectedFallbackId is null && _negotiationId is { } active && negotiationId is { } received && received != active) return;
            if (_fallbackStarted && negotiationId == _expectedFallbackId)
            {
                _fallbackCancellation?.Cancel();
                _fallbackCancellation?.Dispose();
                _fallbackCancellation = new CancellationTokenSource();
            }
            _negotiationId = negotiationId;

            // A later offer is a renegotiation and must land on the same peer. While the new
            // remote description is being applied, trickled ICE stays queued rather than
            // racing addIceCandidate ahead of setRemoteDescription.
            peer = _peer ??= CreatePeer();
            _remoteDescriptionReady = false;
        }
        finally
        {
            _gate.Release();
        }

        string answer;
        try
        {
            answer = await peer.CreateAnswerAsync(offerSdp, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            var stage = e is ViewerNegotiationException negotiation
                ? negotiation.Stage
                : "createAnswer";
            await FailNegotiationAsync(peer, stage, e.Message);
            return;
        }

        if (_fallbackStarted && !_everConnected && negotiationId == _expectedFallbackId)
        {
            _ = WatchFallbackConnectionTimeoutAsync(_fallbackCancellation!.Token, peer);
        }

        try
        {
            await DrainPendingCandidatesAsync(publisher, peer, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            await FailNegotiationAsync(peer, "addIceCandidate", e.Message);
            return;
        }

        EmitDiagnostic("viewer.answer.send.begin", to: publisher);
        try
        {
            await signaling.SendAsync(SignalingMessageTypes.WebRtcAnswer, publisher,
                new { type = "answer", sdp = answer, negotiationId }, ct);
            EmitDiagnostic("viewer.answer.send.ok", to: publisher);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            EmitDiagnostic(
                "viewer.answer.send.failed",
                exceptionType: e.GetType().Name,
                message: e.Message,
                to: publisher);
            await FailNegotiationAsync(peer, "sendAnswer", e.Message);
        }
    }

    private async Task DrainPendingCandidatesAsync(
        Guid publisher,
        IViewerPeerConnection peer,
        CancellationToken ct)
    {
        while (true)
        {
            PendingIceCandidate? next = null;

            await _gate.WaitAsync(ct);
            try
            {
                if (_disposed || !ReferenceEquals(_peer, peer)) return;

                if (_pendingCandidates.TryGetValue(publisher, out var queue) && queue.Count > 0)
                {
                    next = queue.Dequeue();
                    if (queue.Count == 0) _pendingCandidates.Remove(publisher);
                }
                else
                {
                    // Set this under the same gate used by candidate receipt. From this point,
                    // a new candidate either observes ready=true and is applied directly, or it
                    // was already queued and therefore would have been drained above.
                    _remoteDescriptionReady = true;
                    return;
                }
            }
            finally
            {
                _gate.Release();
            }

            if (next.NegotiationId is { } candidateGeneration && candidateGeneration != _negotiationId) continue;
            await peer.AddIceCandidateAsync(
                next.Candidate,
                next.SdpMid,
                next.SdpMLineIndex,
                ct);
        }
    }

    private async Task FailNegotiationAsync(IViewerPeerConnection peer, string stage, string reason)
    {
        var dispose = false;

        await _gate.WaitAsync();
        try
        {
            if (ReferenceEquals(_peer, peer))
            {
                _peer = null;
                _lastPeerDiagnostic = null;
                TransportDiagnostics = null;
                _remoteDescriptionReady = false;
                _pendingCandidates.Clear();
                dispose = true;
            }
        }
        finally
        {
            _gate.Release();
        }

        if (dispose)
        {
            _fallbackCancellation?.Cancel();
            _fallbackCancellation?.Dispose();
            _fallbackCancellation = null;
            if (_fallbackStarted) EmitDiagnostic("viewer.relay_fallback.failed");
            pipeline.MarkFailed($"WebRTC negotiation failed at {stage}: {reason}");
            await peer.DisposeAsync();
        }
        NegotiationFailed?.Invoke($"WebRTC negotiation failed at {stage}: {reason}");
    }

    private IViewerPeerConnection CreatePeer()
    {
        _lastPeerDiagnostic = null;
        var peer = peers.Create(_fallbackStarted ? true : null);
        var peerGeneration = _negotiationId;

        peer.Diagnostic += entry =>
        {
            var enriched = entry with { From = entry.From ?? PublisherId };
            _lastPeerDiagnostic = enriched;
            Diagnostic?.Invoke(enriched);
        };
        peer.TransportDiagnosticsChanged += diagnostics =>
        {
            TransportDiagnostics = diagnostics;
            TransportDiagnosticsChanged?.Invoke(diagnostics);
        };
        peer.ConnectionStateChanged += connected => _ = OnConnectionStateChangedAsync(peer, connected);
        if (peer.TransportDiagnostics is { } existingTransport)
        {
            TransportDiagnostics = existingTransport;
            TransportDiagnosticsChanged?.Invoke(existingTransport);
        }

        peer.IceCandidateGathered += (candidate, mid, index) =>
        {
            if (PublisherId is not { } publisher) return;
            EmitDiagnostic("viewer.ice_candidate.send", to: publisher);
            _ = signaling.SendAsync(SignalingMessageTypes.WebRtcIceCandidate, publisher,
                new { candidate, sdpMid = mid, sdpMLineIndex = index, negotiationId = peerGeneration }, CancellationToken.None);
        };

        peer.VideoSampleReceived += pipeline.Submit;
        if (audioPipeline is not null)
            peer.AudioSampleReceived += audioPipeline.Push;

        if (!_keyFrameHooked)
        {
            pipeline.KeyFrameNeeded += OnKeyFrameNeeded;
            _keyFrameHooked = true;
        }

        return peer;
    }

    private async Task OnConnectionStateChangedAsync(IViewerPeerConnection peer, bool connected)
    {
        if (!ReferenceEquals(_peer, peer)) return;
        if (connected)
        {
            await _gate.WaitAsync();
            try
            {
                if (_disposed || !ReferenceEquals(_peer, peer)) return;
                _everConnected = true;
                _fallbackCancellation?.Cancel();
                if (_fallbackStarted) EmitDiagnostic("viewer.relay_fallback.completed");
            }
            finally { _gate.Release(); }
            return;
        }

        if (_everConnected || _fallbackStarted)
        {
            if (_fallbackStarted) await FailNegotiationAsync(peer, "relayConnectionFailed", "relay peer failed");
            return;
        }
        if (!string.Equals(TransportDiagnostics?.Path, "Direct", StringComparison.OrdinalIgnoreCase)) return;
        if (PublisherId is not { } publisher) return;

        IViewerPeerConnection? relayPeer = null;
        await _gate.WaitAsync();
        try
        {
            if (_disposed || _fallbackStarted || !ReferenceEquals(_peer, peer) || _everConnected) return;
            _fallbackStarted = true;
            _expectedFallbackId = Guid.NewGuid();
            _negotiationId = _expectedFallbackId;
            _remoteDescriptionReady = false;
            _pendingCandidates.Clear();
            _fallbackCancellation = new CancellationTokenSource();
            EmitDiagnostic("viewer.relay_fallback.started", to: publisher);
            relayPeer = CreatePeer();
            _peer = relayPeer;
        }
        finally { _gate.Release(); }

        if (relayPeer is null) return;
        await peer.DisposeAsync();
        var id = _expectedFallbackId!.Value;
        await _gate.WaitAsync();
        try
        {
            if (_disposed || !ReferenceEquals(_peer, relayPeer)) return;
        }
        finally { _gate.Release(); }

        try
        {
            await signaling.SendAsync(SignalingMessageTypes.WebRtcRenegotiate, publisher,
                new { reason = "direct_connection_failed", negotiationId = id, iceTransportPolicy = "relay" }, CancellationToken.None);
        }
        catch (Exception e)
        {
            await FailNegotiationAsync(relayPeer, "sendRenegotiationRequest", e.Message);
            return;
        }
        _ = WatchFallbackOfferTimeoutAsync(_fallbackCancellation!.Token);
    }

    private async Task WatchFallbackOfferTimeoutAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(15), _time, ct);
            EmitDiagnostic("viewer.relay_fallback.offer_timeout");
            if (_peer is { } peer) await FailNegotiationAsync(peer, "relayOfferTimeout", "timed out waiting for offer");
        }
        catch (OperationCanceledException) { }
    }

    private async Task WatchFallbackConnectionTimeoutAsync(CancellationToken ct, IViewerPeerConnection peer)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), _time, ct);
            EmitDiagnostic("viewer.relay_fallback.connection_timeout");
            await FailNegotiationAsync(peer, "relayConnectionTimeout", "relay connection timed out");
        }
        catch (OperationCanceledException) { }
    }

    private void EmitDiagnostic(
        string eventName,
        string? exceptionType = null,
        string? message = null,
        Guid? from = null,
        Guid? to = null)
    {
        var peer = _lastPeerDiagnostic;
        Diagnostic?.Invoke(new ViewerNegotiationDiagnosticEntry(
            DateTimeOffset.UtcNow,
            eventName,
            peer?.SignalingState ?? "unknown",
            peer?.IceGatheringState ?? "unknown",
            peer?.IceConnectionState ?? "unknown",
            peer?.ConnectionState ?? "unknown",
            peer?.SetDescriptionResult,
            exceptionType,
            message,
            from,
            to));
    }

    private void OnKeyFrameNeeded() => _peer?.RequestKeyFrame();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _statsDisposed, 1) == 0)
        {
            _statsCancellation.Cancel();
            await _statsTask;
            _statsCancellation.Dispose();
        }
        await _gate.WaitAsync();
        IViewerPeerConnection? peer;
        try
        {
            if (_disposed) return;
            _disposed = true;
            peer = _peer;
            _peer = null;
            _lastPeerDiagnostic = null;
            TransportDiagnostics = null;
            _remoteDescriptionReady = false;
            _pendingCandidates.Clear();
            _fallbackCancellation?.Cancel();
            _fallbackCancellation?.Dispose();
            _fallbackCancellation = null;
        }
        finally
        {
            _gate.Release();
        }

        if (_keyFrameHooked) pipeline.KeyFrameNeeded -= OnKeyFrameNeeded;
        if (peer is not null) await peer.DisposeAsync();
    }

    private static string? ReadString(SignalingEnvelope envelope, string name) =>
        envelope.Payload is { } payload
        && payload.ValueKind == JsonValueKind.Object
        && payload.TryGetProperty(name, out var element)
        && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private static Guid? ReadGuid(SignalingEnvelope envelope, string name) =>
        envelope.Payload is { } payload
        && payload.ValueKind == JsonValueKind.Object
        && payload.TryGetProperty(name, out var element)
        && element.ValueKind == JsonValueKind.String
        && Guid.TryParse(element.GetString(), out var id)
            ? id
            : null;

    private static int? ReadIndex(SignalingEnvelope envelope) =>
        envelope.Payload is { } payload
        && payload.ValueKind == JsonValueKind.Object
        && payload.TryGetProperty("sdpMLineIndex", out var element)
        && element.ValueKind == JsonValueKind.Number
            ? element.GetInt32()
            : null;

    private sealed record PendingIceCandidate(string Candidate, string? SdpMid, int? SdpMLineIndex, Guid? NegotiationId);
}
