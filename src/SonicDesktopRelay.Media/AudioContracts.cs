namespace SonicDesktopRelay.Media;

public readonly record struct AudioFrame
{
    public AudioFrame(
        ReadOnlyMemory<byte> data,
        int sampleRate,
        int channels,
        int sampleCount,
        TimeSpan timestamp)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));
        if (sampleCount < 0) throw new ArgumentOutOfRangeException(nameof(sampleCount));

        Data = data;
        SampleRate = sampleRate;
        Channels = channels;
        SampleCount = sampleCount;
        Timestamp = timestamp;
    }

    public ReadOnlyMemory<byte> Data { get; }

    public int SampleRate { get; }

    public int Channels { get; }

    public int SampleCount { get; }

    public TimeSpan Timestamp { get; }

    public TimeSpan Duration => TimeSpan.FromSeconds(SampleCount / (double)SampleRate);
}

public readonly record struct EncodedAudioSample(
    ReadOnlyMemory<byte> Data,
    int SampleCount,
    TimeSpan Duration,
    TimeSpan Timestamp);
