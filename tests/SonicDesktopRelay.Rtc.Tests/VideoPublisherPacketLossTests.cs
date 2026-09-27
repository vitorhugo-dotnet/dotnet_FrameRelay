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

    [Fact]
    public async Task Only_video_rtcp_reception_reports_reach_the_screen_quality_controller()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var pipeline = new ScreenPublishPipeline(new FakeCapture(), new FakeEncoder(), time: time);
        var peers = new FakePeerFactory();
        await using var publisher = new VideoPublisher(pipeline, peers, new FakeSignaling(), time: time);
        await pipeline.StartAsync(new MonitorInfo("display", "display", 1920, 1080, true), CancellationToken.None);
        await publisher.AddViewerAsync(Guid.NewGuid(), CancellationToken.None);
        var initialQuality = pipeline.Quality;
        var peer = peers.Created!;
        var audioReports = new List<RtcpReceptionReport>();
        publisher.AudioRtcpReportReceived += (_, report) => audioReports.Add(report);

        for (var i = 0; i < 3; i++)
        {
            peer.ReportReception(new RtcpReceptionReport(RtcMediaKind.Audio, 11, 0.15));
            peer.ReportReception(new RtcpReceptionReport(RtcMediaKind.Unknown, 33, 0.15));
            if (i < 2) time.Advance(TimeSpan.FromSeconds(2.5));
        }

        Assert.Equal(3, audioReports.Count);
        Assert.All(audioReports, report => Assert.Equal(RtcMediaKind.Audio, report.MediaKind));
        Assert.Equal(initialQuality, pipeline.Quality);

        peer.ReportReception(new RtcpReceptionReport(RtcMediaKind.Video, 22, 0.15));
        time.Advance(TimeSpan.FromSeconds(2.5));
        peer.ReportReception(new RtcpReceptionReport(RtcMediaKind.Video, 22, 0.15));
        time.Advance(TimeSpan.FromSeconds(2.5));
        peer.ReportReception(new RtcpReceptionReport(RtcMediaKind.Video, 22, 0.15));
        Assert.True(pipeline.Quality.TargetBitsPerSecond < initialQuality.TargetBitsPerSecond);
        Assert.Equal(3, audioReports.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Audio_and_unknown_loss_cannot_block_recovery_from_either_video_source(bool receiverStats)
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var pipeline = new ScreenPublishPipeline(new FakeCapture(), new FakeEncoder(), time: time);
        var peers = new FakePeerFactory();
        await using var publisher = new VideoPublisher(pipeline, peers, new FakeSignaling(), time: time);
        var viewer = Guid.NewGuid();
        await publisher.AddViewerAsync(viewer, CancellationToken.None);
        var peer = peers.Created!;
        for (var i = 0; i < 3; i++)
        {
            peer.ReportPacketLoss(0.1);
            if (i < 2) time.Advance(TimeSpan.FromSeconds(2.5));
        }
        Assert.Equal(3_000_000, pipeline.Quality.TargetBitsPerSecond);

        for (var i = 0; i < 4; i++)
        {
            peer.ReportReception(new RtcpReceptionReport(RtcMediaKind.Audio, 11, 0.9));
            peer.ReportReception(new RtcpReceptionReport(RtcMediaKind.Unknown, 33, 0.9));
            if (receiverStats)
            {
                using var document = System.Text.Json.JsonDocument.Parse(
                    "{\"version\":1,\"intervalMilliseconds\":2000,\"rtpPacketsReceived\":100,\"rtpPacketsLost\":0,\"accessUnitsReceived\":60,\"incompleteAccessUnits\":0,\"decodedFrames\":60,\"targetFramesPerSecond\":30}");
                await publisher.HandleAsync(new SignalingEnvelope(SignalingMessageTypes.VideoReceiverStats,
                    null, null, viewer, null, null, document.RootElement.Clone()), CancellationToken.None);
            }
            else
                peer.ReportPacketLoss(0);
            if (i < 3) time.Advance(TimeSpan.FromSeconds(10));
        }
        Assert.Equal(VideoQuality.Default, pipeline.Quality);
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
        public event Action<RtcpReceptionReport>? ReceptionReportReceived;
        public event Action<RtcTransportDiagnostics>? TransportDiagnosticsChanged { add { } remove { } }

        public RtcTransportDiagnostics? TransportDiagnostics => null;

        public Task<string> CreateOfferAsync(CancellationToken ct) => Task.FromResult("offer");
        public Task ApplyAnswerAsync(string sdp, CancellationToken ct) => Task.CompletedTask;
        public Task AddIceCandidateAsync(string candidate, string? sdpMid, int? sdpMLineIndex, CancellationToken ct) => Task.CompletedTask;
        public void SendVideo(EncodedVideoSample sample) { }
        public void SendAudio(EncodedAudioSample sample) { }
        public void ReportPacketLoss(double loss) => ReportReception(new RtcpReceptionReport(RtcMediaKind.Video, 22, loss));
        public void ReportReception(RtcpReceptionReport report) => ReceptionReportReceived?.Invoke(report);
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
