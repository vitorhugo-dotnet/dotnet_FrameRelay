using System.Runtime.Versioning;
using SonicDesktopRelay.Media;

namespace SonicDesktopRelay.Media.Windows;

/// <summary>Switches only audio capture; the encoder, media clock, RTC peers and video stay alive.</summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class DiscordExcludingAudioSource : IAudioCaptureSource
{
    private readonly IWasapiLoopbackRecorderFactory _factory;
    private readonly Func<uint?> _findDiscord;
    private readonly Func<bool> _supported;
    private readonly bool _monitor;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _deliveryGate = new();
    private WasapiLoopbackAudioSource? _source;
    private CancellationTokenSource? _monitorCancellation;
    private Task? _monitorTask;
    private volatile bool _ignore;
    private bool _started;
    private bool _disposed;
    private bool _selectedIgnore;
    private uint? _selectedPid;
    private bool _selectionValid;
    private string? _failure;

    public DiscordExcludingAudioSource() : this(new NAudioWasapiLoopbackRecorderFactory(),
        DiscordDesktopProcess.FindRoot, () => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348)) { }

    public DiscordExcludingAudioSource(IWasapiLoopbackRecorderFactory factory, Func<uint?> findDiscord,
        Func<bool> supported, bool monitor = true)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _findDiscord = findDiscord ?? throw new ArgumentNullException(nameof(findDiscord));
        _supported = supported ?? throw new ArgumentNullException(nameof(supported));
        _monitor = monitor;
    }

    public event Action<AudioFrame>? AudioCaptured;
    public event Action? DiagnosticsChanged;
    public string? ActiveEndpointName => _source?.ActiveEndpointName;
    public string? DegradedReason => _failure ?? _source?.DegradedReason;

    public async Task SetIgnoreDiscordAudioAsync(bool value)
    {
        RequestIgnoreDiscordAudio(value);
        await RefreshAsync();
    }

    /// <summary>Closes capture delivery synchronously, before any lifecycle or activation wait.</summary>
    public void RequestIgnoreDiscordAudio(bool value)
    {
        lock (_deliveryGate)
        {
            if (_ignore != value) _selectionValid = false;
            _ignore = value;
        }
    }

    public async Task StartAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(ct);
        try
        {
            if (_started) return;
            _started = true;
            await RefreshCoreAsync(ct);
            if (_monitor)
            {
                _monitorCancellation = new CancellationTokenSource();
                _monitorTask = MonitorAsync(_monitorCancellation.Token);
            }
        }
        catch
        {
            _started = false;
            throw;
        }
        finally { _gate.Release(); }
    }

    public async Task RefreshAsync()
    {
        await _gate.WaitAsync();
        try { if (_started) await RefreshCoreAsync(CancellationToken.None); }
        finally { _gate.Release(); }
    }

    private async Task RefreshCoreAsync(CancellationToken ct)
    {
        var ignore = _ignore;
        uint? pid = null;
        var previousDiagnostics = (ActiveEndpointName, DegradedReason);
        try
        {
            if (ignore)
            {
                if (!_supported()) throw new PlatformNotSupportedException("Discord audio exclusion requires Windows build 20348 or later.");
                pid = _findDiscord();
            }
            if (_selectionValid && ignore == _selectedIgnore && pid == _selectedPid) return;
            await StopSourceAsync();
            _selectionValid = false;
            var recorder = ignore && pid is { } target
                ? await _factory.CreateExcludingAsync(target, 48000, 2, 16, 20)
                : _factory.Create(48000, 2, 16, 20);
            if (ct.IsCancellationRequested)
            {
                await recorder.DisposeAsync();
                ct.ThrowIfCancellationRequested();
            }
            WasapiLoopbackAudioSource? source = null;
            source = new WasapiLoopbackAudioSource(new ReadyRecorderFactory(recorder),
                () => ValidateCapture(source!));
            _source = source;
            _selectedIgnore = ignore;
            _selectedPid = pid;
            source.AudioCaptured += frame => Deliver(source, frame);
            source.CaptureDegraded += () => DiagnosticsChanged?.Invoke();
            // Cancellation has been checked after activation. Once the recorder is owned by
            // the source, let Start own its cleanup rather than cancel before ownership transfer.
            await source.StartAsync(CancellationToken.None);
            lock (_deliveryGate) _selectionValid = true;
            _failure = null;
        }
        catch (OperationCanceledException) { await StopSourceAsync(); throw; }
        catch (Exception error)
        {
            await StopSourceAsync();
            _failure = $"{(ignore ? "Discord audio exclusion" : "WASAPI loopback")} unavailable: {error.Message}";
        }
        finally
        {
            if (previousDiagnostics != (ActiveEndpointName, DegradedReason)) DiagnosticsChanged?.Invoke();
        }
    }

    private bool ValidateCapture(WasapiLoopbackAudioSource source)
    {
        lock (_deliveryGate)
        {
            if (!_selectionValid || source != _source || _ignore != _selectedIgnore) return false;
            if (_ignore)
            {
                try
                {
                    if (_findDiscord() == _selectedPid) return true;
                }
                catch { }
                // Keep invalidation sticky even if Discord exits before the next frame/poll.
                // Refresh must replace capture and its partial PCM before delivery can resume.
                _selectionValid = false;
                return false;
            }
            return true;
        }
    }

    private void Deliver(WasapiLoopbackAudioSource source, AudioFrame frame)
    {
        lock (_deliveryGate)
        {
            if (!ValidateCapture(source)) return;
            AudioCaptured?.Invoke(frame);
        }
    }

    private async Task MonitorAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        try { while (await timer.WaitForNextTickAsync(ct)) await RefreshAsync(); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private async Task StopSourceAsync()
    {
        WasapiLoopbackAudioSource? source;
        lock (_deliveryGate)
        {
            _selectionValid = false;
            source = _source;
            _source = null;
        }
        if (source is not null) await source.DisposeAsync();
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        var cancellation = _monitorCancellation;
        var monitor = _monitorTask;
        try
        {
            _started = false;
            _monitorCancellation = null;
            _monitorTask = null;
            cancellation?.Cancel();
            await StopSourceAsync();
        }
        finally
        {
            _gate.Release();
            // A monitor may already be waiting for the gate; release it before joining.
            try { if (monitor is not null) await monitor; }
            finally { cancellation?.Dispose(); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await StopAsync();
    }

    private sealed class ReadyRecorderFactory(IWasapiLoopbackRecorder recorder) : IWasapiLoopbackRecorderFactory
    {
        public IWasapiLoopbackRecorder Create(int rate, int channels, int bits, int buffer) => recorder;
    }
}
