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
            // Recovery and congestion are different signals. Any actual loss can justify one
            // coalesced clean point; every RTCP sample, including zero loss, feeds the separate
            // hysteretic quality policy so degraded sessions can recover later.
            if (loss > 0)
                pipeline.RequestKeyFrame(KeyFrameRequestReason.PacketLoss);

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
        pipeline.RemoveReceptionSource(participantId);
        _transportDiagnostics.TryRemove(participantId, out _);
        if (_videoQueues.TryRemove(participantId, out var queue))
            await queue.DisposeAsync();
        if (!_peers.TryRemove(participantId, out var peer)) return;
        await peer.DisposeAsync();
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
        }
    }

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
