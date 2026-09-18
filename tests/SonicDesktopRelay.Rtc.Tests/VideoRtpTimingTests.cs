using Xunit;

namespace SonicDesktopRelay.Rtc.Tests;

public sealed class VideoRtpTimingTests
{
    [Theory]
    [InlineData(15, 6000u)]
    [InlineData(30, 3000u)]
    [InlineData(60, 1500u)]
    public void RTP_video_duration_matches_configured_fps(int fps, uint expected)
    {
        var duration = TimeSpan.FromSeconds(1d / fps);

        Assert.Equal(expected, VideoRtpTiming.ToTimestampUnits(duration));
    }

    [Fact]
    public void Zero_or_negative_duration_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => VideoRtpTiming.ToTimestampUnits(TimeSpan.Zero));
    }
}
