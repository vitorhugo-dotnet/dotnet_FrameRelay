using SonicDesktopRelay.App;
using SonicDesktopRelay.Media;
using SonicDesktopRelay.Presentation;
using SonicDesktopRelay.Signaling;

namespace SonicDesktopRelay.Media.Windows.Tests;

public sealed class ShellFullscreenTests
{
    [Fact]
    public void Fullscreen_requires_an_active_watch_session()
    {
        var shell = CreateShell();
        shell.ViewModel.CurrentPage = Page.Watch;

        shell.EnterVideoFullScreen();

        Assert.False(shell.IsVideoFullScreen);
    }

    [Fact]
    public void Repeated_enter_and_exit_notify_only_for_actual_transitions()
    {
        var shell = WatchingShell();
        var notifications = new List<string?>();
        shell.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);

        shell.EnterVideoFullScreen();
        shell.EnterVideoFullScreen();
        Assert.True(shell.IsVideoFullScreen);

        shell.ExitVideoFullScreen();
        shell.ExitVideoFullScreen();
        Assert.False(shell.IsVideoFullScreen);
        Assert.Equal(2, notifications.Count(x => x == nameof(Shell.IsVideoFullScreen)));
    }

    [Fact]
    public void Toggle_returns_to_normal_state_and_can_enter_again()
    {
        var shell = WatchingShell();

        shell.ToggleVideoFullScreen();
        Assert.True(shell.IsVideoFullScreen);
        shell.ToggleVideoFullScreen();
        Assert.False(shell.IsVideoFullScreen);
        shell.ToggleVideoFullScreen();
        Assert.True(shell.IsVideoFullScreen);
    }

    [Fact]
    public void Navigating_away_from_watch_exits_fullscreen()
    {
        var shell = WatchingShell();
        shell.EnterVideoFullScreen();

        shell.ViewModel.CurrentPage = Page.Diagnostics;

        Assert.False(shell.IsVideoFullScreen);
    }

    [Fact]
    public async Task Stopping_without_a_runtime_exits_fullscreen()
    {
        var shell = WatchingShell();
        shell.EnterVideoFullScreen();

        await shell.StopAsync(default);

        Assert.False(shell.IsVideoFullScreen);
    }

    [Fact]
    public void Session_ending_exits_fullscreen()
    {
        var shell = WatchingShell();
        shell.EnterVideoFullScreen();

        shell.ViewModel.Apply(SessionSnapshot.Idle);

        Assert.False(shell.IsVideoFullScreen);
    }

    private static Shell WatchingShell()
    {
        var shell = CreateShell();
        shell.ViewModel.CurrentPage = Page.Watch;
        shell.ViewModel.Apply(new SessionSnapshot(SessionPhase.Watching, null, Guid.NewGuid(), 0,
            SignalingState.Connected, null, Watching: WatchState.Waiting));
        return shell;
    }

    private static Shell CreateShell() => new(new EmptyMonitors(), new EmptyWindows());

    private sealed class EmptyMonitors : IMonitorEnumerator
    {
        public IReadOnlyList<MonitorInfo> List() => [];
    }

    private sealed class EmptyWindows : IWindowEnumerator
    {
        public IReadOnlyList<WindowInfo> List() => [];
    }
}
