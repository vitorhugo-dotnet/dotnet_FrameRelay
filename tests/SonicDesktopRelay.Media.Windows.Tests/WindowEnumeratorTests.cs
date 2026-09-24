using SonicDesktopRelay.Media;
using SonicDesktopRelay.Media.Windows;

namespace SonicDesktopRelay.Media.Windows.Tests;

public sealed class WindowEnumeratorTests
{
    private static readonly DateTime ProcessStart = new(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    [Fact]
    public void Lists_only_visible_titled_valid_non_tool_windows_owned_by_other_processes()
    {
        var eligible = new FakeWindow((nint)1, 101, "Editor", true, true, false, false, 1280, 720);
        var api = new FakeWindowApi(
            eligible,
            new FakeWindow((nint)2, 102, "Hidden", false, true, false, false, 800, 600),
            new FakeWindow((nint)3, 103, "", true, true, false, false, 800, 600),
            new FakeWindow((nint)4, 104, "Tool", true, true, true, false, 300, 100),
            new FakeWindow((nint)5, 105, "Shell", true, true, false, true, 1920, 1080),
            new FakeWindow((nint)6, 42, "FrameRelay", true, true, false, false, 1000, 700),
            new FakeWindow((nint)7, 107, "No process identity", true, false, false, false, 700, 500));

        var windows = new WindowEnumerator(api, currentProcessId: 42).List();

        Assert.Equal(
            [new WindowInfo((nint)1, 101, ProcessStart, "Editor", "editor", 1280, 720)],
            windows);
    }

    [Fact]
    public void Discards_a_window_if_its_handle_is_destroyed_during_metadata_lookup()
    {
        var api = new FakeWindowApi(
            new FakeWindow((nint)9, 109, "Closing", true, true, false, false, 640, 480))
        {
            DestroyAfterMetadataLookup = (nint)9
        };

        var windows = new WindowEnumerator(api, currentProcessId: 42).List();

        Assert.Empty(windows);
    }

    [Fact]
    public void Discards_a_window_if_its_handle_is_reused_by_another_process_during_lookup()
    {
        var api = new FakeWindowApi(
            new FakeWindow((nint)10, 110, "Reused", true, true, false, false, 640, 480))
        {
            ChangedProcessAfterMetadataLookup = (nint)10,
            ReplacementProcessId = 111
        };

        var windows = new WindowEnumerator(api, currentProcessId: 42).List();

        Assert.Empty(windows);
    }

    [Fact]
    public void Discards_a_window_if_the_pid_was_reused_by_a_new_process_during_lookup()
    {
        var api = new FakeWindowApi(
            new FakeWindow((nint)13, 113, "Reused PID", true, true, false, false, 640, 480))
        {
            ChangedProcessStartTimeForProcessId = 113,
            ReplacementProcessStartTimeUtc = ProcessStart.AddSeconds(1)
        };

        var windows = new WindowEnumerator(api, currentProcessId: 42).List();

        Assert.Empty(windows);
    }

    [Fact]
    public void Item_factory_routes_monitor_and_window_targets_to_the_matching_creation_path()
    {
        var monitor = new MonitorInfo("DISPLAY2", "Second", 1920, 1080, false);
        var window = new WindowInfo((nint)12, 112, ProcessStart, "Editor", "editor", 800, 600);
        MonitorInfo? monitorSeen = null;
        WindowInfo? windowSeen = null;
        var factory = new GraphicsCaptureItemFactory(
            createForMonitor: target =>
            {
                monitorSeen = target;
                throw new InvalidOperationException("monitor route observed");
            },
            createForWindow: target =>
            {
                windowSeen = target;
                throw new InvalidOperationException("window route observed");
            });

        Assert.IsType<InvalidOperationException>(Record.Exception(() =>
        {
            _ = factory.CreateForMonitor(monitor);
        }));
        Assert.IsType<InvalidOperationException>(Record.Exception(() =>
        {
            _ = factory.CreateForWindow(window);
        }));

        Assert.Equal(monitor, monitorSeen);
        Assert.Equal(window, windowSeen);
    }

    private sealed record FakeWindow(
        nint Handle,
        uint ProcessId,
        string Title,
        bool Visible,
        bool HasProcessIdentity,
        bool IsToolWindow,
        bool IsShellWindow,
        int Width,
        int Height);

    private sealed class FakeWindowApi(params FakeWindow[] windows) : IWindowApi
    {
        private readonly Dictionary<nint, int> _windowChecks = [];
        private readonly Dictionary<nint, int> _processIdChecks = [];
        private readonly Dictionary<uint, int> _processIdentityChecks = [];

        public nint? DestroyAfterMetadataLookup { get; init; }

        public nint? ChangedProcessAfterMetadataLookup { get; init; }

        public uint ReplacementProcessId { get; init; }

        public uint? ChangedProcessStartTimeForProcessId { get; init; }

        public DateTime ReplacementProcessStartTimeUtc { get; init; }

        public IEnumerable<nint> EnumerateTopLevelWindows() => windows.Select(x => x.Handle);

        public bool IsWindow(nint handle)
        {
            var check = _windowChecks.GetValueOrDefault(handle) + 1;
            _windowChecks[handle] = check;
            return handle != DestroyAfterMetadataLookup || check == 1;
        }

        public bool IsVisible(nint handle) => Find(handle).Visible;

        public bool IsToolWindow(nint handle) => Find(handle).IsToolWindow;

        public bool IsShellWindow(nint handle) => Find(handle).IsShellWindow;

        public string GetTitle(nint handle) => Find(handle).Title;

        public uint GetProcessId(nint handle)
        {
            var check = _processIdChecks.GetValueOrDefault(handle) + 1;
            _processIdChecks[handle] = check;
            return handle == ChangedProcessAfterMetadataLookup && check > 1
                ? ReplacementProcessId
                : Find(handle).ProcessId;
        }

        public bool TryGetBounds(nint handle, out int width, out int height)
        {
            width = Find(handle).Width;
            height = Find(handle).Height;
            return true;
        }

        public bool TryGetProcessIdentity(uint processId, out string processName, out DateTime startTimeUtc)
        {
            var check = _processIdentityChecks.GetValueOrDefault(processId) + 1;
            _processIdentityChecks[processId] = check;
            var window = windows.FirstOrDefault(x => x.ProcessId == processId);
            if (window is null || !window.HasProcessIdentity)
            {
                processName = string.Empty;
                startTimeUtc = default;
                return false;
            }

            processName = "editor";
            startTimeUtc = processId == ChangedProcessStartTimeForProcessId && check > 1
                ? ReplacementProcessStartTimeUtc
                : ProcessStart;
            return true;
        }

        private FakeWindow Find(nint handle) => windows.Single(x => x.Handle == handle);
    }
}
