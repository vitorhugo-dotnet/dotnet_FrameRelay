using SonicDesktopRelay.Media;

namespace SonicDesktopRelay.Rtc;

/// <summary>
/// Sends video for one peer on a dedicated worker. At most one sample waits behind the sample
/// currently being sent; replacing that sample keeps latency bounded and asks the shared encoder
/// for a clean point so the receiver does not remain on a broken H.264 prediction chain.
/// </summary>
internal sealed class VideoSampleSendQueue : IAsyncDisposable
{
    private readonly IPeerConnection _peer;
    private readonly Action<TimeSpan> _sendDurationRecorded;
    private readonly Action _sampleDropped;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;

    private EncodedVideoSample? _pending;
    private bool _disposed;
    private long _dropped;
    private long _sendFailures;

    public VideoSampleSendQueue(
        IPeerConnection peer,
        Action<TimeSpan> sendDurationRecorded,
        Action sampleDropped,
        TimeProvider time)
    {
        _peer = peer;
        _sendDurationRecorded = sendDurationRecorded;
        _sampleDropped = sampleDropped;
        _time = time;
        _worker = Task.Run(RunAsync);
    }

    public long DroppedSamples => Interlocked.Read(ref _dropped);

    public long SendFailures => Interlocked.Read(ref _sendFailures);

    public int PendingSamples
    {
        get
        {
            lock (_gate) return _pending is null ? 0 : 1;
        }
    }

    public void Enqueue(EncodedVideoSample sample)
    {
        var replaced = false;
        var signal = false;

        lock (_gate)
        {
            if (_disposed) return;

            replaced = _pending is not null;
            _pending = sample;
            signal = !replaced;
        }

        if (replaced)
        {
            Interlocked.Increment(ref _dropped);
            _sampleDropped();
        }

        if (signal) SignalWorker();
    }

    private async Task RunAsync()
    {
        try
        {
            while (true)
            {
                await _signal.WaitAsync(_stop.Token);

                EncodedVideoSample sample;
                lock (_gate)
                {
                    if (_pending is not { } next) continue;
                    _pending = null;
                    sample = next;
                }

                var started = _time.GetTimestamp();
                try
                {
                    _peer.SendVideo(sample);
                    _sendDurationRecorded(_time.GetElapsedTime(started));
                }
                catch (Exception)
                {
                    Interlocked.Increment(ref _sendFailures);
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _pending = null;
        }

        _stop.Cancel();
        SignalWorker();
        await _worker;
        _signal.Dispose();
        _stop.Dispose();
    }

    private void SignalWorker()
    {
        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
