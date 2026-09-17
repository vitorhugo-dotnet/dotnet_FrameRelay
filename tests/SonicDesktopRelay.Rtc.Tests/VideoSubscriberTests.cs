using Microsoft.Extensions.Time.Testing;
using SonicDesktopRelay.Media;
using SonicDesktopRelay.Signaling;
using Xunit;

namespace SonicDesktopRelay.Rtc.Tests;

public sealed class VideoSubscriberTests
{
    private static readonly Guid Publisher = Guid.Parse("6f9619ff-8b86-d011-b42d-00cf4fc96401");
    private static readonly Guid Stranger = Guid.Parse("6f9619ff-8b86-d011-b42d-00cf4fc96402");
    private static readonly DateTimeOffset Start = new(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Publisher_ready_learns_the_publisher_and_answers_viewer_ready()
    {
        var harness = new Harness();

        await harness.Subscriber.HandleAsync(Frame(SignalingMessageTypes.PublisherReady, Publisher, "{}"),
            CancellationToken.None);

        Assert.Equal(Publisher, harness.Subscriber.PublisherId);
        var sent = Assert.Single(harness.Signaling.Sent);
        Assert.Equal(SignalingMessageTypes.ViewerReady, sent.Type);
        Assert.Equal(Publisher, sent.To);
    }

    [Fact]
    public async Task The_publisher_identity_comes_from_the_authenticated_from_field()
    {
        var harness = new Harness();

        // The payload is attacker-controlled; `from` is the server's word. Only `from` counts.
        await harness.Subscriber.HandleAsync(
            Frame(SignalingMessageTypes.PublisherReady, Publisher, $$"""{"participantId":"{{Stranger}}"}"""),
            CancellationToken.None);

        Assert.Equal(Publisher, harness.Subscriber.PublisherId);
    }

    [Fact]
    public async Task An_offer_produces_an_answer_addressed_to_the_publisher()
    {
        var harness = new Harness();
        await harness.ReadyAsync();
        harness.Signaling.Sent.Clear();

        await harness.Subscriber.HandleAsync(
            Frame(SignalingMessageTypes.WebRtcOffer, Publisher, """{"type":"offer","sdp":"offer-sdp"}"""),
            CancellationToken.None);

        var sent = Assert.Single(harness.Signaling.Sent);
        Assert.Equal(SignalingMessageTypes.WebRtcAnswer, sent.Type);
        Assert.Equal(Publisher, sent.To);
        Assert.Equal("offer-sdp", harness.Peers.Created!.ReceivedOffer);
    }

    [Fact]
    public async Task An_offer_from_someone_who_is_not_the_publisher_is_ignored()
    {
        var harness = new Harness();
        await harness.ReadyAsync();
        harness.Signaling.Sent.Clear();

        await harness.Subscriber.HandleAsync(
            Frame(SignalingMessageTypes.WebRtcOffer, Stranger, """{"type":"offer","sdp":"offer-sdp"}"""),
            CancellationToken.None);

        Assert.Empty(harness.Signaling.Sent);
    }

    [Fact]
    public async Task An_ice_candidate_from_the_publisher_reaches_the_peer()
    {
        var harness = new Harness();
        await harness.ReadyAsync();
        await harness.OfferAsync();

        await harness.Subscriber.HandleAsync(
            Frame(SignalingMessageTypes.WebRtcIceCandidate, Publisher,
                """{"candidate":"candidate:1","sdpMid":"0","sdpMLineIndex":0}"""),
            CancellationToken.None);

        Assert.Single(harness.Peers.Created!.RemoteCandidates);
    }

    [Fact]
    public async Task A_gathered_candidate_is_signalled_to_the_publisher()
    {
        var harness = new Harness();
        await harness.ReadyAsync();
        await harness.OfferAsync();
        harness.Signaling.Sent.Clear();

        harness.Peers.Created!.GatherCandidate("candidate:2", "0", 0);

        var sent = Assert.Single(harness.Signaling.Sent);
        Assert.Equal(SignalingMessageTypes.WebRtcIceCandidate, sent.Type);
        Assert.Equal(Publisher, sent.To);
    }

    [Fact]
    public async Task A_received_video_sample_is_decoded_and_rendered()
    {
        var harness = new Harness();
        await harness.ReadyAsync();
        await harness.OfferAsync();
        var frames = 0;
        harness.Pipeline.FrameDecoded += _ => frames++;

        harness.Peers.Created!.ReceiveVideo(new EncodedVideoSample(new byte[8], TimeSpan.Zero, true, 1920, 1080));

        Assert.Equal(1, frames);
    }

    [Fact]
    public async Task A_stalled_pipeline_sends_a_keyframe_request_to_the_peer()
    {
        var harness = new Harness();
        await harness.ReadyAsync();
        await harness.OfferAsync();

        harness.Pipeline.RaiseKeyFrameNeeded();

        Assert.Equal(1, harness.Peers.Created!.KeyFrameRequests);
    }

    [Fact]
    public async Task A_renegotiation_offer_replaces_the_previous_description_on_the_same_peer()
    {
        var harness = new Harness();
        await harness.ReadyAsync();
        await harness.OfferAsync();
        var first = harness.Peers.Created;

        await harness.Subscriber.HandleAsync(
            Frame(SignalingMessageTypes.WebRtcOffer, Publisher, """{"type":"offer","sdp":"second-sdp"}"""),
            CancellationToken.None);

        // The publisher renegotiates when the monitor resolution changes; that must not build
        // a second peer connection and leak the first.
        Assert.Same(first, harness.Peers.Created);
        Assert.Equal("second-sdp", harness.Peers.Created!.ReceivedOffer);
    }

    [Fact]
    public async Task An_offer_that_arrives_before_publisher_ready_still_establishes_the_publisher()
    {
        var harness = new Harness();

        // The publishing half of this app sends webrtc.offer straight off session.joined and
        // never sends publisher.ready, so a viewer that waited for it would never connect to
        // its own product. The authenticated `from` of the first offer is just as trustworthy.
        await harness.OfferAsync();

        Assert.Equal(Publisher, harness.Subscriber.PublisherId);
        var sent = Assert.Single(harness.Signaling.Sent);
        Assert.Equal(SignalingMessageTypes.WebRtcAnswer, sent.Type);
    }

    private static SignalingEnvelope Frame(string type, Guid from, string payloadJson) =>
        new(type, null, null, from, null, null,
            System.Text.Json.JsonDocument.Parse(payloadJson).RootElement.Clone());

    private sealed class Harness
    {
        public Harness()
        {
            Pipeline = new WatchPipelineDriver();
            Peers = new FakeViewerPeerFactory();
            Signaling = new FakeSignaling();
            Subscriber = new VideoSubscriber(Pipeline.Pipeline, Peers, Signaling);
        }

        public WatchPipelineDriver Pipeline { get; }

        public FakeViewerPeerFactory Peers { get; }

        public FakeSignaling Signaling { get; }

        public VideoSubscriber Subscriber { get; }

        public Task ReadyAsync() => Subscriber.HandleAsync(
            Frame(SignalingMessageTypes.PublisherReady, Publisher, "{}"), CancellationToken.None);

        public Task OfferAsync() => Subscriber.HandleAsync(
            Frame(SignalingMessageTypes.WebRtcOffer, Publisher, """{"type":"offer","sdp":"offer-sdp"}"""),
            CancellationToken.None);
    }

    /// <summary>
    /// Drives a real <see cref="ScreenWatchPipeline"/> over a fake clock. A stall is produced
    /// the way the real one is — the watchdog fires with no frame in the window — rather than
    /// by adding a test-only hook to production code.
    /// </summary>
    private sealed class WatchPipelineDriver
    {
        private readonly FakeTimeProvider _time = new(Start);

        public WatchPipelineDriver() => Pipeline = new ScreenWatchPipeline(new FakeDecoder(), _time);

        public ScreenWatchPipeline Pipeline { get; }

        public event Action<VideoFrame>? FrameDecoded
        {
            add => Pipeline.FrameDecoded += value;
            remove => Pipeline.FrameDecoded -= value;
        }

        public void RaiseKeyFrameNeeded()
        {
            Pipeline.Submit(new EncodedVideoSample(new byte[8], TimeSpan.Zero, true, 1920, 1080));
            _time.Advance(TimeSpan.FromSeconds(5));
            Pipeline.CheckForStall();
        }
    }

    private sealed class FakeDecoder : IVideoDecoder
    {
        public string Name => "fake";

        public VideoFrame? Decode(EncodedVideoSample sample) =>
            new(sample.Width, sample.Height, new byte[16], sample.Timestamp);

        public void Dispose()
        {
        }
    }

    private sealed class FakeViewerPeerFactory : IViewerPeerConnectionFactory
    {
        public FakeViewerPeer? Created { get; private set; }

        public int CreateCalls { get; private set; }

        public IViewerPeerConnection Create()
        {
            CreateCalls++;
            Created = new FakeViewerPeer();
            return Created;
        }
    }

    private sealed class FakeViewerPeer : IViewerPeerConnection
    {
        public string? ReceivedOffer { get; private set; }

        public List<string> RemoteCandidates { get; } = [];

        public int KeyFrameRequests { get; private set; }

        public bool Disposed { get; private set; }

        public event Action<string, string?, int?>? IceCandidateGathered;

        public event Action<EncodedVideoSample>? VideoSampleReceived;

        public event Action<EncodedAudioSample>? AudioSampleReceived
        {
            add { }
            remove { }
        }

        public Task<string> CreateAnswerAsync(string offerSdp, CancellationToken ct)
        {
            ReceivedOffer = offerSdp;
            return Task.FromResult("answer-sdp");
        }

        public Task AddIceCandidateAsync(string candidate, string? sdpMid, int? sdpMLineIndex, CancellationToken ct)
        {
            RemoteCandidates.Add(candidate);
            return Task.CompletedTask;
        }

        public void RequestKeyFrame() => KeyFrameRequests++;

        public void GatherCandidate(string candidate, string? mid, int? index) =>
            IceCandidateGathered?.Invoke(candidate, mid, index);

        public void ReceiveVideo(EncodedVideoSample sample) => VideoSampleReceived?.Invoke(sample);

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeSignaling : ISignalingConnection
    {
        public List<(string Type, Guid? To, object? Payload)> Sent { get; } = [];

        public SignalingState State => SignalingState.Connected;

        public event Action<SignalingEnvelope>? FrameReceived
        {
            add { }
            remove { }
        }

        public event Action<SignalingState>? StateChanged
        {
            add { }
            remove { }
        }

        public Task StartAsync(Guid sessionId, CancellationToken ct) => Task.CompletedTask;

        public Task SendAsync(string type, Guid? to, object? payload, CancellationToken ct)
        {
            Sent.Add((type, to, payload));
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
