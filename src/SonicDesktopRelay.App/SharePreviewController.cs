using SonicDesktopRelay.Media;

namespace SonicDesktopRelay.App;

/// <summary>Owns a capture source solely for the Share page preview.</summary>
internal sealed class SharePreviewController : IAsyncDisposable
{
    private static readonly VideoQuality PreviewQuality = new(360, 15, 600_000);
    private readonly Func<CaptureTarget, IScreenCaptureSource> _createSource;
    private readonly Action<Action> _postToUi;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private CancellationTokenSource _change = new();
    private IScreenCaptureSource? _source;
    private Action<VideoFrame>? _frameHandler;
    private Action<string>? _closedHandler;
    private VideoFrame? _pendingFrame;
    private bool _framePosted;
    private bool _disposed;
    private long _generation;

    public SharePreviewController(Func<CaptureTarget, IScreenCaptureSource> createSource, Action<Action> postToUi)
    {
        _createSource = createSource;
        _postToUi = postToUi;
    }

    public string PreviewStatus { get; private set; } = "Select a source to preview.";
    public event Action<VideoFrame>? FrameCaptured;
    public event Action? StatusChanged;

    public Task SetTargetAsync(CaptureTarget? target) => ReplaceAsync(target, "Select a source to preview.");

    private async Task ReplaceAsync(CaptureTarget? target, string stoppedStatus,
        long? onlyIfGeneration = null)
    {
        long generation;
        CancellationTokenSource previous;
        CancellationTokenSource current;
        lock (_sync)
        {
            if (_disposed) return;
            if (onlyIfGeneration is { } expected && expected != _generation) return;
            generation = ++_generation;
            previous = _change;
            current = _change = new CancellationTokenSource();
            previous.Cancel();
            _pendingFrame = null;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            previous.Dispose();
            await StopSourceAsync().ConfigureAwait(false);
            if (!IsCurrent(generation)) return;
            if (target is null)
            {
                SetStatus(stoppedStatus, generation);
                return;
            }

            SetStatus("Starting preview…", generation);
            IScreenCaptureSource source;
            try { source = _createSource(target); }
            catch (Exception e)
            {
                SetStatus($"Preview unavailable: {e.Message}", generation);
                return;
            }
            _source = source;
            _frameHandler = frame => OnFrame(frame, generation);
            _closedHandler = reason => _ = ReplaceAsync(null, $"Preview stopped: {reason}", generation);
            source.FrameCaptured += _frameHandler;
            source.TargetClosed += _closedHandler;
            try
            {
                await source.StartAsync(target, PreviewQuality, current.Token).ConfigureAwait(false);
                if (IsCurrent(generation)) SetStatus("Live preview", generation);
            }
            catch (OperationCanceledException) when (!IsCurrent(generation)) { }
            catch (Exception e)
            {
                if (IsCurrent(generation)) SetStatus($"Preview unavailable: {e.Message}", generation);
                await StopSourceAsync().ConfigureAwait(false);
            }
        }
        finally { _gate.Release(); }
    }

    private void OnFrame(VideoFrame frame, long generation)
    {
        lock (_sync)
        {
            if (_disposed || generation != _generation || FrameCaptured is null) return;
            // WGC reuses its buffer; preserve the pixels until the UI has blitted them.
            _pendingFrame = ResizeForPreview(frame);
            if (_framePosted) return;
            _framePosted = true;
        }
        _postToUi(DeliverFrame);
    }

    private static VideoFrame ResizeForPreview(VideoFrame frame)
    {
        if (frame.Height <= PreviewQuality.MaxHeight || frame.Width <= 0
            || frame.Bgra.Length < (long)frame.Width * frame.Height * 4)
            return new VideoFrame(frame.Width, frame.Height, frame.Bgra.ToArray(), frame.Timestamp);

        var height = PreviewQuality.MaxHeight;
        var width = Math.Max(1, (int)Math.Round((double)frame.Width * height / frame.Height));
        var pixels = new byte[checked(width * height * 4)];
        var source = frame.Bgra.Span;
        for (var y = 0; y < height; y++)
        {
            var sourceY = y * frame.Height / height;
            for (var x = 0; x < width; x++)
            {
                var sourceX = x * frame.Width / width;
                source.Slice((sourceY * frame.Width + sourceX) * 4, 4)
                    .CopyTo(pixels.AsSpan((y * width + x) * 4, 4));
            }
        }
        return new VideoFrame(width, height, pixels, frame.Timestamp);
    }

    private void DeliverFrame()
    {
        VideoFrame? frame;
        lock (_sync)
        {
            frame = _pendingFrame;
            _pendingFrame = null;
            _framePosted = false;
        }
        if (frame is not null) FrameCaptured?.Invoke(frame);
    }

    private void SetStatus(string status, long generation)
    {
        lock (_sync)
        {
            if (_disposed || generation != _generation) return;
            PreviewStatus = status;
        }
        _postToUi(() =>
        {
            if (IsCurrent(generation)) StatusChanged?.Invoke();
        });
    }

    private bool IsCurrent(long generation)
    {
        lock (_sync) return !_disposed && generation == _generation;
    }

    private async Task StopSourceAsync()
    {
        var source = _source;
        if (source is null) return;
        _source = null;
        if (_frameHandler is not null) source.FrameCaptured -= _frameHandler;
        if (_closedHandler is not null) source.TargetClosed -= _closedHandler;
        _frameHandler = null;
        _closedHandler = null;
        try { await source.StopAsync().ConfigureAwait(false); }
        catch (Exception) { /* A closed source may already have stopped itself. */ }
        try { await source.DisposeAsync().ConfigureAwait(false); }
        catch (Exception) { /* Cleanup must not prevent selection of a replacement source. */ }
    }

    internal async Task WhenIdleAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        _gate.Release();
    }

    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource previous;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            ++_generation;
            _pendingFrame = null;
            previous = _change;
            previous.Cancel();
        }
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopSourceAsync().ConfigureAwait(false);
            previous.Dispose();
        }
        finally { _gate.Release(); }
    }
}
