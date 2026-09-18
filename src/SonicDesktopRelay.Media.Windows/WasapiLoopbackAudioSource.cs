using NAudio.Wave;
using SonicDesktopRelay.Media;
using System.Runtime.Versioning;

namespace SonicDesktopRelay.Media.Windows;

public delegate void WasapiPcmDataAvailableHandler(ReadOnlySpan<byte> data);

public interface IWasapiLoopbackRecorder : IAsyncDisposable
{
    string EndpointId { get; }

    string EndpointName { get; }

    event WasapiPcmDataAvailableHandler? DataAvailable;

    event Action<Exception?>? Stopped;

    void Start();

    void Stop();
}

public interface IWasapiLoopbackRecorderFactory
{
    IWasapiLoopbackRecorder Create(int sampleRate, int channels, int bitsPerSample, int bufferMilliseconds);
}

/// <summary>
/// Captures the current default Windows render endpoint in loopback mode and exposes exact
/// 20 ms PCM16 stereo frames at 48 kHz. Device failures degrade audio without throwing from
/// late WASAPI callbacks, allowing the video path to continue independently.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class WasapiLoopbackAudioSource : IAudioCaptureSource
{
    private const int OutputSampleRate = 48_000;
    private const int OutputChannels = 2;
    public const int BitsPerSample = 16;
    public const int FrameSamples = 960;
    public const int BufferMilliseconds = 20;

    private readonly IWasapiLoopbackRecorderFactory _factory;
    private readonly PcmFrameAccumulator _accumulator = new(
        OutputSampleRate,
        OutputChannels,
        BitsPerSample,
        FrameSamples);
    private readonly Lock _gate = new();

    private IWasapiLoopbackRecorder? _recorder;
    private bool _started;
    private bool _acceptAudio;
    private bool _disposed;

    public WasapiLoopbackAudioSource()
        : this(new NAudioWasapiLoopbackRecorderFactory())
    {
    }

    public WasapiLoopbackAudioSource(IWasapiLoopbackRecorderFactory factory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    public event Action<AudioFrame>? AudioCaptured;

    public string? ActiveEndpointId { get; private set; }

    public string? ActiveEndpointName { get; private set; }

    public int NormalizedSampleRate => OutputSampleRate;

    public int NormalizedChannels => OutputChannels;

    public string? DegradedReason { get; private set; }

    public async Task StartAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();

        IWasapiLoopbackRecorder recorder;
        lock (_gate)
        {
            if (_started) return;

            recorder = _factory.Create(
                OutputSampleRate,
                OutputChannels,
                BitsPerSample,
                BufferMilliseconds);

            _recorder = recorder;
            _started = true;
            _acceptAudio = true;
            DegradedReason = null;
            ActiveEndpointId = recorder.EndpointId;
            ActiveEndpointName = recorder.EndpointName;
            _accumulator.Reset();
            recorder.DataAvailable += OnDataAvailable;
            recorder.Stopped += OnRecorderStopped;
        }

        try
        {
            recorder.Start();
        }
        catch (Exception e)
        {
            lock (_gate)
            {
                _acceptAudio = false;
                _started = false;
                DegradedReason = $"WASAPI loopback failed to start: {e.Message}";
                if (ReferenceEquals(_recorder, recorder)) _recorder = null;
                recorder.DataAvailable -= OnDataAvailable;
                recorder.Stopped -= OnRecorderStopped;
                _accumulator.Reset();
            }

            await recorder.DisposeAsync();
            throw;
        }
    }

    public async Task StopAsync()
    {
        IWasapiLoopbackRecorder? recorder;
        lock (_gate)
        {
            if (!_started && _recorder is null) return;

            recorder = _recorder;
            _recorder = null;
            _started = false;
            _acceptAudio = false;
            _accumulator.Reset();

            if (recorder is not null)
            {
                recorder.DataAvailable -= OnDataAvailable;
                recorder.Stopped -= OnRecorderStopped;
            }
        }

        if (recorder is null) return;

        try
        {
            recorder.Stop();
        }
        finally
        {
            await recorder.DisposeAsync();
        }
    }

    private void OnDataAvailable(ReadOnlySpan<byte> data)
    {
        IReadOnlyList<byte[]> frames;
        lock (_gate)
        {
            if (!_acceptAudio || _disposed) return;
            frames = _accumulator.Append(data);
        }

        foreach (var frame in frames)
        {
            AudioCaptured?.Invoke(new AudioFrame(
                frame,
                OutputSampleRate,
                OutputChannels,
                FrameSamples,
                TimeSpan.Zero));
        }
    }

    private void OnRecorderStopped(Exception? error)
    {
        if (error is null) return;

        lock (_gate)
        {
            if (!_started || !_acceptAudio) return;

            _acceptAudio = false;
            DegradedReason ??= $"WASAPI loopback stopped: {error.Message}";
            _accumulator.Reset();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await StopAsync();
    }
}

[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class NAudioWasapiLoopbackRecorderFactory : IWasapiLoopbackRecorderFactory
{
    public IWasapiLoopbackRecorder Create(int sampleRate, int channels, int bitsPerSample, int bufferMilliseconds)
        => new NAudioWasapiLoopbackRecorder(sampleRate, channels, bitsPerSample, bufferMilliseconds);
}

[SupportedOSPlatform("windows10.0.19041.0")]
internal sealed class NAudioWasapiLoopbackRecorder : IWasapiLoopbackRecorder
{
    private readonly WasapiRecorder _recorder;
    private bool _disposed;

    public NAudioWasapiLoopbackRecorder(int sampleRate, int channels, int bitsPerSample, int bufferMilliseconds)
    {
        var format = new WaveFormat(sampleRate, bitsPerSample, channels);
        _recorder = new WasapiRecorderBuilder()
            .WithSharedMode()
            .WithEventSync()
            .WithLoopbackCapture()
            .WithBufferLength(bufferMilliseconds)
            .WithFormat(format)
            .WithMmcssThreadPriority("Audio")
            .Build();

        EndpointId = _recorder.DeviceId ?? "default-render";
        EndpointName = _recorder.DeviceFriendlyName ?? "Default render endpoint";
        _recorder.DataAvailable += OnDataAvailable;
        _recorder.RecordingStopped += OnRecordingStopped;
    }

    public string EndpointId { get; }

    public string EndpointName { get; }

    public event WasapiPcmDataAvailableHandler? DataAvailable;

    public event Action<Exception?>? Stopped;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _recorder.StartRecording();
    }

    public void Stop()
    {
        if (_disposed) return;
        _recorder.StopRecording();
    }

    private void OnDataAvailable(
        ReadOnlySpan<byte> data,
        NAudio.CoreAudioApi.AudioClientBufferFlags flags,
        long devicePosition,
        long qpcPosition)
    {
        DataAvailable?.Invoke(data);
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
        => Stopped?.Invoke(e.Exception);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _recorder.DataAvailable -= OnDataAvailable;
        _recorder.RecordingStopped -= OnRecordingStopped;
        await _recorder.DisposeAsync();
    }
}
