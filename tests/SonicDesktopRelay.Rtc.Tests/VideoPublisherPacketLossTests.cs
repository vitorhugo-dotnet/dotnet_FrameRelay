using SonicDesktopRelay.Media;
using SonicDesktopRelay.Rtc;
using SonicDesktopRelay.Signaling;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace SonicDesktopRelay.Rtc.Tests;

public sealed class VideoPublisherPacketLossTests
{
    [Fact]
    public async Task Sustained_zero_decode_receiver_stats_are_accepted_and_downshift_quality()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var pipeline = new ScreenPublishPipeline(new FakeCapture(), new FakeEncoder(), time: time);
        var peers = new FakePeerFactory();
        await using var publisher = new VideoPublisher(pipeline, peers, new FakeSignaling());
        var viewer = Guid.NewGuid();
        await publisher.AddViewerAsync(viewer, CancellationToken.None);

        for (var i = 0; i < 3; i++)
        {
            using var document = System.Text.Json.JsonDocument.Parse(
                "{\"version\":1,\"intervalMilliseconds\":2000,\"rtpPacketsReceived\":0,\"rtpPacketsLost\":0,\"accessUnitsReceived\":0,\"incompleteAccessUnits\":0,\"decodedFrames\":0,\"targetFramesPerSecond\":30}");
            await publisher.HandleAsync(new SignalingEnvelope(SignalingMessageTypes.VideoReceiverStats,
                null, null, viewer, null, null, document.RootElement.Clone()), CancellationToken.None);
            if (i < 2) time.Advance(TimeSpan.FromSeconds(2.5));
        }

        Assert.Equal(3_000_000, pipeline.Quality.TargetBitsPerSecond);
    }

    [Theory]
    [InlineData("{\"version\":2,\"intervalMilliseconds\":2000,\"rtpPacketsReceived\":1,\"rtpPacketsLost\":1,\"accessUnitsReceived\":1,\"incompleteAccessUnits\":0,\"decodedFrames\":1,\"targetFramesPerSecond\":30}")]
    [InlineData("{\"version\":1,\"intervalMilliseconds\":999,\"rtpPacketsReceived\":1,\"rtpPacketsLost\":1,\"accessUnitsReceived\":1,\"incompleteAccessUnits\":0,\"decodedFrames\":1,\"targetFramesPerSecond\":30}")]
    [InlineData("{\"version\":1,\"intervalMilliseconds\":5001,\"rtpPacketsReceived\":1,\"rtpPacketsLost\":1,\"accessUnitsReceived\":1,\"incompleteAccessUnits\":0,\"decodedFrames\":1,\"targetFramesPerSecond\":30}")]
    [InlineData("{\"version\":1,\"intervalMilliseconds\":2000,\"rtpPacketsReceived\":1.5,\"rtpPacketsLost\":1,\"accessUnitsReceived\":1,\"incompleteAccessUnits\":0,\"decodedFrames\":1,\"targetFramesPerSecond\":30}")]
    [InlineData("{\"version\":1,\"intervalMilliseconds\":2000,\"rtpPacketsReceived\":-1,\"rtpPacketsLost\":0,\"accessUnitsReceived\":1,\"incompleteAccessUnits\":0,\"decodedFrames\":1,\"targetFramesPerSecond\":30}")]
    [InlineData("{\"version\":1,\"intervalMilliseconds\":2000,\"rtpPacketsReceived\":10000001,\"rtpPacketsLost\":0,\"accessUnitsReceived\":1,\"incompleteAccessUnits\":0,\"decodedFrames\":1,\"targetFramesPerSecond\":30}")]
    [InlineData("{\"version\":1,\"intervalMilliseconds\":2000,\"rtpPacketsReceived\":6000000,\"rtpPacketsLost\":5000000,\"accessUnitsReceived\":1,\"incompleteAccessUnits\":0,\"decodedFrames\":1,\"targetFramesPerSecond\":30}")]
    [InlineData("{\"version\":1,\"intervalMilliseconds\":2000,\"rtpPacketsReceived\":1,\"rtpPacketsLost\":0,\"accessUnitsReceived\":10000001,\"incompleteAccessUnits\":0,\"decodedFrames\":1,\"targetFramesPerSecond\":30}")]
    [InlineData("{\"version\":1,\"intervalMilliseconds\":2000,\"rtpPacketsReceived\":1,\"rtpPacketsLost\":1,\"accessUnitsReceived\":1,\"incompleteAccessUnits\":2,\"decodedFrames\":1,\"targetFramesPerSecond\":30}")]
    [InlineData("{\"version\":1,\"intervalMilliseconds\":2000,\"rtpPacketsReceived\":1,\"rtpPacketsLost\":0,\"accessUnitsReceived\":1,\"incompleteAccessUnits\":0,\"decodedFrames\":10000001,\"targetFramesPerSecond\":30}")]
    [InlineData("{\"version\":1,\"intervalMilliseconds\":2000,\"rtpPacketsReceived\":1,\"rtpPacketsLost\":0,\"accessUnitsReceived\":1,\"incompleteAccessUnits\":0,\"decodedFrames\":1,\"targetFramesPerSecond\":0}")]
    [InlineData("{\"version\":1,\"intervalMilliseconds\":2000,\"rtpPacketsReceived\":1,\"rtpPacketsLost\":0,\"accessUnitsReceived\":1,\"incompleteAccessUnits\":0,\"decodedFrames\":1,\"targetFramesPerSecond\":61}")]
    [InlineData("{\"version\":1,\"intervalMilliseconds\":2000,\"rtpPacketsReceived\":1,\"rtpPacketsLost\":1,\"accessUnitsReceived\":1,\"incompleteAccessUnits\":0,\"decodedFrames\":1,\"targetFramesPerSecond\":1e999}")]
    public async Task Invalid_receiver_stats_are_ignored(string json)
    {
        var pipeline = new ScreenPublishPipeline(new FakeCapture(), new FakeEncoder());
        var peers = new FakePeerFactory();
        await using var activePublisher = new VideoPublisher(pipeline, peers, new FakeSignaling());
        var viewer = Guid.NewGuid();
        await activePublisher.AddViewerAsync(viewer, CancellationToken.None);

        await activePublisher.HandleAsync(new SignalingEnvelope(SignalingMessageTypes.VideoReceiverStats,
            null, null, viewer, null, null, System.Text.Json.JsonDocument.Parse(json).RootElement.Clone()),
            CancellationToken.None);

        Assert.Equal(VideoQuality.Default, pipeline.Quality);
    }

    [Fact]
    public async Task Receiver_stats_arriving_after_viewer_removal_are_ignored()
    {
        var pipeline = new ScreenPublishPipeline(new FakeCapture(), new FakeEncoder());
        var peers = new FakePeerFactory();
        await using var publisher = new VideoPublisher(pipeline, peers, new FakeSignaling());
        var viewer = Guid.NewGuid();
        await publisher.AddViewerAsync(viewer, CancellationToken.None);
        await publisher.RemoveViewerAsync(viewer);

        await publisher.HandleAsync(new SignalingEnvelope(SignalingMessageTypes.VideoReceiverStats,
            null, null, viewer, null, null, System.Text.Json.JsonDocument.Parse(
                "{\"version\":1,\"intervalMilliseconds\":2000,\"rtpPacketsReceived\":0,\"rtpPacketsLost\":100,\"accessUnitsReceived\":0,\"incompleteAccessUnits\":0,\"decodedFrames\":0,\"targetFramesPerSecond\":30}").RootElement.Clone()),
            CancellationToken.None);

        Assert.Equal(VideoQuality.Default, pipeline.Quality);
    }

    [Fact]
    public async Task Rtcp_packet_loss_does_not_request_a_keyframe_or_force_quality_down()
    {
        var capture = new FakeCapture();
        var encoder = new FakeEncoder();
        var pipeline = new ScreenPublishPipeline(capture, encoder);
        var peers = new FakePeerFactory();
        await using var publisher = new VideoPublisher(pipeline, peers, new FakeSignaling());
        await pipeline.StartAsync(new MonitorInfo("display", "display", 1920, 1080, true), CancellationToken.None);
        await publisher.AddViewerAsync(Guid.NewGuid(), CancellationToken.None);

        peers.Created!.ReportPacketLoss(0.01);

        Assert.Equal(0, encoder.KeyFrameRequests);
        Assert.Equal(1080, pipeline.Quality.MaxHeight);
    }

    private sealed class FakeCapture : IScreenCaptureSource
    {
        public MonitorInfo Monitor { get; private set; }
        public event Action<VideoFrame>? FrameCaptured { add { } remove { } }

        public Task StartAsync(MonitorInfo monitor, VideoQuality quality, CancellationToken ct)
        {
            Monitor = monitor;
            return Task.CompletedTask;
        }

        public void SetFrameRate(int framesPerSecond) { }

        public Task StopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeEncoder : IVideoEncoder
    {
        public string Name => "fake";
        public int KeyFrameRequests { get; private set; }

        public EncodedVideoSample? Encode(VideoFrame frame, VideoQuality quality) => null;
        public void RequestKeyFrame() => KeyFrameRequests++;
        public void Dispose() { }
    }

    private sealed class FakePeerFactory : IPeerConnectionFactory
    {
        public FakePeer? Created { get; private set; }

        public IPeerConnection Create(Guid participantId, bool? forceRelay = null) => Created = new FakePeer(participantId);
    }

    private sealed class FakePeer(Guid participantId) : IPeerConnection
    {
        public Guid ParticipantId { get; } = participantId;

        public event Action<string, string?, int?>? IceCandidateGathered { add { } remove { } }
        public event Action<KeyFrameRequestReason>? KeyFrameRequested { add { } remove { } }
        public event Action<double>? PacketLossReported;
        public event Action<RtcTransportDiagnostics>? TransportDiagnosticsChanged { add { } remove { } }

        public RtcTransportDiagnostics? TransportDiagnostics => null;

        public Task<string> CreateOfferAsync(CancellationToken ct) => Task.FromResult("offer");
        public Task ApplyAnswerAsync(string sdp, CancellationToken ct) => Task.CompletedTask;
        public Task AddIceCandidateAsync(string candidate, string? sdpMid, int? sdpMLineIndex, CancellationToken ct) => Task.CompletedTask;
        public void SendVideo(EncodedVideoSample sample) { }
        public void SendAudio(EncodedAudioSample sample) { }
        public void ReportPacketLoss(double loss) => PacketLossReported?.Invoke(loss);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeSignaling : ISignalingConnection
    {
        public SignalingState State => SignalingState.Connected;

        public event Action<SignalingEnvelope>? FrameReceived { add { } remove { } }
        public event Action<SignalingState>? StateChanged { add { } remove { } }

        public Task StartAsync(Guid sessionId, CancellationToken ct) => Task.CompletedTask;
        public Task SendAsync(string type, Guid? to, object? payload, CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
