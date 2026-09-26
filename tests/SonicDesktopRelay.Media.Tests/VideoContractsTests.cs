using Xunit;

namespace SonicDesktopRelay.Media.Tests;

public sealed class VideoContractsTests
{
    [Fact]
    public void Monitor_capture_target_keeps_monitor_metadata()
    {
        var monitor = new MonitorInfo("DISPLAY1", "Primary", 1920, 1080, true);

        var target = new CaptureTarget.Monitor(monitor);

        Assert.Equal(monitor, target.Info);
    }

    [Fact]
    public void Window_capture_target_keeps_window_and_process_identity()
    {
        var window = new WindowInfo(
            (nint)0x1234,
            57,
            DateTime.UnixEpoch,
            "Editor",
            "editor.exe",
            1280,
            720);

        var target = new CaptureTarget.Window(window);

        Assert.Equal(window, target.Info);
        Assert.Equal((nint)0x1234, target.Info.Handle);
        Assert.Equal(57u, target.Info.ProcessId);
        Assert.Equal((1280, 720), (target.Info.Width, target.Info.Height));
    }

    [Fact]
    public void The_default_quality_targets_1080p30()
    {
        var quality = VideoQuality.Default;

        Assert.Equal(1080, quality.MaxHeight);
        Assert.Equal(30, quality.FramesPerSecond);
        Assert.Equal(4_000_000, quality.TargetBitsPerSecond);
    }

    [Fact]
    public void Reducing_quality_spends_bitrate_before_desktop_resolution()
    {
        var reduced = VideoQuality.Default.Reduced();

        Assert.Equal(1080, reduced.MaxHeight);
        Assert.Equal(3_000_000, reduced.TargetBitsPerSecond);
    }

    [Fact]
    public void Resolution_reduces_only_after_lower_1080p_bitrate_rungs_are_exhausted()
    {
        var quality = VideoQuality.Default.Reduced().Reduced().Reduced();

        Assert.Equal(720, quality.MaxHeight);
        Assert.Equal(2_000_000, quality.TargetBitsPerSecond);
    }

    [Fact]
    public void Improving_quality_walks_back_toward_default_one_step_at_a_time()
    {
        var degraded = VideoQuality.Default.Reduced().Reduced();

        var improved = degraded.Improved();

        Assert.Equal(1080, improved.MaxHeight);
        Assert.Equal(3_000_000, improved.TargetBitsPerSecond);
    }

    [Fact]
    public void Quality_never_degrades_below_the_floor_however_often_it_is_reduced()
    {
        var quality = VideoQuality.Default;

        for (var i = 0; i < 20; i++) quality = quality.Reduced();

        Assert.Equal(360, quality.MaxHeight);
        Assert.Equal(15, quality.FramesPerSecond);
        Assert.Equal(600_000, quality.TargetBitsPerSecond);
    }

    [Theory]
    [InlineData(1080, 1920, 1080, 1920, 1080)]
    [InlineData(720, 1920, 1080, 1280, 720)]
    [InlineData(1080, 1280, 720, 1280, 720)]
    public void Scaling_preserves_aspect_ratio_and_never_upscales(
        int maxHeight, int sourceWidth, int sourceHeight, int expectedWidth, int expectedHeight)
    {
        var (width, height) = new VideoQuality(maxHeight, 30, 1).ScaleFor(sourceWidth, sourceHeight);

        Assert.Equal(expectedWidth, width);
        Assert.Equal(expectedHeight, height);
    }

    [Fact]
    public void Scaled_dimensions_are_even_because_h264_yuv420_requires_it()
    {
        var (width, height) = new VideoQuality(721, 30, 1).ScaleFor(1919, 1081);

        Assert.Equal(0, width % 2);
        Assert.Equal(0, height % 2);
    }
}
