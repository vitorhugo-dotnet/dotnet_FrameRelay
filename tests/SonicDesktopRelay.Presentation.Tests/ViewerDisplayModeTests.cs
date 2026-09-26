using Avalonia;
using Avalonia.Controls;
using Xunit;

namespace SonicDesktopRelay.Presentation.Tests;

public sealed class ViewerDisplayModeTests
{
    [Fact]
    public void Fit_uses_client_area_without_changing_maximized_window()
    {
        var state = new ViewerDisplayState();
        Assert.Equal(WindowState.Maximized, state.SetMode(ViewerDisplayMode.Fit, WindowState.Maximized));
        Assert.True(state.IsExpanded);
        Assert.Equal(new Rect(0, 26.875, 900, 506.25),
            LetterboxGeometry.Fit(new Size(1920, 1080), new Size(900, 560)));
    }

    [Fact]
    public void Fullscreen_exits_to_fit_and_restores_previous_window_state()
    {
        var state = new ViewerDisplayState();
        state.SetMode(ViewerDisplayMode.Fit, WindowState.Maximized);
        Assert.Equal(WindowState.FullScreen, state.ToggleFullScreen(WindowState.Maximized));
        Assert.Equal(WindowState.Maximized, state.ToggleFullScreen(WindowState.FullScreen));
        Assert.Equal(ViewerDisplayMode.Fit, state.Mode);
        state.ToggleFullScreen(WindowState.Maximized);
        Assert.Equal(WindowState.Maximized, state.Reset(WindowState.FullScreen));
        Assert.Equal(ViewerDisplayMode.Normal, state.Mode);
    }

    [Theory]
    [InlineData(ViewerDisplayMode.Normal, WindowState.Normal)]
    [InlineData(ViewerDisplayMode.Fit, WindowState.Maximized)]
    [InlineData(ViewerDisplayMode.FullScreen, WindowState.Normal)]
    [InlineData(ViewerDisplayMode.FullScreen, WindowState.Maximized)]
    public void Stop_navigation_and_escape_reset_layout_and_restore_window(
        ViewerDisplayMode mode, WindowState initialWindow)
    {
        var state = new ViewerDisplayState();
        var currentWindow = state.SetMode(mode, initialWindow);
        Assert.Equal(initialWindow, state.Reset(currentWindow));
        Assert.Equal(ViewerDisplayMode.Normal, state.Mode);
        Assert.False(state.IsExpanded);
        Assert.True(state.ControlsVisible(TimeSpan.FromMinutes(1), false));
    }

    [Theory]
    [InlineData(ViewerDisplayMode.Fit)]
    [InlineData(ViewerDisplayMode.FullScreen)]
    public void Expanded_modes_share_idle_controls_and_pointer_reveal(ViewerDisplayMode mode)
    {
        var state = new ViewerDisplayState();
        state.SetMode(mode, WindowState.Normal);
        state.RevealControls(TimeSpan.Zero);
        Assert.True(state.ControlsVisible(TimeSpan.FromSeconds(1), false));
        Assert.False(state.ControlsVisible(TimeSpan.FromSeconds(4), false));
        Assert.True(state.ControlsVisible(TimeSpan.FromSeconds(4), true));
        state.RevealControls(TimeSpan.FromSeconds(4));
        Assert.True(state.ControlsVisible(TimeSpan.FromSeconds(5), false));
    }
}
