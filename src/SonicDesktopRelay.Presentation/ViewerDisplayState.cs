using Avalonia.Controls;

namespace SonicDesktopRelay.Presentation;

public enum ViewerDisplayMode { Normal, Fit, FullScreen }

/// <summary>Display transitions and idle controls, independent of the native window.</summary>
public sealed class ViewerDisplayState
{
    private WindowState _restoreWindowState;
    private ViewerDisplayMode _restoreMode;
    private TimeSpan _lastActivity;

    public ViewerDisplayMode Mode { get; private set; }
    public bool IsExpanded => Mode != ViewerDisplayMode.Normal;

    public WindowState SetMode(ViewerDisplayMode mode, WindowState windowState)
    {
        if (Mode == mode) return windowState;
        if (Mode == ViewerDisplayMode.FullScreen)
            windowState = _restoreWindowState;
        else if (mode == ViewerDisplayMode.FullScreen)
        {
            _restoreMode = Mode;
            _restoreWindowState = windowState == WindowState.FullScreen ? WindowState.Normal : windowState;
        }
        Mode = mode;
        return mode == ViewerDisplayMode.FullScreen ? WindowState.FullScreen : windowState;
    }

    public WindowState ToggleFullScreen(WindowState windowState) =>
        SetMode(Mode == ViewerDisplayMode.FullScreen ? _restoreMode : ViewerDisplayMode.FullScreen, windowState);

    public WindowState Reset(WindowState windowState) => SetMode(ViewerDisplayMode.Normal, windowState);

    public void RevealControls(TimeSpan now) => _lastActivity = now;

    public bool ControlsVisible(TimeSpan now, bool interacting) =>
        !IsExpanded || interacting || now - _lastActivity < TimeSpan.FromSeconds(3);
}
