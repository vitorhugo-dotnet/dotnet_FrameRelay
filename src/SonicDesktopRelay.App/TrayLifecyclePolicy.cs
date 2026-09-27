namespace SonicDesktopRelay.App;

internal static class TrayLifecyclePolicy
{
    public static bool ShouldHideWindow(bool minimizeToTray, bool explicitExit) =>
        minimizeToTray && !explicitExit;
}
