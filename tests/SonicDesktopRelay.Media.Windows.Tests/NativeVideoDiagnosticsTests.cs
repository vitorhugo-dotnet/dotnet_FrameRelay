using SonicDesktopRelay.Media;
using SonicDesktopRelay.Media.Windows;

namespace SonicDesktopRelay.Media.Windows.Tests;

public sealed class NativeVideoDiagnosticsTests
{
    [Fact]
    public void Encoder_diagnostics_project_the_live_selected_transform()
    {
        if (!MediaFoundationH264Encoder.IsSupported) return;

        using var encoder = new MediaFoundationH264Encoder();
        var pixels = new byte[640 * 360 * 4];
        encoder.Encode(
            new VideoFrame(640, 360, pixels, TimeSpan.Zero),
            new VideoQuality(360, 30, 1_500_000));

        var diagnostics = encoder.Diagnostics;

        Assert.Equal("Media Foundation", diagnostics.Backend);
        Assert.False(string.IsNullOrWhiteSpace(diagnostics.TransformName));
        Assert.Equal("NV12", diagnostics.InputFormat);
        Assert.Equal("H264", diagnostics.OutputFormat);
        Assert.Equal(640, diagnostics.Width);
        Assert.Equal(360, diagnostics.Height);
        Assert.Equal(30, diagnostics.FramesPerSecond);
        Assert.Equal(1_500_000, diagnostics.Bitrate);
        Assert.Equal(encoder.RejectionLog, diagnostics.RejectionReasons);

        var text = diagnostics.ToString();
        Assert.DoesNotContain("turn:", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http://", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credential", text, StringComparison.OrdinalIgnoreCase);
    }
}
