using System.Collections.Concurrent;
using System.Text.Json;
using SonicDesktopRelay.Media;
using SonicDesktopRelay.Signaling;

namespace SonicDesktopRelay.Rtc;

/// <summary>
/// Owns one peer connection per viewer and feeds all of them from a single video encode and,
/// when available, a single audio encode. Everything that scales with viewer count lives here;
/// capture and encoding remain session-scoped in the media pipelines.
/// </summary>
public sealed class VideoPublisher(
    ScreenPublishPipeline pipeline,
    IPeerConnectionFactory peers,
    ISignalingConnection signaling,
    AudioPublishPipeline? audioPipeline = null,
    TimeProvider? time = null) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, IPeerConnection> _peers = new();
    private readonly ConcurrentDictionary<Guid, VideoSampleSendQueue> _videoQueues = new();
    private readonly ConcurrentDictionary<Guid, RtcTransportDiagnostics> _transportDiagnostics = new();
    private readonly object _receiverStatsGate = new();
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private long _lastVideoSendDurationTicks;
    private bool _subscribed;

    public int PeerCount => _peers.Count;

    public IReadOnlyDictionary<Guid, RtcTransportDiagnostics> TransportDiagnostics => _transportDiagnostics;

    public TimeSpan? LastVideoSendDuration
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastVideoSendDurationTicks);
            return ticks <= 0 ? null : TimeSpan.FromTicks(ticks);
        }
    }

    public long DroppedVideoSamples => _videoQueues.Values.Sum(x => x.DroppedSamples);

    public long VideoSendFailures => _videoQueues.Values.Sum(x => x.SendFailures);

    public int PendingVideoSamples => _videoQueues.Values.Sum(x => x.PendingSamples);

    public int ViewersAwaitingKeyFrame => _videoQueues.Values.Count(x => x.AwaitingKeyFrame);

    public event Action<Guid, RtcTransportDiagnostics>? TransportDiagnosticsChanged;

    public async Task AddViewerAsync(Guid participantId, CancellationToken ct)
    {
        if (_peers.ContainsKey(participantId)) return;

        var peer = peers.Create(participantId);
        if (!_peers.TryAdd(participantId, peer))
        {
            await peer.DisposeAsync();
            return;
        }

        var queue = new VideoSampleSendQueue(
            peer,
            duration => Interlocked.Exchange(ref _lastVideoSendDurationTicks, duration.Ticks),
            () => pipeline.RequestKeyFrame(KeyFrameRequestReason.PacketLoss),
            _time);
        _videoQueues[participantId] = queue;

        peer.IceCandidateGathered += (candidate, mid, index) =>
            _ = signaling.SendAsync(SignalingMessageTypes.WebRtcIceCandidate, participantId,
                new { candidate, sdpMid = mid, sdpMLineIndex = index }, CancellationToken.None);
        peer.KeyFrameRequested += pipeline.RequestKeyFrame;
        peer.PacketLossReported += loss =>
        {
            pipeline.ReportReception(participantId, loss);
        };
        peer.TransportDiagnosticsChanged += diagnostics =>
        {
            if (!_peers.ContainsKey(participantId)) return;
            _transportDiagnostics[participantId] = diagnostics;
            TransportDiagnosticsChanged?.Invoke(participantId, diagnostics);
        };

        EnsureSubscribed();

        // publisher.ready first, then the offer — the order dotnet_SonicRelay/docs/protocol.md
        // specifies. It is how a viewer learns which participant is the publisher, from the
        // server-authenticated `from` rather than from anything a peer claims about itself.
        // Skipping it happens to work with our own viewer, which also accepts the first offer,
        // but it would silently break any client written against the documented contract.
        await signaling.SendAsync(SignalingMessageTypes.PublisherReady, participantId, new { }, ct);

        var offer = await peer.CreateOfferAsync(ct);
        await signaling.SendAsync(SignalingMessageTypes.WebRtcOffer, participantId,
            new { type = "offer", sdp = offer }, ct);
    }

    public async Task RemoveViewerAsync(Guid participantId)
    {
        IPeerConnection? peer;
        lock (_receiverStatsGate)
        {
            _peers.TryRemove(participantId, out peer);
            pipeline.RemoveReceptionSource(participantId);
        }
        _transportDiagnostics.TryRemove(participantId, out _);
        if (_videoQueues.TryRemove(participantId, out var queue))
            await queue.DisposeAsync();
        if (peer is not null) await peer.DisposeAsync();
    }

    public async Task HandleAsync(SignalingEnvelope envelope, CancellationToken ct)
    {
        if (envelope.From is not { } from) return;
        if (!_peers.TryGetValue(from, out var peer)) return;
        if (envelope.Payload is not { } payload) return;

        switch (envelope.Type)
        {
            case SignalingMessageTypes.WebRtcAnswer:
                if (payload.TryGetProperty("sdp", out var sdp) && sdp.GetString() is { } sdpText)
                    await peer.ApplyAnswerAsync(sdpText, ct);
                break;

            case SignalingMessageTypes.WebRtcIceCandidate:
                if (payload.TryGetProperty("candidate", out var candidate)
                    && candidate.GetString() is { } candidateText)
                {
                    await peer.AddIceCandidateAsync(
                        candidateText,
                        payload.TryGetProperty("sdpMid", out var mid) ? mid.GetString() : null,
                        payload.TryGetProperty("sdpMLineIndex", out var index)
                            && index.ValueKind == JsonValueKind.Number
                                ? index.GetInt32()
                                : null,
                        ct);
                }

                break;

            case SignalingMessageTypes.VideoReceiverStats:
                if (!TryReadReceiverStats(payload, out var stats)) break;
                lock (_receiverStatsGate)
                {
                    if (_peers.ContainsKey(from))
                        pipeline.ReportReceiverStats(from, stats);
                }
                break;
        }
    }

    private static bool TryReadReceiverStats(JsonElement payload, out VideoReceiverStats stats)
    {
        stats = null!;
        if (payload.ValueKind != JsonValueKind.Object
            || !TryInt(payload, "version", out var version) || version != 1
            || !TryLong(payload, "intervalMilliseconds", out var interval) || interval is < 1000 or > 5000
            || !TryLong(payload, "rtpPacketsReceived", out var received) || received is < 0 or > 10_000_000
            || !TryLong(payload, "rtpPacketsLost", out var lost) || lost is < 0 or > 10_000_000
            || received + lost > 10_000_000
            || !TryLong(payload, "accessUnitsReceived", out var units) || units is < 0 or > 10_000_000
            || !TryLong(payload, "incompleteAccessUnits", out var incomplete)
            || incomplete < 0 || incomplete > 10_000_000 || incomplete > units
            || !TryLong(payload, "decodedFrames", out var decoded) || decoded is < 0 or > 10_000_000
            || !payload.TryGetProperty("targetFramesPerSecond", out var fpsElement)
            || fpsElement.ValueKind != JsonValueKind.Number || !fpsElement.TryGetDouble(out var fps)
            || !double.IsFinite(fps) || fps is < 1 or > 60)
            return false;

        stats = new VideoReceiverStats(version, interval, received, lost, units, incomplete, decoded, fps);
        return true;
    }

    private static bool TryLong(JsonElement payload, string name, out long value) =>
        payload.TryGetProperty(name, out var element)
        && element.ValueKind == JsonValueKind.Number
        && element.TryGetInt64(out value) || SetDefault(out value);

    private static bool TryInt(JsonElement payload, string name, out int value) =>
        payload.TryGetProperty(name, out var element)
        && element.ValueKind == JsonValueKind.Number
        && element.TryGetInt32(out value) || SetDefault(out value);

    private static bool SetDefault(out long value) { value = 0; return false; }
    private static bool SetDefault(out int value) { value = 0; return false; }

    // Subscribed on the first viewer rather than at construction: with nobody watching there
    // is nothing to send, and unsubscribed media pipelines are the cheap idle state.
    private void EnsureSubscribed()
    {
        if (_subscribed) return;
        pipeline.SampleEncoded += BroadcastVideo;
        if (audioPipeline is not null) audioPipeline.SampleEncoded += BroadcastAudio;
        _subscribed = true;
    }

    private void BroadcastVideo(EncodedVideoSample sample)
    {
        foreach (var queue in _videoQueues.Values) queue.Enqueue(sample);
    }

    private void BroadcastAudio(EncodedAudioSample sample)
    {
        foreach (var peer in _peers.Values) peer.SendAudio(sample);
    }

    public async ValueTask DisposeAsync()
    {
        if (_subscribed)
        {
            pipeline.SampleEncoded -= BroadcastVideo;
            if (audioPipeline is not null) audioPipeline.SampleEncoded -= BroadcastAudio;
            _subscribed = false;
        }

        foreach (var participantId in _peers.Keys) await RemoveViewerAsync(participantId);
    }
}
