using System.Reflection;
using SonicDesktopRelay.Media.Windows;
using Xunit;

namespace SonicDesktopRelay.Media.Windows.Tests;

public sealed class CaptureFrameGeometryTests
{
    [Fact]
    public void Visible_size_uses_content_size_when_the_capture_surface_is_larger()
    {
        var method = GetVisibleSizeMethod();

        var result = ((int Width, int Height))method.Invoke(null, [1920, 1080, 1920, 1040])!;

        Assert.Equal(1920, result.Width);
        Assert.Equal(1040, result.Height);
    }

    [Fact]
    public void Visible_size_never_exceeds_the_capture_surface()
    {
        var method = GetVisibleSizeMethod();

        var result = ((int Width, int Height))method.Invoke(null, [1920, 1080, 2000, 1200])!;

        Assert.Equal(1920, result.Width);
        Assert.Equal(1080, result.Height);
    }

    private static MethodInfo GetVisibleSizeMethod()
    {
        var type = typeof(GraphicsCaptureScreenSource).Assembly.GetType(
            "SonicDesktopRelay.Media.Windows.CaptureFrameGeometry");
        Assert.NotNull(type);

        var method = type!.GetMethod(
            "VisibleSize",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return method!;
    }
}
