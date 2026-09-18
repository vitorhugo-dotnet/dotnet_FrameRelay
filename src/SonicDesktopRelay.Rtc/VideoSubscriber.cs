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
    private const int PendingCandidatesPerParticipant = 64;
    private const int PendingCandidateParticipants = 8;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, Queue<PendingIceCandidate>> _pendingCandidates = [];

    private IViewerPeerConnection? _peer;
    private bool _remoteDescriptionReady;
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

    /// <summary>
    /// Raised when offer/answer negotiation has definitively failed. The message contains only
    /// an actionable stage/reason; SDP, candidates and credentials are deliberately excluded.
    /// </summary>
    public event Action<string>? NegotiationFailed;

    public async Task HandleAsync(SignalingEnvelope envelope, CancellationToken ct)
    {
        if (envelope.From is not { } from) return;

        switch (envelope.Type)
        {
            case SignalingMessageTypes.PublisherReady:
                await LearnPublisherAsync(from, ct);
                if (PublisherId != from) return;
                await signaling.SendAsync(SignalingMessageTypes.ViewerReady, from, new { }, ct);
                return;

            case SignalingMessageTypes.WebRtcOffer:
                if (ReadString(envelope, "sdp") is not { } offerSdp) return;
                await LearnPublisherAsync(from, ct);
                if (PublisherId != from) return;
                await AnswerAsync(from, offerSdp, ct);
                return;

            case SignalingMessageTypes.WebRtcIceCandidate:
                if (ReadString(envelope, "candidate") is not { } candidate) return;
                await ReceiveIceCandidateAsync(
                    from,
                    new PendingIceCandidate(candidate, ReadString(envelope, "sdpMid"), ReadIndex(envelope)),
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

    private async Task AnswerAsync(Guid publisher, string offerSdp, CancellationToken ct)
    {
        IViewerPeerConnection peer;

        await _gate.WaitAsync(ct);
        try
        {
            if (_disposed) return;
            if (PublisherId != publisher) return;

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
            await FailNegotiationAsync(peer, "createAnswer", e.Message);
            return;
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

        try
        {
            await signaling.SendAsync(SignalingMessageTypes.WebRtcAnswer, publisher,
                new { type = "answer", sdp = answer }, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
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
                _remoteDescriptionReady = false;
                _pendingCandidates.Clear();
                dispose = true;
            }
        }
        finally
        {
            _gate.Release();
        }

        if (dispose) await peer.DisposeAsync();
        NegotiationFailed?.Invoke($"WebRTC negotiation failed at {stage}: {reason}");
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
            _remoteDescriptionReady = false;
            _pendingCandidates.Clear();
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

    private sealed record PendingIceCandidate(string Candidate, string? SdpMid, int? SdpMLineIndex);
}
