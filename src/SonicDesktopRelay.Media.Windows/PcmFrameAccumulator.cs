namespace SonicDesktopRelay.Media.Windows;

/// <summary>
/// Turns arbitrary PCM callback sizes into fixed codec frames. Only one partial frame is retained,
/// so capture jitter cannot turn into an unbounded allocation queue.
/// </summary>
public sealed class PcmFrameAccumulator
{
    private readonly byte[] _pending;
    private readonly Lock _gate = new();
    private int _pendingCount;

    public PcmFrameAccumulator(int sampleRate, int channels, int bitsPerSample, int frameSamples)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));
        if (bitsPerSample <= 0 || bitsPerSample % 8 != 0)
            throw new ArgumentOutOfRangeException(nameof(bitsPerSample));
        if (frameSamples <= 0) throw new ArgumentOutOfRangeException(nameof(frameSamples));

        SampleRate = sampleRate;
        Channels = channels;
        BitsPerSample = bitsPerSample;
        FrameSamples = frameSamples;
        FrameBytes = checked(frameSamples * channels * (bitsPerSample / 8));
        _pending = new byte[FrameBytes];
    }

    public int SampleRate { get; }

    public int Channels { get; }

    public int BitsPerSample { get; }

    public int FrameSamples { get; }

    public int FrameBytes { get; }

    public int BufferedBytes
    {
        get
        {
            lock (_gate) return _pendingCount;
        }
    }

    public IReadOnlyList<byte[]> Append(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return Array.Empty<byte[]>();

        lock (_gate)
        {
            var frames = new List<byte[]>((data.Length + _pendingCount) / FrameBytes);

            if (_pendingCount > 0)
            {
                var needed = FrameBytes - _pendingCount;
                var copied = Math.Min(needed, data.Length);
                data[..copied].CopyTo(_pending.AsSpan(_pendingCount));
                _pendingCount += copied;
                data = data[copied..];

                if (_pendingCount == FrameBytes)
                {
                    frames.Add(_pending.ToArray());
                    _pendingCount = 0;
                }
            }

            while (data.Length >= FrameBytes)
            {
                frames.Add(data[..FrameBytes].ToArray());
                data = data[FrameBytes..];
            }

            if (!data.IsEmpty)
            {
                data.CopyTo(_pending);
                _pendingCount = data.Length;
            }

            return frames;
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            if (_pendingCount > 0)
                _pending.AsSpan(0, _pendingCount).Clear();
            _pendingCount = 0;
        }
    }
}
