using System.Buffers;

namespace SonicDesktopRelay.Media;

/// <summary>
/// A bounded latest-frame-wins handoff from the capture callback to the encoder.
/// The capture callback owns no frame memory after <see cref="Enqueue"/> returns.
/// </summary>
internal sealed class VideoFrameEncodeQueue : IAsyncDisposable
{
    private readonly Action<VideoFrame> _consume;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;

    private OwnedFrame? _pending;
    private bool _accepting = true;
    private int _disposed;

    public VideoFrameEncodeQueue(Action<VideoFrame> consume)
    {
        _consume = consume;
        _worker = Task.Run(WorkerAsync);
    }
    private int _droppedFrames;

    public int DroppedFrames => Volatile.Read(ref _droppedFrames);

    public bool Enqueue(VideoFrame frame)
    {
        var owned = OwnedFrame.Copy(frame);
        OwnedFrame? replaced;
        var signal = false;

        lock (_gate)
        {
            if (!_accepting)
            {
                owned.Dispose();
                return false;
            }

            replaced = _pending;
            _pending = owned;
            signal = replaced is null;
            if (replaced is not null)
                Interlocked.Increment(ref _droppedFrames);
        }

        replaced?.Dispose();
        if (signal)
            _signal.Release();
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        OwnedFrame? pending;
        lock (_gate)
        {
            if (!_accepting)
                pending = null;
            else
            {
                _accepting = false;
                pending = _pending;
                _pending = null;
            }
        }

        pending?.Dispose();
        _stop.Cancel();
        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // The worker is already awake and will observe cancellation.
        }
        await _worker.ConfigureAwait(false);
        _signal.Dispose();
        _stop.Dispose();
    }

    private async Task WorkerAsync()
    {
        try
        {
            while (true)
            {
                await _signal.WaitAsync(_stop.Token).ConfigureAwait(false);
                OwnedFrame? next;
                lock (_gate)
                {
                    next = _pending;
                    _pending = null;
                }

                if (next is null)
                    return;

                using (next)
                    _consume(next.Frame);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
    }

    private sealed class OwnedFrame(byte[] buffer, VideoFrame frame) : IDisposable
    {
        public VideoFrame Frame { get; } = frame;

        public static OwnedFrame Copy(VideoFrame source)
        {
            var length = source.Bgra.Length;
            var buffer = ArrayPool<byte>.Shared.Rent(Math.Max(1, length));
            source.Bgra.Span.CopyTo(buffer.AsSpan(0, length));
            return new OwnedFrame(buffer, new VideoFrame(
                source.Width, source.Height, buffer.AsMemory(0, length), source.Timestamp));
        }

        public void Dispose() => ArrayPool<byte>.Shared.Return(buffer);
    }
}
