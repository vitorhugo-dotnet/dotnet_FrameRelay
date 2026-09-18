namespace SonicDesktopRelay.Media;

public interface IAudioSink : IAsyncDisposable
{
    string Name { get; }

    Task StartAsync(CancellationToken ct);

    void Write(AudioFrame frame);

    Task StopAsync();
}
