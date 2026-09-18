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
    ISignalingConnection signaling) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IViewerPeerConnection? _peer;
    private bool _keyFrameHooked;
    private bool _disposed;

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

    public async Task HandleAsync(SignalingEnvelope envelope, CancellationToken ct)
    {
        if (envelope.From is not { } from) return;

        switch (envelope.Type)
        {
            case SignalingMessageTypes.PublisherReady:
                PublisherId = from;
                await signaling.SendAsync(SignalingMessageTypes.ViewerReady, from, new { }, ct);
                return;

            case SignalingMessageTypes.WebRtcOffer:
                // The publishing half of this app sends webrtc.offer straight off
                // session.joined and never sends publisher.ready, so treating the first
                // offer's authenticated sender as the publisher is what makes the two halves
                // meet. Once known, a stranger's offer is ignored.
                if (PublisherId is null) PublisherId = from;
                else if (PublisherId != from) return;

                if (ReadString(envelope, "sdp") is not { } offerSdp) return;
                await AnswerAsync(from, offerSdp, ct);
                return;

            case SignalingMessageTypes.WebRtcIceCandidate:
                if (PublisherId != from) return;
                if (_peer is not { } peer) return;
                if (ReadString(envelope, "candidate") is not { } candidate) return;

                await peer.AddIceCandidateAsync(
                    candidate,
                    ReadString(envelope, "sdpMid"),
                    ReadIndex(envelope),
                    ct);
                return;
        }
    }

    private async Task AnswerAsync(Guid publisher, string offerSdp, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        IViewerPeerConnection peer;
        try
        {
            if (_disposed) return;
            // A later offer is a renegotiation — the publisher does one when the monitor
            // resolution changes — and must land on the same peer. Building a second one
            // would leak the first and restart ICE for nothing.
            peer = _peer ??= CreatePeer();
        }
        finally
        {
            _gate.Release();
        }

        var answer = await peer.CreateAnswerAsync(offerSdp, ct);
        await signaling.SendAsync(SignalingMessageTypes.WebRtcAnswer, publisher,
            new { type = "answer", sdp = answer }, ct);
    }

    private IViewerPeerConnection CreatePeer()
    {
        var peer = peers.Create();

        peer.IceCandidateGathered += (candidate, mid, index) =>
        {
            if (PublisherId is not { } publisher) return;
            _ = signaling.SendAsync(SignalingMessageTypes.WebRtcIceCandidate, publisher,
                new { candidate, sdpMid = mid, sdpMLineIndex = index }, CancellationToken.None);
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

    private void OnKeyFrameNeeded() => _peer?.RequestKeyFrame();

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        IViewerPeerConnection? peer;
        try
        {
            if (_disposed) return;
            _disposed = true;
            peer = _peer;
            _peer = null;
        }
        finally
        {
            _gate.Release();
        }

        if (_keyFrameHooked) pipeline.KeyFrameNeeded -= OnKeyFrameNeeded;
        if (peer is not null) await peer.DisposeAsync();
        _gate.Dispose();
    }

    private static string? ReadString(SignalingEnvelope envelope, string name) =>
        envelope.Payload is { } payload
        && payload.ValueKind == JsonValueKind.Object
        && payload.TryGetProperty(name, out var element)
        && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private static int? ReadIndex(SignalingEnvelope envelope) =>
        envelope.Payload is { } payload
        && payload.ValueKind == JsonValueKind.Object
        && payload.TryGetProperty("sdpMLineIndex", out var element)
        && element.ValueKind == JsonValueKind.Number
            ? element.GetInt32()
            : null;
}
