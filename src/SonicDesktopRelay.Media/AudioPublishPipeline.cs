namespace SonicDesktopRelay.Media;

/// <summary>
/// capture -> encode -> one encoded audio event for the whole publishing session.
/// The encoded sample is fanned out by RTC, never encoded once per viewer.
/// </summary>
public sealed class AudioPublishPipeline(
    IAudioCaptureSource capture,
    IAudioEncoder encoder,
    MediaSessionClock clock) : IAsyncDisposable
{
    private bool _started;
    private bool _running;
    private bool _disposed;

    public event Action<EncodedAudioSample>? SampleEncoded;

    public event Action<Exception>? Failed;

    public string EncoderName => encoder.Name;

    public async Task StartAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started) return;

        _started = true;
        _running = true;
        capture.AudioCaptured += OnAudioCaptured;

        try
        {
            await capture.StartAsync(ct);
        }
        catch
        {
            capture.AudioCaptured -= OnAudioCaptured;
            _running = false;
            _started = false;
            throw;
        }
    }

    public async Task StopAsync()
    {
        if (!_started) return;

        _started = false;
        _running = false;
        capture.AudioCaptured -= OnAudioCaptured;
        await capture.StopAsync();
    }

    private void OnAudioCaptured(AudioFrame frame)
    {
        if (!_running) return;

        var timestamp = clock.Now;
        var stamped = new AudioFrame(
            frame.Data,
            frame.SampleRate,
            frame.Channels,
            frame.SampleCount,
            timestamp);

        EncodedAudioSample? encoded;
        try
        {
            encoded = encoder.Encode(stamped);
        }
        catch (Exception e)
        {
            _running = false;
            capture.AudioCaptured -= OnAudioCaptured;
            Failed?.Invoke(e);
            return;
        }

        if (encoded is not { } sample) return;

        SampleEncoded?.Invoke(sample with { Timestamp = timestamp });
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await StopAsync();
        await capture.DisposeAsync();
        encoder.Dispose();
    }
}
