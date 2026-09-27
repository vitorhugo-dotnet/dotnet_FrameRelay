using SonicDesktopRelay.App;

namespace SonicDesktopRelay.App.Tests;

public sealed class TrayLifecyclePolicyTests
{
    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    public void Hides_window_only_for_tray_close_without_explicit_exit(
        bool minimizeToTray, bool explicitExit, bool expected)
    {
        Assert.Equal(expected, TrayLifecyclePolicy.ShouldHideWindow(minimizeToTray, explicitExit));
    }
}
