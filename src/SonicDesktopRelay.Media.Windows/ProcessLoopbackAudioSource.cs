using System.Runtime.Versioning;
using SonicDesktopRelay.Media;

namespace SonicDesktopRelay.Media.Windows;

internal interface IProcessLoopbackClientFactory
{
    IProcessLoopbackClient Create(uint targetPid, bool includeProcessTree, int sampleRate, int channels, int bitsPerSample, int frameSamples);
}

internal interface IProcessLoopbackClient : IAsyncDisposable
{
    event WasapiPcmDataAvailableHandler? DataAvailable;
    event Action<Exception?>? Stopped;
    void Start();
    void Stop();
}

/// <summary>Captures normalized PCM from a selected process tree, without falling back to system audio.</summary>
public sealed class ProcessLoopbackAudioSource : IAudioCaptureSource
{
    public const int SampleRate = 48_000;
    public const int Channels = 2;
    public const int BitsPerSample = 16;
    public const int FrameSamples = 960;

    private readonly WindowInfo _target;
    private readonly IProcessLoopbackClientFactory _factory;
    private readonly bool _isSupported;
    private readonly PcmFrameAccumulator _accumulator = new(SampleRate, Channels, BitsPerSample, FrameSamples);
    private readonly object _gate = new();
    private IProcessLoopbackClient? _client;
    private bool _acceptAudio;
    private bool _disposed;

    public ProcessLoopbackAudioSource(WindowInfo target) : this(target, new ProcessLoopbackClientFactory(), null) { }

    internal ProcessLoopbackAudioSource(WindowInfo target, IProcessLoopbackClientFactory factory, bool? isSupported = null)
    {
        _target = target;
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _isSupported = isSupported ?? IsSupported;
    }

    public static bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348);
    public uint TargetProcessId => _target.ProcessId;
    public string TargetProcessName => _target.ProcessName;
    public bool IncludesProcessTree => true;
    public bool IsAvailable { get; private set; }
    public string? DegradedReason { get; private set; }
    public event Action<AudioFrame>? AudioCaptured;

    public Task StartAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_client is not null) return Task.CompletedTask;
            if (!_isSupported)
            {
                DegradedReason = "Per-process audio capture requires Windows build 20348 or later.";
                return Task.CompletedTask;
            }

            IProcessLoopbackClient client;
            try
            {
                client = _factory.Create(_target.ProcessId, true, SampleRate, Channels, BitsPerSample, FrameSamples);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                DegradedReason = $"Per-process audio capture is unavailable: {e.Message}";
                return Task.CompletedTask;
            }

            _client = client;
            _acceptAudio = true;
            _accumulator.Reset();
            client.DataAvailable += OnDataAvailable;
            client.Stopped += OnStopped;
            try
            {
                client.Start();
                IsAvailable = true;
                DegradedReason = null;
            }
            catch (Exception e)
            {
                DegradedReason = $"Per-process audio capture failed to start: {e.Message}";
                _acceptAudio = false;
                client.DataAvailable -= OnDataAvailable;
                client.Stopped -= OnStopped;
                _client = null;
                _ = client.DisposeAsync();
            }
        }
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        IProcessLoopbackClient? client;
        lock (_gate)
        {
            client = _client;
            _client = null;
            _acceptAudio = false;
            IsAvailable = false;
            _accumulator.Reset();
            if (client is not null)
            {
                client.DataAvailable -= OnDataAvailable;
                client.Stopped -= OnStopped;
            }
        }
        if (client is null) return;
        try { client.Stop(); }
        finally { await client.DisposeAsync().ConfigureAwait(false); }
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
            AudioCaptured?.Invoke(new AudioFrame(frame, SampleRate, Channels, FrameSamples, TimeSpan.Zero));
    }

    private void OnStopped(Exception? error)
    {
        if (error is null) return;
        lock (_gate)
        {
            if (!_acceptAudio) return;
            _acceptAudio = false;
            IsAvailable = false;
            _accumulator.Reset();
            DegradedReason ??= $"Per-process audio capture stopped: {error.Message}";
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await StopAsync().ConfigureAwait(false);
    }
}
