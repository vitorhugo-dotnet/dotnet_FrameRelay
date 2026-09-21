namespace SonicDesktopRelay.Media;

public interface IMonitorEnumerator
{
    IReadOnlyList<MonitorInfo> List();
}

public interface IScreenCaptureSource : IAsyncDisposable
{
    MonitorInfo Monitor { get; }

    event Action<VideoFrame>? FrameCaptured;

    Task StartAsync(MonitorInfo monitor, VideoQuality quality, CancellationToken ct);

    void SetFrameRate(int framesPerSecond);

    Task StopAsync();
}
