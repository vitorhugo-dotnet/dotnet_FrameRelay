namespace SonicDesktopRelay.Media;

/// <summary>
/// encoded Opus -> decode -> playback for the watching session.
/// Audio failure is contained here so it cannot tear down the video pipeline or RTC peer.
/// </summary>
public sealed class AudioWatchPipeline(IAudioDecoder decoder, IAudioSink sink) : IAsyncDisposable
{
    private bool _running;
    private bool _failed;
    private bool _disposed;

    public event Action<Exception>? Failed;

    public string DecoderName => decoder.Name;

    public string SinkName => sink.Name;

    public async Task StartAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_running) return;

        await sink.StartAsync(ct);
        _failed = false;
        _running = true;
    }

    public void Push(EncodedAudioSample sample)
    {
        if (!_running || _failed) return;

        try
        {
            var frame = decoder.Decode(sample);
            if (frame is not null) sink.Write(frame.Value);
        }
        catch (Exception e)
        {
            _failed = true;
            Failed?.Invoke(e);
        }
    }

    public async Task StopAsync()
    {
        if (!_running) return;

        _running = false;
        await sink.StopAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await StopAsync();
        decoder.Dispose();
        await sink.DisposeAsync();
    }
}
