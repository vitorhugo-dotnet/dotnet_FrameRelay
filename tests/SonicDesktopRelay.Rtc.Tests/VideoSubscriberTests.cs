using System.Text.Json;
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

        await harness.Subscriber.HandleAsync(
            Frame(SignalingMessageTypes.PublisherReady, Publisher, $$"""{"participantId":"{{Stranger}}"}"""),
            CancellationToken.None);

        Assert.Equal(Publisher, harness.Subscriber.PublisherId);
    }

    [Fact]
    public async Task A_successful_offer_emits_metadata_only_offer_and_answer_send_diagnostics()
    {
        var harness = new Harness();
        var diagnostics = new List<ViewerNegotiationDiagnosticEntry>();
        harness.Subscriber.Diagnostic += diagnostics.Add;

        await harness.OfferAsync();

        Assert.Contains(diagnostics, x => x.Event == "viewer.offer.received");
        Assert.Contains(diagnostics, x => x.Event == "viewer.answer.send.begin");
        var sentAnswer = Assert.Single(diagnostics, x => x.Event == "viewer.answer.send.ok");
        Assert.Equal("stable", sentAnswer.SignalingState);
        Assert.Equal("connected", sentAnswer.ConnectionState);

        var serialized = JsonSerializer.Serialize(diagnostics);
        Assert.DoesNotContain("offer-sdp", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("answer-sdp", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("candidate:", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Selected_transport_is_forwarded_without_raw_candidate_data()
    {
        var harness = new Harness();
        var expected = new RtcTransportDiagnostics("TURN", "UDP", "relay", "host");
        RtcTransportDiagnostics? observed = null;
        harness.Subscriber.TransportDiagnosticsChanged += diagnostics => observed = diagnostics;

        await harness.OfferAsync();
        harness.Peers.Created!.ReportTransport(expected);

        Assert.Equal(expected, observed);
        Assert.Equal(expected, harness.Subscriber.TransportDiagnostics);
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
    public async Task Ice_candidates_received_before_the_offer_are_buffered_and_applied_in_order()
    {
        var harness = new Harness();

        await harness.Subscriber.HandleAsync(
            Frame(SignalingMessageTypes.WebRtcIceCandidate, Publisher,
                """{"candidate":"candidate:early-1","sdpMid":"0","sdpMLineIndex":0}"""),
            CancellationToken.None);
        await harness.Subscriber.HandleAsync(
            Frame(SignalingMessageTypes.WebRtcIceCandidate, Publisher,
                """{"candidate":"candidate:early-2","sdpMid":"0","sdpMLineIndex":0}"""),
            CancellationToken.None);

        await harness.OfferAsync();

        Assert.Equal(
            ["candidate:early-1", "candidate:early-2"],
            harness.Peers.Created!.RemoteCandidates);
    }

    [Fact]
    public async Task Early_ice_buffer_is_bounded_per_participant()
    {
        var harness = new Harness();

        for (var i = 0; i < 65; i++)
        {
            await harness.Subscriber.HandleAsync(
                Frame(SignalingMessageTypes.WebRtcIceCandidate, Publisher,
                    JsonSerializer.Serialize(new
                    {
                        candidate = $"candidate:{i}",
                        sdpMid = "0",
                        sdpMLineIndex = 0
                    })),
                CancellationToken.None);
        }

        await harness.OfferAsync();

        Assert.Equal(64, harness.Peers.Created!.RemoteCandidates.Count);
        Assert.Equal("candidate:0", harness.Peers.Created.RemoteCandidates[0]);
        Assert.Equal("candidate:63", harness.Peers.Created.RemoteCandidates[^1]);
    }

    [Fact]
    public async Task A_negotiation_failure_does_not_escape_the_signaling_callback_and_disposes_the_peer()
    {
        var harness = new Harness();
        harness.Peers.AnswerFailure = new InvalidOperationException("synthetic negotiation failure");
        string? reportedFailure = null;
        harness.Subscriber.NegotiationFailed += failure => reportedFailure = failure;

        var failure = await Record.ExceptionAsync(harness.OfferAsync);

        Assert.Null(failure);
        Assert.True(harness.Peers.Created!.Disposed);
        Assert.Empty(harness.Signaling.Sent);
        Assert.Equal("WebRTC negotiation failed at createAnswer: synthetic negotiation failure", reportedFailure);
    }

    [Fact]
    public async Task A_peer_negotiation_failure_preserves_the_exact_failed_stage()
    {
        var harness = new Harness();
        harness.Peers.AnswerFailure = new ViewerNegotiationException(
            "setLocalDescription",
            "synthetic local-description failure");
        string? reportedFailure = null;
        harness.Subscriber.NegotiationFailed += failure => reportedFailure = failure;

        await harness.OfferAsync();

        Assert.Equal(
            "WebRTC negotiation failed at setLocalDescription: synthetic local-description failure",
            reportedFailure);
        Assert.True(harness.Peers.Created!.Disposed);
    }

    [Fact]
    public async Task Ice_candidates_from_an_unrelated_participant_are_ignored()
    {
        var harness = new Harness();
        await harness.ReadyAsync();
        await harness.OfferAsync();

        await harness.Subscriber.HandleAsync(
            Frame(SignalingMessageTypes.WebRtcIceCandidate, Stranger,
                """{"candidate":"candidate:stranger","sdpMid":"0","sdpMLineIndex":0}"""),
            CancellationToken.None);

        Assert.Empty(harness.Peers.Created!.RemoteCandidates);
    }

    [Fact]
    public async Task A_gathered_candidate_is_signalled_to_the_publisher()
    {
        var harness = new Harness();
        await harness.ReadyAsync();
        await harness.OfferAsync();
        harness.Signaling.Sent.Clear();
        var diagnostics = new List<ViewerNegotiationDiagnosticEntry>();
        harness.Subscriber.Diagnostic += diagnostics.Add;

        harness.Peers.Created!.GatherCandidate("candidate:2", "0", 0);

        var sent = Assert.Single(harness.Signaling.Sent);
        Assert.Equal(SignalingMessageTypes.WebRtcIceCandidate, sent.Type);
        Assert.Equal(Publisher, sent.To);
        Assert.Contains(diagnostics, x => x.Event == "viewer.ice_candidate.send");
        Assert.DoesNotContain("candidate:2", JsonSerializer.Serialize(diagnostics), StringComparison.Ordinal);
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
    public async Task A_received_audio_sample_reaches_the_audio_watch_pipeline()
    {
        var video = new WatchPipelineDriver();
        var peers = new FakeViewerPeerFactory();
        var signaling = new FakeSignaling();
        var decoder = new FakeAudioDecoder();
        var sink = new FakeAudioSink();
        await using var audio = new AudioWatchPipeline(decoder, sink);
        await audio.StartAsync(CancellationToken.None);
        await using var subscriber = new VideoSubscriber(video.Pipeline, audio, peers, signaling);

        await subscriber.HandleAsync(
            Frame(SignalingMessageTypes.WebRtcOffer, Publisher, """{"type":"offer","sdp":"offer-sdp"}"""),
            CancellationToken.None);

        peers.Created!.ReceiveAudio(new EncodedAudioSample(
            new byte[] { 1, 2, 3 }, 960, TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(40)));

        Assert.Equal(1, decoder.DecodeCalls);
        var frame = Assert.Single(sink.Frames);
        Assert.Equal(TimeSpan.FromMilliseconds(40), frame.Timestamp);
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

        Assert.Same(first, harness.Peers.Created);
        Assert.Equal("second-sdp", harness.Peers.Created!.ReceivedOffer);
    }

    [Fact]
    public async Task An_offer_that_arrives_before_publisher_ready_still_establishes_the_publisher()
    {
        var harness = new Harness();

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

    private sealed class FakeAudioDecoder : IAudioDecoder
    {
        public string Name => "fake-opus";
        public int DecodeCalls { get; private set; }

        public AudioFrame? Decode(EncodedAudioSample sample)
        {
            DecodeCalls++;
            return new AudioFrame(new byte[sample.SampleCount * 2 * 2], 48_000, 2, sample.SampleCount, sample.Timestamp);
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeAudioSink : IAudioSink
    {
        public string Name => "fake-sink";
        public List<AudioFrame> Frames { get; } = [];

        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

        public void Write(AudioFrame frame) => Frames.Add(frame);

        public Task StopAsync() => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeViewerPeerFactory : IViewerPeerConnectionFactory
    {
        public FakeViewerPeer? Created { get; private set; }

        public int CreateCalls { get; private set; }

        public Exception? AnswerFailure { get; set; }

        public IViewerPeerConnection Create()
        {
            CreateCalls++;
            Created = new FakeViewerPeer(AnswerFailure);
            return Created;
        }
    }

    private sealed class FakeViewerPeer(Exception? answerFailure) : IViewerPeerConnection
    {
        public string? ReceivedOffer { get; private set; }

        public List<string> RemoteCandidates { get; } = [];

        public int KeyFrameRequests { get; private set; }

        public bool Disposed { get; private set; }

        public event Action<string, string?, int?>? IceCandidateGathered;

        public event Action<EncodedVideoSample>? VideoSampleReceived;

        public event Action<EncodedAudioSample>? AudioSampleReceived;

        public event Action<ViewerNegotiationDiagnosticEntry>? Diagnostic;

        public event Action<RtcTransportDiagnostics>? TransportDiagnosticsChanged;

        public RtcTransportDiagnostics? TransportDiagnostics { get; private set; }

        public Task<string> CreateAnswerAsync(string offerSdp, CancellationToken ct)
        {
            ReceivedOffer = offerSdp;
            if (answerFailure is not null) return Task.FromException<string>(answerFailure);

            Diagnostic?.Invoke(new ViewerNegotiationDiagnosticEntry(
                DateTimeOffset.UtcNow,
                "viewer.local_description.ok",
                "stable",
                "complete",
                "connected",
                "connected",
                SetDescriptionResult: "OK"));

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

        public void ReceiveAudio(EncodedAudioSample sample) => AudioSampleReceived?.Invoke(sample);

        public void ReportTransport(RtcTransportDiagnostics diagnostics)
        {
            TransportDiagnostics = diagnostics;
            TransportDiagnosticsChanged?.Invoke(diagnostics);
        }

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
