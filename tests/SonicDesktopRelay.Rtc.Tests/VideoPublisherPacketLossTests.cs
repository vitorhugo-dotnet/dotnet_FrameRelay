using SonicDesktopRelay.Media;
using SonicDesktopRelay.Rtc;
using SonicDesktopRelay.Signaling;
using Xunit;

namespace SonicDesktopRelay.Rtc.Tests;

public sealed class VideoPublisherPacketLossTests
{
    [Fact]
    public async Task Any_reported_packet_loss_requests_a_recovery_keyframe_without_forcing_quality_down()
    {
        var capture = new FakeCapture();
        var encoder = new FakeEncoder();
        var pipeline = new ScreenPublishPipeline(capture, encoder);
        var peers = new FakePeerFactory();
        await using var publisher = new VideoPublisher(pipeline, peers, new FakeSignaling());
        await pipeline.StartAsync(new MonitorInfo("display", "display", 1920, 1080, true), CancellationToken.None);
        await publisher.AddViewerAsync(Guid.NewGuid(), CancellationToken.None);

        peers.Created!.ReportPacketLoss(0.01);

        Assert.Equal(1, encoder.KeyFrameRequests);
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

        public IPeerConnection Create(Guid participantId) => Created = new FakePeer(participantId);
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
