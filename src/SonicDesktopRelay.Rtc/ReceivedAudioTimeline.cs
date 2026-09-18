using SonicDesktopRelay.Media;

namespace SonicDesktopRelay.Rtc;

/// <summary>
/// Reconstructs the media timeline for received Opus frames from their codec duration. Network
/// arrival time is deliberately irrelevant: jitter changes when packets arrive, not when they
/// belong in the media stream.
/// </summary>
internal sealed class ReceivedAudioTimeline
{
    private const int OpusClockRate = 48_000;
    private TimeSpan _nextTimestamp;

    public EncodedAudioSample Map(ReadOnlyMemory<byte> payload, uint durationMilliseconds)
    {
        if (durationMilliseconds == 0)
            throw new ArgumentOutOfRangeException(nameof(durationMilliseconds));

        var duration = TimeSpan.FromMilliseconds(durationMilliseconds);
        var sampleCount = checked((int)(durationMilliseconds * (OpusClockRate / 1000)));
        var sample = new EncodedAudioSample(payload, sampleCount, duration, _nextTimestamp);
        _nextTimestamp += duration;
        return sample;
    }
}
