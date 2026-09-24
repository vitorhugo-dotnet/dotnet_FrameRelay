namespace SonicDesktopRelay.Media;

public interface IAudioSink : IAsyncDisposable
{
    string Name { get; }

    float Volume { get; set; }

    bool IsMuted { get; set; }

    Task StartAsync(CancellationToken ct);

    void Write(AudioFrame frame);

    Task StopAsync();
}
