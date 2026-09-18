using Xunit;

namespace SonicDesktopRelay.Media.Windows.Tests;

public sealed class BoundedPcmBufferTests
{
    [Fact]
    public void Overflow_discards_oldest_audio_instead_of_growing_latency()
    {
        var buffer = new BoundedPcmBuffer(maxBytes: 8);

        buffer.Write(new byte[] { 1, 2, 3, 4, 5, 6 });
        buffer.Write(new byte[] { 7, 8, 9, 10 });

        Assert.Equal(new byte[] { 3, 4, 5, 6, 7, 8, 9, 10 }, buffer.Snapshot());
    }

    [Fact]
    public void Reads_are_fifo_and_remove_consumed_bytes()
    {
        var buffer = new BoundedPcmBuffer(maxBytes: 16);
        buffer.Write(new byte[] { 1, 2, 3, 4, 5, 6 });
        Span<byte> output = stackalloc byte[4];

        buffer.Read(output);

        Assert.Equal(new byte[] { 1, 2, 3, 4 }, output.ToArray());
        Assert.Equal(new byte[] { 5, 6 }, buffer.Snapshot());
    }

    [Fact]
    public void Underrun_returns_silence_for_the_missing_tail()
    {
        var buffer = new BoundedPcmBuffer(maxBytes: 16);
        buffer.Write(new byte[] { 9, 8 });
        Span<byte> output = stackalloc byte[6];
        output.Fill(0xFF);

        var read = buffer.Read(output);

        Assert.Equal(output.Length, read);
        Assert.Equal(new byte[] { 9, 8, 0, 0, 0, 0 }, output.ToArray());
        Assert.Empty(buffer.Snapshot());
    }
}
