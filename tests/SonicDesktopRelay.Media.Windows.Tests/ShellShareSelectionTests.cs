using SonicDesktopRelay.App;
using SonicDesktopRelay.Media;

namespace SonicDesktopRelay.Media.Windows.Tests;

public sealed class ShellShareSelectionTests
{
    private static readonly MonitorInfo Primary = new("DISPLAY1", "Primary", 1920, 1080, true);
    private static readonly WindowInfo First = new((nint)10, 20, new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), "Editor", "editor", 1280, 720);
    private readonly FakeMonitorEnumerator _monitors = new(Primary);
    private readonly FakeWindowEnumerator _windows = new(First);

    [Fact]
    public void Defaults_to_primary_monitor_and_switches_typed_selection()
    {
        var shell = CreateShell();
        Assert.Equal(Primary, shell.SelectedMonitor);
        Assert.Equal(new CaptureTarget.Monitor(Primary), shell.SelectedCaptureTarget);

        shell.SelectedWindow = First;
        shell.IsWindowSourceSelected = true;
        Assert.Equal(new CaptureTarget.Window(First), shell.SelectedCaptureTarget);
        Assert.True(shell.CanShareSelectedTarget);
    }

    [Fact]
    public void Share_audio_summary_follows_the_selected_capture_mode()
    {
        var shell = CreateShell();
        var changed = new List<string?>();
        shell.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        Assert.Contains("System audio", shell.ShareAudioStatus);

        shell.IsWindowSourceSelected = true;
        Assert.Contains("window", shell.ShareAudioStatus, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(nameof(shell.ShareAudioStatus), changed);
    }

    [Fact]
    public void Refresh_retains_window_only_while_handle_pid_and_creation_time_match()
    {
        var shell = CreateShell();
        var refreshed = First with { Title = "Editor - Document" };
        _windows.Items = [refreshed];
        shell.RefreshWindows();
        Assert.Equal(refreshed, shell.SelectedWindow);

        var reusedHandle = refreshed with { ProcessId = 21 };
        _windows.Items = [reusedHandle];
        shell.RefreshWindows();
        Assert.Equal(reusedHandle, shell.SelectedWindow);

        var reusedPid = reusedHandle with { ProcessStartTimeUtc = First.ProcessStartTimeUtc.AddSeconds(3) };
        _windows.Items = [reusedPid];
        shell.RefreshWindows();
        Assert.Equal(reusedPid, shell.SelectedWindow);
    }

    [Fact]
    public void Replaces_disappeared_selection_and_handles_empty_lists()
    {
        var shell = CreateShell();
        var replacement = First with { Handle = (nint)11, Title = "Browser" };
        _windows.Items = [replacement];
        shell.RefreshWindows();
        Assert.Equal(replacement, shell.SelectedWindow);

        _windows.Items = [];
        shell.RefreshWindows();
        Assert.Empty(shell.Windows);
        Assert.Null(shell.SelectedWindow);
        shell.IsWindowSourceSelected = true;
        Assert.Null(shell.SelectedCaptureTarget);
        Assert.False(shell.CanShareSelectedTarget);
        Assert.False(shell.CanStartShare);
    }

    [Fact]
    public async Task Share_command_reports_missing_window_selection()
    {
        var shell = CreateShell();
        shell.IsWindowSourceSelected = true;
        shell.SelectedWindow = null;

        await shell.ShareAsync(default);

        Assert.Contains("Select an available application window", shell.ShellError);
    }

    [Fact]
    public void Empty_monitor_enumerator_leaves_target_unselected()
    {
        var shell = new Shell(new FakeMonitorEnumerator(), _windows);
        Assert.Null(shell.SelectedMonitor);
        Assert.Null(shell.SelectedCaptureTarget);
    }

    private Shell CreateShell() => new(_monitors, _windows);

    private sealed class FakeMonitorEnumerator(params MonitorInfo[] monitors) : IMonitorEnumerator
    {
        public IReadOnlyList<MonitorInfo> List() => monitors;
    }

    private sealed class FakeWindowEnumerator(params WindowInfo[] windows) : IWindowEnumerator
    {
        public IReadOnlyList<WindowInfo> Items { get; set; } = windows;
        public IReadOnlyList<WindowInfo> List() => Items;
    }
}
