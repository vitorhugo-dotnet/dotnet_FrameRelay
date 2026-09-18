using NAudio.Wave;
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
            return new NAudioWasapiPlaybackSession(player);
        }
        catch
        {
            if (player is not null)
                player.Dispose();
            throw;
        }
    }
}

internal sealed class NAudioWasapiPlaybackSession(WasapiPlayer player) : IWasapiPlaybackSession
{
    public string EndpointName => player.DeviceFriendlyName ?? "Default render endpoint";

    public void Play() => player.Play();

    public void Stop() => player.Stop();

    public ValueTask DisposeAsync() => player.DisposeAsync();
}

internal sealed class BoundedPcmWaveProvider : IWaveProvider
{
    private readonly BoundedPcmBuffer _buffer;

    public BoundedPcmWaveProvider(BoundedPcmBuffer buffer, int sampleRate, int channels)
    {
        _buffer = buffer;
        WaveFormat = new WaveFormat(sampleRate, 16, channels);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(Span<byte> buffer) => _buffer.Read(buffer);
}
