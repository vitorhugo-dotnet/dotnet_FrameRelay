using Xunit;

namespace SonicDesktopRelay.Media.Tests;

public sealed class VideoCodecNegotiatorTests
{
    private static readonly VideoCodecConstraints Required = new("0", 5);

    [Fact]
    public void Selects_av1_when_publisher_and_every_viewer_support_the_required_configuration()
    {
        var result = VideoCodecNegotiator.Select(Capabilities(true, true, 5), [Capabilities(false, true, 5)], Required);

        Assert.Equal(VideoCodec.Av1, result.Codec);
        Assert.Null(result.FallbackReason);
    }

    [Fact]
    public void Keeps_h264_when_there_are_no_active_viewers()
    {
        var result = VideoCodecNegotiator.Select(Capabilities(true, true, 5), [], Required);

        Assert.Equal(VideoCodec.H264, result.Codec);
        Assert.Equal("no-active-viewers", result.FallbackReason);
    }

    [Fact]
    public void Keeps_h264_when_publisher_has_no_av1_encoder()
    {
        var result = VideoCodecNegotiator.Select(Capabilities(false, false, 0), [Capabilities(false, true, 5)], Required);

        Assert.Equal("publisher-av1-encoder-unavailable", result.FallbackReason);
    }

    [Fact]
    public void Keeps_h264_when_any_viewer_has_no_av1_decoder()
    {
        var result = VideoCodecNegotiator.Select(Capabilities(true, true, 5), [Capabilities(false, true, 5), Capabilities(false, false, 0)], Required);

        Assert.Equal("viewer-av1-decoder-unavailable", result.FallbackReason);
    }

    [Fact]
    public void Keeps_h264_when_av1_configuration_is_missing()
    {
        var result = VideoCodecNegotiator.Select(Capabilities(true, false, 0, includeEncoderConfiguration: false), [Capabilities(false, true, 5)], Required);

        Assert.Equal("av1-configuration-unavailable", result.FallbackReason);
    }

    [Fact]
    public void Keeps_h264_when_av1_profiles_differ()
    {
        var publisher = Capabilities(true, true, 5, profile: "1");
        var result = VideoCodecNegotiator.Select(publisher, [Capabilities(false, true, 5)], Required);

        Assert.Equal("av1-profile-incompatible", result.FallbackReason);
    }

    [Fact]
    public void Keeps_h264_when_decoder_level_is_too_low()
    {
        var result = VideoCodecNegotiator.Select(Capabilities(true, true, 5), [Capabilities(false, true, 4)], Required);

        Assert.Equal("av1-level-insufficient", result.FallbackReason);
    }

    private static VideoCodecCapabilities Capabilities(bool encoder, bool decoder, int level, string profile = "0", bool includeEncoderConfiguration = true) =>
        new(
            encoder ? new HashSet<VideoCodec> { VideoCodec.Av1 } : new HashSet<VideoCodec>(),
            decoder ? new HashSet<VideoCodec> { VideoCodec.Av1 } : new HashSet<VideoCodec>(),
            encoder && includeEncoderConfiguration ? new Dictionary<VideoCodec, VideoCodecConstraints> { [VideoCodec.Av1] = new(profile, level) } : new Dictionary<VideoCodec, VideoCodecConstraints>(),
            decoder ? new Dictionary<VideoCodec, VideoCodecConstraints> { [VideoCodec.Av1] = new(profile, level) } : new Dictionary<VideoCodec, VideoCodecConstraints>(),
            new Dictionary<VideoCodec, string>());
}
