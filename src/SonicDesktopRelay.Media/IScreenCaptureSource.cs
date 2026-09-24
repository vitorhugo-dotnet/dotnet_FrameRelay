namespace SonicDesktopRelay.Media;

public interface IMonitorEnumerator
{
    IReadOnlyList<MonitorInfo> List();
}

public interface IWindowEnumerator
{
    IReadOnlyList<WindowInfo> List();
}

public interface IScreenCaptureSource : IAsyncDisposable
{
    MonitorInfo Monitor => Target is CaptureTarget.Monitor monitor
        ? monitor.Info
        : new MonitorInfo($"HWND:{((CaptureTarget.Window)Target).Info.Handle:X}",
            ((CaptureTarget.Window)Target).Info.Title, CurrentDimensions.Width, CurrentDimensions.Height, false);

    CaptureTarget Target => new CaptureTarget.Monitor(Monitor);

    (int Width, int Height) CurrentDimensions => Target switch
    {
        CaptureTarget.Monitor monitor => (monitor.Info.Width, monitor.Info.Height),
        CaptureTarget.Window window => (window.Info.Width, window.Info.Height),
        _ => (0, 0)
    };

    event Action<VideoFrame>? FrameCaptured;

    event Action<string>? TargetClosed
    {
        add { }
        remove { }
    }

    Task StartAsync(MonitorInfo monitor, VideoQuality quality, CancellationToken ct) =>
        StartAsync(new CaptureTarget.Monitor(monitor), quality, ct);

    Task StartAsync(CaptureTarget target, VideoQuality quality, CancellationToken ct) => target switch
    {
        CaptureTarget.Monitor monitor => StartAsync(monitor.Info, quality, ct),
        CaptureTarget.Window => Task.FromException(new PlatformNotSupportedException(
            "This capture source does not support application windows.")),
        _ => Task.FromException(new ArgumentOutOfRangeException(nameof(target)))
    };

    void SetFrameRate(int framesPerSecond);

    Task StopAsync();
}

public interface IScreenCaptureDiagnostics
{
    long FramesArrived { get; }

    long FramesDelivered { get; }

    long FramesDropped { get; }
}
