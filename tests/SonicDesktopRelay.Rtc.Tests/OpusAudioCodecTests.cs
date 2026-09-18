using System.Buffers.Binary;
using SonicDesktopRelay.Media;
using SonicDesktopRelay.Rtc;
using Xunit;

namespace SonicDesktopRelay.Rtc.Tests;

public sealed class OpusAudioCodecTests
{
    [Fact]
    public void Twenty_ms_stereo_pcm_round_trips_through_opus()
    {
        using var codec = new OpusAudioCodec(channels: 2);
        var input = new AudioFrame(
            SinePcm16Stereo(sampleCount: 960),
            sampleRate: 48_000,
            channels: 2,
            sampleCount: 960,
            timestamp: TimeSpan.FromMilliseconds(120));

        var encoded = codec.Encode(input);

        Assert.NotNull(encoded);
        Assert.NotEmpty(encoded!.Value.Data.ToArray());
        Assert.Equal(960, encoded.Value.SampleCount);
        Assert.Equal(TimeSpan.FromMilliseconds(20), encoded.Value.Duration);
        Assert.Equal(input.Timestamp, encoded.Value.Timestamp);

        var decoded = codec.Decode(encoded.Value);

        Assert.NotNull(decoded);
        Assert.Equal(48_000, decoded!.Value.SampleRate);
        Assert.Equal(2, decoded.Value.Channels);
        Assert.Equal(960, decoded.Value.SampleCount);
        Assert.Equal(960 * 2 * sizeof(short), decoded.Value.Data.Length);
        Assert.Equal(input.Timestamp, decoded.Value.Timestamp);
    }

    private static byte[] SinePcm16Stereo(int sampleCount)
    {
        var bytes = new byte[sampleCount * 2 * sizeof(short)];
        for (var i = 0; i < sampleCount; i++)
        {
            var sample = (short)(Math.Sin(2 * Math.PI * 440 * i / 48_000d) * short.MaxValue * 0.25);
            var offset = i * 2 * sizeof(short);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(offset, sizeof(short)), sample);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(offset + sizeof(short), sizeof(short)), sample);
        }

        return bytes;
    }
}
