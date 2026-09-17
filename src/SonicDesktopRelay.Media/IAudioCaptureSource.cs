namespace SonicDesktopRelay.Media;

public interface IAudioCaptureSource : IAsyncDisposable
{
    event Action<AudioFrame>? AudioCaptured;

    Task StartAsync(CancellationToken ct);

    Task StopAsync();
}
