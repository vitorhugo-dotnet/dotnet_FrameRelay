namespace SonicDesktopRelay.Media.Windows;

/// <summary>
/// Thread-safe byte ring used between the network decode path and WASAPI's render thread.
/// Overflow drops the oldest audio so playback catches up instead of accumulating latency;
/// underrun returns silence so the render stream stays alive.
/// </summary>
public sealed class BoundedPcmBuffer
{
    private readonly byte[] _buffer;
    private readonly Lock _gate = new();
    private int _head;
    private int _count;

    public BoundedPcmBuffer(int maxBytes)
    {
        if (maxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        _buffer = new byte[maxBytes];
    }

    public int Capacity => _buffer.Length;

    public int Count
    {
        get
        {
            lock (_gate) return _count;
        }
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return;

        lock (_gate)
        {
            if (data.Length >= _buffer.Length)
            {
                data[^_buffer.Length..].CopyTo(_buffer);
                _head = 0;
                _count = _buffer.Length;
                return;
            }

            var overflow = _count + data.Length - _buffer.Length;
            if (overflow > 0)
            {
                _head = (_head + overflow) % _buffer.Length;
                _count -= overflow;
            }

            var tail = (_head + _count) % _buffer.Length;
            var first = Math.Min(data.Length, _buffer.Length - tail);
            data[..first].CopyTo(_buffer.AsSpan(tail, first));
            if (first < data.Length)
                data[first..].CopyTo(_buffer.AsSpan(0, data.Length - first));

            _count += data.Length;
        }
    }

    /// <summary>
    /// Reads queued PCM in FIFO order. The destination is always completely filled; unavailable
    /// bytes are zeroed so a WASAPI renderer can keep requesting data without stopping on underrun.
    /// </summary>
    public int Read(Span<byte> destination)
    {
        if (destination.IsEmpty) return 0;

        lock (_gate)
        {
            var available = Math.Min(destination.Length, _count);
            if (available > 0)
            {
                var first = Math.Min(available, _buffer.Length - _head);
                _buffer.AsSpan(_head, first).CopyTo(destination[..first]);
                if (first < available)
                    _buffer.AsSpan(0, available - first).CopyTo(destination[first..available]);

                _head = (_head + available) % _buffer.Length;
                _count -= available;
            }

            destination[available..].Clear();
            return destination.Length;
        }
    }

    public byte[] Snapshot()
    {
        lock (_gate)
        {
            if (_count == 0) return [];

            var snapshot = new byte[_count];
            var first = Math.Min(_count, _buffer.Length - _head);
            _buffer.AsSpan(_head, first).CopyTo(snapshot);
            if (first < _count)
                _buffer.AsSpan(0, _count - first).CopyTo(snapshot.AsSpan(first));
            return snapshot;
        }
    }
}
