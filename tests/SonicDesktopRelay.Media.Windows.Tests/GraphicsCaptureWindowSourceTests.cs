using SonicDesktopRelay.Media;
using SonicDesktopRelay.Media.Windows;

namespace SonicDesktopRelay.Media.Windows.Tests;

public sealed class GraphicsCaptureWindowSourceTests
{
    private static readonly DateTime Started = new(2025, 2, 3, 4, 5, 6, DateTimeKind.Utc);

    [Fact]
    public async Task Rejects_stale_window_before_starting_capture_resources()
    {
        var api = new FakeWindowApi { Valid = false };
        var capture = new FakeCaptureSource();
        var source = new GraphicsCaptureWindowSource(api, capture);
        var target = new CaptureTarget.Window(new WindowInfo((nint)44, 12, Started, "Editor", "editor", 640, 480));

        await Assert.ThrowsAsync<InvalidOperationException>(() => source.StartAsync(target, Quality, default));

        Assert.Equal(0, capture.StartCalls);
    }

    [Fact]
    public async Task Delegates_target_and_stop_to_shared_capture_source()
    {
        var api = new FakeWindowApi();
        var capture = new FakeCaptureSource();
        var source = new GraphicsCaptureWindowSource(api, capture);
        var target = new CaptureTarget.Window(new WindowInfo((nint)44, 12, Started, "Editor", "editor", 640, 480));

        await source.StartAsync(target, Quality, default);
        await source.StopAsync();

        Assert.Equal(target, capture.StartedTarget);
        Assert.Equal(1, capture.StopCalls);
        await source.DisposeAsync();
    }

    private static VideoQuality Quality => new(720, 30, 1000);

    private sealed class FakeWindowApi : IWindowApi
    {
        public bool Valid { get; init; } = true;
        public IEnumerable<nint> EnumerateTopLevelWindows() => [(nint)44];
        public bool IsWindow(nint handle) => Valid;
        public bool IsVisible(nint handle) => Valid;
        public bool IsToolWindow(nint handle) => false;
        public bool IsShellWindow(nint handle) => false;
        public string GetTitle(nint handle) => "Editor";
        public uint GetProcessId(nint handle) => 12;
        public bool TryGetBounds(nint handle, out int width, out int height) { width = 640; height = 480; return true; }
        public bool TryGetProcessIdentity(uint processId, out string processName, out DateTime startTimeUtc)
        { processName = "editor"; startTimeUtc = Started; return Valid; }
    }

    private sealed class FakeCaptureSource : IScreenCaptureSource, IScreenCaptureDiagnostics
    {
        public CaptureTarget? StartedTarget { get; private set; }
        public int StartCalls { get; private set; }
        public int StopCalls { get; private set; }
        public MonitorInfo Monitor => new("fake", "fake", 640, 480, true);
        public CaptureTarget Target => StartedTarget ?? new CaptureTarget.Monitor(Monitor);
        public (int Width, int Height) CurrentDimensions => (640, 480);
        public event Action<VideoFrame>? FrameCaptured { add { } remove { } }
        public event Action<string>? TargetClosed { add { } remove { } }
        public long FramesArrived => 0;
        public long FramesDelivered => 0;
        public long FramesDropped => 0;
        public Task StartAsync(MonitorInfo monitor, VideoQuality quality, CancellationToken ct) => Task.CompletedTask;
        public Task StartAsync(CaptureTarget target, VideoQuality quality, CancellationToken ct)
        { StartCalls++; StartedTarget = target; return Task.CompletedTask; }
        public void SetFrameRate(int framesPerSecond) { }
        public Task StopAsync() { StopCalls++; return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
