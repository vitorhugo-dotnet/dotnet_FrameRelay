using SonicDesktopRelay.Media.Windows;
using Xunit;

namespace SonicDesktopRelay.Media.Windows.Tests;

public sealed class PcmFrameAccumulatorTests
{
    [Fact]
    public void Arbitrary_chunks_emit_exact_20ms_stereo_frames()
    {
        var acc = new PcmFrameAccumulator(sampleRate: 48_000, channels: 2, bitsPerSample: 16, frameSamples: 960);
        var output = new List<byte[]>();

        output.AddRange(acc.Append(new byte[1_000]));
        output.AddRange(acc.Append(new byte[3_000]));

        var frame = Assert.Single(output);
        Assert.Equal(960 * 2 * sizeof(short), frame.Length);
        Assert.Equal(160, acc.BufferedBytes);
    }

    [Fact]
    public void Remainder_is_preserved_across_callbacks()
    {
        var acc = new PcmFrameAccumulator(48_000, 2, 16, 960);
        var first = Enumerable.Range(0, 2_000).Select(i => (byte)(i % 251)).ToArray();
        var second = Enumerable.Range(2_000, 1_840).Select(i => (byte)(i % 251)).ToArray();

        Assert.Empty(acc.Append(first));
        var frame = Assert.Single(acc.Append(second));

        Assert.Equal(first.Concat(second), frame);
        Assert.Equal(0, acc.BufferedBytes);
    }

    [Fact]
    public void Reset_discards_partial_frame_from_the_previous_device_stream()
    {
        var acc = new PcmFrameAccumulator(48_000, 2, 16, 960);
        Assert.Empty(acc.Append(new byte[2_000]));

        acc.Reset();

        Assert.Equal(0, acc.BufferedBytes);
        Assert.Empty(acc.Append(new byte[2_000]));
        Assert.Single(acc.Append(new byte[1_840]));
    }
}
