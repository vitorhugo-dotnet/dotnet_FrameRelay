using System.Runtime.Versioning;
using SonicDesktopRelay.Media;

namespace SonicDesktopRelay.Media.Windows;

/// <summary>Captures a single validated top-level window using the shared WGC/D3D lifecycle.</summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class GraphicsCaptureWindowSource : IScreenCaptureSource, IScreenCaptureDiagnostics
{
    private readonly IWindowApi _windowApi;
    private readonly IScreenCaptureSource _source;
    private readonly IScreenCaptureDiagnostics _diagnostics;
    private CancellationTokenSource? _ownerMonitor;
    private IDisposable? _windowDestroyWatch;
    private int _closeNotified;

    public GraphicsCaptureWindowSource() : this(new Win32WindowApi(), new GraphicsCaptureItemSource()) { }

    internal GraphicsCaptureWindowSource(IWindowApi windowApi, IScreenCaptureSource source)
    {
        _windowApi = windowApi ?? throw new ArgumentNullException(nameof(windowApi));
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _diagnostics = source as IScreenCaptureDiagnostics
            ?? throw new ArgumentException("The capture source must expose diagnostics.", nameof(source));
        _source.FrameCaptured += frame => FrameCaptured?.Invoke(frame);
        _source.TargetClosed += NotifyClosed;
        _source.DimensionsChanged += (width, height) => DimensionsChanged?.Invoke(width, height);
    }

    public MonitorInfo Monitor => _source.Monitor;
    public CaptureTarget Target => _source.Target;
    public (int Width, int Height) CurrentDimensions => _source.CurrentDimensions;
    public long FramesArrived => _diagnostics.FramesArrived;
    public long FramesDelivered => _diagnostics.FramesDelivered;
    public long FramesDropped => _diagnostics.FramesDropped;
    public event Action<VideoFrame>? FrameCaptured;
    public event Action<string>? TargetClosed;
    public event Action<int, int>? DimensionsChanged;

    public Task StartAsync(MonitorInfo monitor, VideoQuality quality, CancellationToken ct) =>
        Task.FromException(new ArgumentException("A window target is required.", nameof(monitor)));

    public async Task StartAsync(CaptureTarget target, VideoQuality quality, CancellationToken ct)
    {
        if (target is not CaptureTarget.Window window)
            throw new ArgumentException("A window target is required.", nameof(target));

        ValidateWindow(window.Info);
        _closeNotified = 0;
        _windowDestroyWatch?.Dispose();
        _windowDestroyWatch = _windowApi.WatchWindowDestroy(window.Info.Handle,
            () => _ = HandleWindowDestroyedAsync(window.Info));
        try
        {
            await _source.StartAsync(target, quality, ct).ConfigureAwait(false);
            ValidateWindow(window.Info);
        }
        catch
        {
            _windowDestroyWatch.Dispose();
            _windowDestroyWatch = null;
            await _source.StopAsync().ConfigureAwait(false);
            throw;
        }
        _ownerMonitor?.Cancel();
        _ownerMonitor?.Dispose();
        _ownerMonitor = new CancellationTokenSource();
        _ = MonitorOwnerAsync(window.Info, _ownerMonitor.Token);
    }

    public Task StopAsync()
    {
        _ownerMonitor?.Cancel();
        _windowDestroyWatch?.Dispose();
        _windowDestroyWatch = null;
        return _source.StopAsync();
    }

    public void SetFrameRate(int framesPerSecond) => _source.SetFrameRate(framesPerSecond);

    public async ValueTask DisposeAsync()
    {
        _ownerMonitor?.Cancel();
        _ownerMonitor?.Dispose();
        _ownerMonitor = null;
        _windowDestroyWatch?.Dispose();
        _windowDestroyWatch = null;
        await _source.DisposeAsync().ConfigureAwait(false);
    }

    private void ValidateWindow(WindowInfo window)
    {
        if (window.Handle == nint.Zero || !_windowApi.IsWindow(window.Handle)
            || !_windowApi.IsVisible(window.Handle) || _windowApi.GetProcessId(window.Handle) != window.ProcessId
            || !_windowApi.TryGetProcessIdentity(window.ProcessId, out var processName, out var startTime)
            || startTime != window.ProcessStartTimeUtc
            || !string.Equals(processName, window.ProcessName, StringComparison.Ordinal)
            || !string.Equals(_windowApi.GetTitle(window.Handle).Trim(), window.Title, StringComparison.Ordinal)
            || !_windowApi.TryGetBounds(window.Handle, out var width, out var height)
            || width != window.Width || height != window.Height)
            throw new InvalidOperationException("The selected window is no longer available.");
    }

    private async Task HandleWindowDestroyedAsync(WindowInfo window)
    {
        try { await _source.StopAsync().ConfigureAwait(false); }
        finally { NotifyClosed($"Window '{window.Title}' was destroyed."); }
    }

    private async Task MonitorOwnerAsync(WindowInfo window, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                if (_windowApi.IsWindow(window.Handle)
                    && _windowApi.GetProcessId(window.Handle) == window.ProcessId
                    && _windowApi.TryGetProcessIdentity(window.ProcessId, out _, out var startTime)
                    && startTime == window.ProcessStartTimeUtc) continue;

                try { await _source.StopAsync().ConfigureAwait(false); }
                finally { NotifyClosed($"Window '{window.Title}' or its owner process exited."); }
                return;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private void NotifyClosed(string reason)
    {
        if (Interlocked.Exchange(ref _closeNotified, 1) == 0) TargetClosed?.Invoke(reason);
        _ownerMonitor?.Cancel();
    }
}
