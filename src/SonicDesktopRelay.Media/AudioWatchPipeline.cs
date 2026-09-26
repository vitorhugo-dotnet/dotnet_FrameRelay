using System.Buffers.Binary;

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
    private double _playbackGain = 1;

    /// <summary>Viewer-local gain; applied only to decoded signed 16-bit little-endian PCM.</summary>
    public void SetPlaybackVolume(double volumePercent, bool muted) =>
        Volatile.Write(ref _playbackGain, muted ? 0 :
            (double.IsFinite(volumePercent) ? Math.Clamp(volumePercent, 0, 100) / 100 : 1));

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
            if (frame is not null) sink.Write(ApplyGain(frame.Value));
        }
        catch (Exception e)
        {
            _failed = true;
            Failed?.Invoke(e);
        }
    }

    private AudioFrame ApplyGain(AudioFrame frame)
    {
        var gain = Volatile.Read(ref _playbackGain);
        if (gain == 1) return frame;
        var data = frame.Data.ToArray();
        for (var offset = 0; offset + 1 < data.Length; offset += 2)
        {
            var sample = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(offset, 2));
            BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(offset, 2), (short)(sample * gain));
        }
        return new AudioFrame(data, frame.SampleRate, frame.Channels, frame.SampleCount, frame.Timestamp);
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
