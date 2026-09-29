using System.Runtime.Versioning;
using SonicDesktopRelay.Media;

namespace SonicDesktopRelay.Media.Windows;

internal interface IProcessLoopbackClientFactory
{
    Task<IProcessLoopbackClient> CreateAsync(uint targetPid, bool includeProcessTree, int sampleRate, int channels, int bitsPerSample, int frameSamples);
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
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
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
    public string ActivationResult { get; private set; } = "not-started";
    public event Action<AudioFrame>? AudioCaptured;

    public async Task StartAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();
        await _lifecycleGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            lock (_gate)
                if (_client is not null) return;

            if (!_isSupported)
            {
                DegradedReason = "Per-process audio capture requires Windows build 20348 or later.";
                ActivationResult = "unsupported_os";
                return;
            }

            IProcessLoopbackClient client;
            try
            {
                client = await _factory.CreateAsync(_target.ProcessId, true, SampleRate, Channels, BitsPerSample, FrameSamples)
                    .ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                DegradedReason = $"Per-process audio capture is unavailable: {e.Message}";
                ActivationResult = "activation_failed";
                return;
            }

            var disposedDuringActivation = false;
            lock (_gate)
            {
                if (_disposed)
                {
                    disposedDuringActivation = true;
                }
                else
                {
                    _client = client;
                    _acceptAudio = true;
                    _accumulator.Reset();
                    client.DataAvailable += OnDataAvailable;
                    client.Stopped += OnStopped;
                }
            }

            if (disposedDuringActivation)
            {
                await client.DisposeAsync().ConfigureAwait(false);
                throw new ObjectDisposedException(nameof(ProcessLoopbackAudioSource));
            }

            try
            {
                client.Start();
                IsAvailable = true;
                DegradedReason = null;
                ActivationResult = "started";
            }
            catch (Exception e)
            {
                DegradedReason = $"Per-process audio capture failed to start: {e.Message}";
                ActivationResult = "start_failed";
                lock (_gate)
                {
                    _acceptAudio = false;
                    client.DataAvailable -= OnDataAvailable;
                    client.Stopped -= OnStopped;
                    _client = null;
                }
                await client.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task StopAsync()
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
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
        finally
        {
            _lifecycleGate.Release();
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
            ActivationResult = "stopped_with_error";
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await StopAsync().ConfigureAwait(false);
    }
}
