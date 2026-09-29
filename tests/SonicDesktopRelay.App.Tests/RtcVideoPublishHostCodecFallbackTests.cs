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

    private sealed class FakeEncoder(string name) : IVideoEncoder
    {
        public string Name { get; } = name;
        public EncodedVideoSample? Encode(VideoFrame frame, VideoQuality quality) => null;
        public void RequestKeyFrame() { }
        public void Dispose() { }
    }
}
