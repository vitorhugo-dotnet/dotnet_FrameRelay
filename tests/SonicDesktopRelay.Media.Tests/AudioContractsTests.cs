namespace SonicDesktopRelay.Media.Tests;

public sealed class AudioContractsTests
{
    [Fact]
    public void Audio_frame_duration_is_derived_from_sample_count_and_rate()
    {
        var frame = new AudioFrame(new byte[3840], 48_000, 2, 960, TimeSpan.FromSeconds(2));

        Assert.Equal(TimeSpan.FromMilliseconds(20), frame.Duration);
        Assert.Equal(TimeSpan.FromSeconds(2), frame.Timestamp);
    }

    [Theory]
    [InlineData(0, 2, 960)]
    [InlineData(48_000, 0, 960)]
    [InlineData(48_000, 2, -1)]
    public void Audio_frame_rejects_invalid_clock_shape(int sampleRate, int channels, int sampleCount)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new AudioFrame(ReadOnlyMemory<byte>.Empty, sampleRate, channels, sampleCount, TimeSpan.Zero));
    }

    [Fact]
    public void Encoded_sample_preserves_transport_timing_metadata()
    {
        var sample = new EncodedAudioSample(
            new byte[] { 1, 2, 3 },
            960,
            TimeSpan.FromMilliseconds(20),
            TimeSpan.FromMilliseconds(120));

        Assert.Equal(960, sample.SampleCount);
        Assert.Equal(TimeSpan.FromMilliseconds(20), sample.Duration);
        Assert.Equal(TimeSpan.FromMilliseconds(120), sample.Timestamp);
    }
}
