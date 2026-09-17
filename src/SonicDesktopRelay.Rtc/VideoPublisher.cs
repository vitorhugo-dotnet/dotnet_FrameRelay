using System.Collections.Concurrent;
using System.Text.Json;
using SonicDesktopRelay.Media;
using SonicDesktopRelay.Signaling;

namespace SonicDesktopRelay.Rtc;

/// <summary>
/// Owns one peer connection per viewer and feeds all of them from a single encode. Everything
/// that scales with viewer count lives here; everything that must not is in the pipeline.
/// </summary>
public sealed class VideoPublisher(
    ScreenPublishPipeline pipeline,
    IPeerConnectionFactory peers,
    ISignalingConnection signaling) : IAsyncDisposable
{
    /// <summary>
    /// Below this, loss is ordinary internet weather and reacting to it would make the picture
    /// worse for everyone over nothing.
    /// </summary>
    private const double PoorReceptionLossRatio = 0.05;

    private readonly ConcurrentDictionary<Guid, IPeerConnection> _peers = new();
    private bool _subscribed;

    public int PeerCount => _peers.Count;

    public async Task AddViewerAsync(Guid participantId, CancellationToken ct)
    {
        if (_peers.ContainsKey(participantId)) return;

        var peer = peers.Create(participantId);
        if (!_peers.TryAdd(participantId, peer))
        {
            await peer.DisposeAsync();
            return;
        }

        peer.IceCandidateGathered += (candidate, mid, index) =>
            _ = signaling.SendAsync(SignalingMessageTypes.WebRtcIceCandidate, participantId,
                new { candidate, sdpMid = mid, sdpMLineIndex = index }, CancellationToken.None);
        peer.KeyFrameRequested += pipeline.RequestKeyFrame;
        peer.PacketLossReported += loss =>
        {
            if (loss <= 0) return;

            // Any reported loss can break an inter-frame decode chain, so recover immediately.
            // Only sustained/high loss is allowed to reduce the global stream quality.
            if (loss >= PoorReceptionLossRatio) pipeline.ReportPoorReception();
            else pipeline.RequestKeyFrame();
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
    // is nothing to send, and an unsubscribed pipeline is the cheap idle state.
    private void EnsureSubscribed()
    {
        if (_subscribed) return;
        pipeline.SampleEncoded += Broadcast;
        _subscribed = true;
    }

    private void Broadcast(EncodedVideoSample sample)
    {
        foreach (var peer in _peers.Values) peer.SendVideo(sample);
    }

    public async ValueTask DisposeAsync()
    {
        if (_subscribed) pipeline.SampleEncoded -= Broadcast;
        foreach (var participantId in _peers.Keys) await RemoveViewerAsync(participantId);
    }
}
