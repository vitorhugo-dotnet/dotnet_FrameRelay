using SonicDesktopRelay.Media;
using SonicDesktopRelay.Rtc;
using SonicDesktopRelay.Signaling;
using Xunit;

namespace SonicDesktopRelay.Rtc.Tests;

public sealed class VideoPublisherTests
{
    private static readonly Guid ViewerA = Guid.Parse("6f9619ff-8b86-d011-b42d-00cf4fc96401");
    private static readonly Guid ViewerB = Guid.Parse("6f9619ff-8b86-d011-b42d-00cf4fc96402");
    private static readonly MonitorInfo Monitor = new("\\\\.\\DISPLAY1", "Primary", 1920, 1080, true);

    [Fact]
    public async Task Adding_a_viewer_creates_one_peer_and_sends_it_an_offer()
    {
        var harness = await Harness.StartedAsync();

        await harness.Publisher.AddViewerAsync(ViewerA, CancellationToken.None);

        Assert.Equal(1, harness.Publisher.PeerCount);
        Assert.Equal(
            [SignalingMessageTypes.PublisherReady, SignalingMessageTypes.WebRtcOffer],
            harness.Signaling.Sent.Select(x => x.Type).ToArray());
        Assert.All(harness.Signaling.Sent, sent => Assert.Equal(ViewerA, sent.To));
    }

    [Fact]
    public async Task Every_viewer_receives_the_same_encoded_sample()
    {
        var harness = await Harness.StartedAsync();
        await harness.Publisher.AddViewerAsync(ViewerA, CancellationToken.None);
        await harness.Publisher.AddViewerAsync(ViewerB, CancellationToken.None);

        harness.Capture.Emit();

        Assert.Equal(1, harness.Encoder.EncodeCalls);
        Assert.All(harness.Peers.Created, peer => Assert.Single(peer.SentSamples));
    }

    [Fact]
    public async Task A_removed_viewer_stops_receiving_and_is_disposed()
    {
        var harness = await Harness.StartedAsync();
        await harness.Publisher.AddViewerAsync(ViewerA, CancellationToken.None);
        var peer = harness.Peers.Created[0];

        await harness.Publisher.RemoveViewerAsync(ViewerA);
        harness.Capture.Emit();

        Assert.Equal(0, harness.Publisher.PeerCount);
        Assert.True(peer.Disposed);
        Assert.Empty(peer.SentSamples);
    }

    [Fact]
    public async Task An_answer_is_applied_to_the_peer_that_sent_it()
    {
        var harness = await Harness.StartedAsync();
        await harness.Publisher.AddViewerAsync(ViewerA, CancellationToken.None);

        await harness.Publisher.HandleAsync(
            Frame(SignalingMessageTypes.WebRtcAnswer, ViewerA, """{"type":"answer","sdp":"the-sdp"}"""),
            CancellationToken.None);

        Assert.Equal("the-sdp", harness.Peers.Created[0].AppliedAnswer);
    }

    [Fact]
    public async Task An_answer_from_an_unknown_participant_is_ignored()
    {
        var harness = await Harness.StartedAsync();
        await harness.Publisher.AddViewerAsync(ViewerA, CancellationToken.None);

        await harness.Publisher.HandleAsync(
            Frame(SignalingMessageTypes.WebRtcAnswer, ViewerB, """{"type":"answer","sdp":"the-sdp"}"""),
            CancellationToken.None);

        Assert.Null(harness.Peers.Created[0].AppliedAnswer);
    }

    [Fact]
    public async Task An_ice_candidate_is_routed_to_its_peer()
    {
        var harness = await Harness.StartedAsync();
        await harness.Publisher.AddViewerAsync(ViewerA, CancellationToken.None);

        await harness.Publisher.HandleAsync(
            Frame(SignalingMessageTypes.WebRtcIceCandidate, ViewerA,
                """{"candidate":"candidate:1","sdpMid":"0","sdpMLineIndex":0}"""),
            CancellationToken.None);

        Assert.Single(harness.Peers.Created[0].RemoteCandidates);
    }

    [Fact]
    public async Task A_gathered_candidate_is_signalled_to_that_viewer()
    {
        var harness = await Harness.StartedAsync();
        await harness.Publisher.AddViewerAsync(ViewerA, CancellationToken.None);
        harness.Signaling.Sent.Clear();

        harness.Peers.Created[0].GatherCandidate("candidate:2", "0", 0);

        var sent = Assert.Single(harness.Signaling.Sent);
        Assert.Equal(SignalingMessageTypes.WebRtcIceCandidate, sent.Type);
        Assert.Equal(ViewerA, sent.To);
    }

    [Fact]
    public async Task A_keyframe_request_from_any_viewer_reaches_the_single_encoder()
    {
        var harness = await Harness.StartedAsync();
        await harness.Publisher.AddViewerAsync(ViewerA, CancellationToken.None);
        await harness.Publisher.AddViewerAsync(ViewerB, CancellationToken.None);

        harness.Peers.Created[1].RequestKeyFrame();

        Assert.Equal(1, harness.Encoder.KeyFrameRequests);
    }

    [Fact]
    public async Task Sustained_loss_on_one_viewer_degrades_quality_for_the_session()
    {
        var harness = await Harness.StartedAsync();
        await harness.Publisher.AddViewerAsync(ViewerA, CancellationToken.None);
        await harness.Publisher.AddViewerAsync(ViewerB, CancellationToken.None);

        harness.Peers.Created[0].ReportPacketLoss(0.15);

        Assert.Equal(720, harness.Pipeline.Quality.MaxHeight);
    }

    [Fact]
    public async Task Mild_loss_does_not_degrade_quality()
    {
        var harness = await Harness.StartedAsync();
        await harness.Publisher.AddViewerAsync(ViewerA, CancellationToken.None);

        harness.Peers.Created[0].ReportPacketLoss(0.01);

        Assert.Equal(1080, harness.Pipeline.Quality.MaxHeight);
    }

    [Fact]
    public async Task Adding_the_same_viewer_twice_does_not_create_a_second_peer()
    {
        var harness = await Harness.StartedAsync();

        await harness.Publisher.AddViewerAsync(ViewerA, CancellationToken.None);
        await harness.Publisher.AddViewerAsync(ViewerA, CancellationToken.None);

        Assert.Equal(1, harness.Publisher.PeerCount);
    }

    private static SignalingEnvelope Frame(string type, Guid from, string payloadJson) =>
        new(type, null, null, from, null, null,
            System.Text.Json.JsonDocument.Parse(payloadJson).RootElement.Clone());

    private sealed class Harness
    {
        public required FakeCapture Capture { get; init; }
        public required FakeEncoder Encoder { get; init; }
        public required ScreenPublishPipeline Pipeline { get; init; }
        public required FakePeerFactory Peers { get; init; }
        public required FakeSignaling Signaling { get; init; }
        public required VideoPublisher Publisher { get; init; }

        public static async Task<Harness> StartedAsync()
        {
            var capture = new FakeCapture();
            var encoder = new FakeEncoder();
            var pipeline = new ScreenPublishPipeline(capture, encoder);
            var peers = new FakePeerFactory();
            var signaling = new FakeSignaling();
            var publisher = new VideoPublisher(pipeline, peers, signaling);
            await pipeline.StartAsync(Monitor, CancellationToken.None);
            return new Harness
            {
                Capture = capture,
                Encoder = encoder,
                Pipeline = pipeline,
                Peers = peers,
                Signaling = signaling,
                Publisher = publisher
            };
        }
    }

    private sealed class FakeCapture : IScreenCaptureSource
    {
        public MonitorInfo Monitor { get; private set; }
        public event Action<VideoFrame>? FrameCaptured;

        public Task StartAsync(MonitorInfo monitor, VideoQuality quality, CancellationToken ct)
        {
            Monitor = monitor;
            return Task.CompletedTask;
        }

        public Task StopAsync() => Task.CompletedTask;

        public void Emit() => FrameCaptured?.Invoke(new VideoFrame(1920, 1080, new byte[16], TimeSpan.Zero));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeEncoder : IVideoEncoder
    {
        public string Name => "fake";
        public int EncodeCalls { get; private set; }
        public int KeyFrameRequests { get; private set; }

        public EncodedVideoSample? Encode(VideoFrame frame, VideoQuality quality)
        {
            EncodeCalls++;
            return new EncodedVideoSample(new byte[8], frame.Timestamp, true, frame.Width, frame.Height);
        }

        public void RequestKeyFrame() => KeyFrameRequests++;
        public void Dispose() { }
    }

    private sealed class FakePeerFactory : IPeerConnectionFactory
    {
        public List<FakePeer> Created { get; } = [];

        public IPeerConnection Create(Guid participantId)
        {
            var peer = new FakePeer(participantId);
            Created.Add(peer);
            return peer;
        }
    }

    private sealed class FakePeer(Guid participantId) : IPeerConnection
    {
        public Guid ParticipantId { get; } = participantId;
        public List<EncodedVideoSample> SentSamples { get; } = [];
        public List<EncodedAudioSample> SentAudioSamples { get; } = [];
        public List<string> RemoteCandidates { get; } = [];
        public string? AppliedAnswer { get; private set; }
        public bool Disposed { get; private set; }

        public event Action<string, string?, int?>? IceCandidateGathered;
        public event Action? KeyFrameRequested;
        public event Action<double>? PacketLossReported;

        public Task<string> CreateOfferAsync(CancellationToken ct) => Task.FromResult("offer-sdp");

        public Task ApplyAnswerAsync(string sdp, CancellationToken ct)
        {
            AppliedAnswer = sdp;
            return Task.CompletedTask;
        }

        public Task AddIceCandidateAsync(string candidate, string? sdpMid, int? sdpMLineIndex, CancellationToken ct)
        {
            RemoteCandidates.Add(candidate);
            return Task.CompletedTask;
        }

        public void SendVideo(EncodedVideoSample sample) => SentSamples.Add(sample);

        public void SendAudio(EncodedAudioSample sample) => SentAudioSamples.Add(sample);

        public void GatherCandidate(string candidate, string? mid, int? index) =>
            IceCandidateGathered?.Invoke(candidate, mid, index);

        public void RequestKeyFrame() => KeyFrameRequested?.Invoke();

        public void ReportPacketLoss(double loss) => PacketLossReported?.Invoke(loss);

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
