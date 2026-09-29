using SonicDesktopRelay.App;
using SonicDesktopRelay.Media;

namespace SonicDesktopRelay.App.Tests;

public sealed class RtcVideoPublishHostCodecFallbackTests
{
    [Fact]
    public void Av1_encoder_initialization_failure_selects_h264_and_disables_av1_advertising()
    {
        var capabilities = new VideoCodecCapabilities(
            new HashSet<VideoCodec> { VideoCodec.H264, VideoCodec.Av1 },
            new HashSet<VideoCodec> { VideoCodec.H264, VideoCodec.Av1 },
            new Dictionary<VideoCodec, VideoCodecConstraints>
            {
                [VideoCodec.Av1] = new("0", 4)
            },
            new Dictionary<VideoCodec, VideoCodecConstraints>
            {
                [VideoCodec.Av1] = new("0", 4)
            },
            new Dictionary<VideoCodec, string>());
        var h264 = new FakeEncoder("h264");

        var result = RtcVideoPublishHost.CreateVideoEncoder(
            capabilities,
            () => throw new InvalidOperationException("injected AV1 initialization failure"),
            () => h264);

        Assert.Same(h264, result.Encoder);
        Assert.Null(result.PeerCapabilities);
        Assert.Contains("injected AV1 initialization failure", result.InitializationFailure);
    }

    [Fact]
    public void Required_av1_workload_covers_recovery_ceiling_while_current_quality_is_degraded()
    {
        var profile = new VideoPublishProfile(1080, 60);
        var recoveredQuality = VideoQuality.InitialFor(profile);
        var degradedQuality = new VideoQuality(360, 15, 600_000);

        var required = RtcVideoPublishHost.RequiredAv1Workload(recoveredQuality, 1920, 1080);
        var degradedOnly = RtcVideoPublishHost.RequiredAv1Workload(degradedQuality, 1920, 1080);

        Assert.Equal(new VideoCodecConstraints("0", 9), required);
        Assert.Equal(new VideoCodecConstraints("0", 1), degradedOnly);
        Assert.True(required.MaxLevel > degradedOnly.MaxLevel);
    }

    private sealed class FakeEncoder(string name) : IVideoEncoder
    {
        public string Name { get; } = name;
        public EncodedVideoSample? Encode(VideoFrame frame, VideoQuality quality) => null;
        public void RequestKeyFrame() { }
        public void Dispose() { }
    }
}
