using NAudio.Wave;
using System.Runtime.InteropServices;
using SonicDesktopRelay.Media;

namespace SonicDesktopRelay.Media.Windows;

/// <summary>
/// Plays decoded 48 kHz stereo PCM through the default Windows render endpoint. Network jitter and
/// renderer hiccups are isolated by a bounded queue: old audio is dropped rather than allowing
/// latency to grow without limit.
/// </summary>
public sealed class WasapiAudioSink : IAudioSink
{
    private const int SampleRate = 48_000;
    private const int Channels = 2;
    private const int BytesPerSample = sizeof(short);
    private const int DefaultMaxBufferedMilliseconds = 200;

    private readonly IWasapiPlaybackFactory _factory;
    private readonly BoundedPcmBuffer _buffer;
    private readonly Lock _gate = new();

    private IWasapiPlaybackSession? _session;
    private bool _started;
    private bool _stopped;
    private bool _terminalFailure;
    private bool _disposed;
    private float _volume = 1f;
    private float _lastNonZeroVolume = 1f;
    private bool _isMuted;

    public WasapiAudioSink()
        : this(new NAudioWasapiPlaybackFactory(), DefaultMaxBufferedMilliseconds)
    {
    }

    public WasapiAudioSink(IWasapiPlaybackFactory factory, int maxBufferedMilliseconds = DefaultMaxBufferedMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(factory);
        if (maxBufferedMilliseconds <= 0) throw new ArgumentOutOfRangeException(nameof(maxBufferedMilliseconds));

        _factory = factory;
        var maxBytes = checked(SampleRate * Channels * BytesPerSample * maxBufferedMilliseconds / 1_000);
        _buffer = new BoundedPcmBuffer(maxBytes);
    }

    public string Name => ActiveEndpointName is { Length: > 0 }
        ? $"WASAPI — {ActiveEndpointName}"
        : "WASAPI default render endpoint";

    public string? ActiveEndpointName { get; private set; }

    public string? DegradedReason { get; private set; }

    public float Volume
    {
        get { lock (_gate) return _volume; }
        set
        {
            lock (_gate)
            {
                _volume = float.IsNaN(value) ? 0f : Math.Clamp(value, 0f, 1f);
                if (_volume > 0f) _lastNonZeroVolume = _volume;
                _isMuted = _volume == 0f;
                ApplyGain();
            }
        }
    }

    public bool IsMuted
    {
        get { lock (_gate) return _isMuted; }
        set
        {
            lock (_gate)
            {
                _isMuted = value;
                if (!value && _volume == 0f) _volume = _lastNonZeroVolume;
                ApplyGain();
            }
        }
    }

    public void SetPlaybackControls(float volume, bool isMuted)
    {
        lock (_gate)
        {
            _volume = float.IsNaN(volume) ? 0f : Math.Clamp(volume, 0f, 1f);
            if (_volume > 0f) _lastNonZeroVolume = _volume;
            _isMuted = isMuted || _volume == 0f;
            ApplyGain();
        }
    }

    private void ApplyGain() => _session?.Gain = _isMuted ? 0f : _volume;

    public Task StartAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started || _terminalFailure) return Task.CompletedTask;

            try
            {
                _session ??= _factory.Create(_buffer, SampleRate, Channels);
                ApplyGain();
                ActiveEndpointName = _session.EndpointName;
                _session.Play();
                _started = true;
                _stopped = false;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _terminalFailure = true;
                _started = false;
                DegradedReason = $"WASAPI playback unavailable: {e.Message}";
            }
        }

        return Task.CompletedTask;
    }

    public void Write(AudioFrame frame)
    {
        if (frame.SampleRate != SampleRate || frame.Channels != Channels)
            throw new ArgumentException($"WASAPI sink expects {SampleRate} Hz stereo PCM16.", nameof(frame));

        lock (_gate)
        {
            if (_disposed || !_started || _terminalFailure) return;
            _buffer.Write(frame.Data.Span);
        }
    }

    public Task StopAsync()
    {
        lock (_gate)
        {
            if (_stopped || _session is null)
            {
                _started = false;
                _stopped = true;
                return Task.CompletedTask;
            }

            try
            {
                _session.Stop();
            }
            catch (Exception e)
            {
                _terminalFailure = true;
                DegradedReason ??= $"WASAPI playback stopped with an error: {e.Message}";
            }
            finally
            {
                _started = false;
                _stopped = true;
            }
        }

        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        IWasapiPlaybackSession? session;
        lock (_gate)
        {
            if (_disposed) return;
        }

        await StopAsync().ConfigureAwait(false);

        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            session = _session;
            _session = null;
        }

        if (session is not null)
            await session.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>Factory seam that keeps NAudio out of the test double surface.</summary>
public interface IWasapiPlaybackFactory
{
    IWasapiPlaybackSession Create(BoundedPcmBuffer buffer, int sampleRate, int channels);
}

public interface IWasapiPlaybackSession : IAsyncDisposable
{
    string EndpointName { get; }

    float Gain { get; set; }

    void Play();

    void Stop();
}

internal sealed class NAudioWasapiPlaybackFactory : IWasapiPlaybackFactory
{
    private const int DeviceLatencyMilliseconds = 40;

    public IWasapiPlaybackSession Create(BoundedPcmBuffer buffer, int sampleRate, int channels)
    {
        var source = new BoundedPcmWaveProvider(buffer, sampleRate, channels);
        WasapiPlayer? player = null;
        try
        {
            player = new WasapiPlayerBuilder()
                .WithSharedMode()
                .WithEventSync()
                .WithLatency(DeviceLatencyMilliseconds)
                .WithLowLatency(required: false)
                .WithMmcssThreadPriority("Audio")
                .Build();
            player.Init(source);
            return new NAudioWasapiPlaybackSession(player, source);
        }
        catch
        {
            if (player is not null)
                player.Dispose();
            throw;
        }
    }
}

internal sealed class NAudioWasapiPlaybackSession(WasapiPlayer player, BoundedPcmWaveProvider source) : IWasapiPlaybackSession
{
    public string EndpointName => player.DeviceFriendlyName ?? "Default render endpoint";

    public float Gain { get => source.Gain; set => source.Gain = value; }

    public void Play() => player.Play();

    public void Stop() => player.Stop();

    public ValueTask DisposeAsync() => player.DisposeAsync();
}

internal sealed class BoundedPcmWaveProvider : IWaveProvider
{
    private readonly BoundedPcmBuffer _buffer;
    private float _gain = 1f;

    public BoundedPcmWaveProvider(BoundedPcmBuffer buffer, int sampleRate, int channels)
    {
        _buffer = buffer;
        WaveFormat = new WaveFormat(sampleRate, 16, channels);
    }

    public WaveFormat WaveFormat { get; }

    public float Gain { get => Volatile.Read(ref _gain); set => Volatile.Write(ref _gain, value); }

    public int Read(Span<byte> buffer)
    {
        var count = _buffer.Read(buffer);
        var gain = Gain;
        if (gain == 1f) return count;
        if (gain == 0f)
        {
            buffer[..count].Clear();
            return count;
        }

        foreach (ref var sample in MemoryMarshal.Cast<byte, short>(buffer[..(count & ~1)]))
            sample = (short)Math.Clamp((int)MathF.Round(sample * gain), short.MinValue, short.MaxValue);
        return count;
    }
}
