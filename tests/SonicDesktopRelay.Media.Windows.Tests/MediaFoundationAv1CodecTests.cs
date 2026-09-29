using SonicDesktopRelay.Media;
using SonicDesktopRelay.Media.Windows;

namespace SonicDesktopRelay.Media.Windows.Tests;

public sealed class MediaFoundationAv1CodecTests
{
    [HardwareAv1EncoderFact]
    public void Hardware_encoder_emits_tagged_AV1_samples_with_timing_and_geometry()
    {
        using var encoder = new MediaFoundationAv1Encoder();
        const int width = 640;
        const int height = 360;
        const int fps = 30;
        var quality = new VideoQuality(height, fps, 1_500_000);
        var sample = EncodeUntilOutput(encoder, quality, width, height, fps);

        Assert.Equal(VideoCodec.Av1, sample.Codec);
        Assert.Equal(width, sample.Width);
        Assert.Equal(height, sample.Height);
        Assert.Equal(TimeSpan.FromTicks(TimeSpan.TicksPerSecond / fps), sample.Duration);
        Assert.True(sample.Data.Length > 0);
    }

    [HardwareAv1MftFact]
    public void Host_hardware_MFTs_accept_the_AV1_stream_contract_when_available()
    {
        var capabilities = new MediaFoundationAv1CapabilityProbe().Detect();
        Assert.Contains(VideoCodec.Av1, capabilities.Encoders);
        Assert.Contains(VideoCodec.Av1, capabilities.Decoders);

        using var encoder = new MediaFoundationAv1Encoder();
        using var decoder = new MediaFoundationAv1Decoder();
        const int width = 640;
        const int height = 360;
        const int fps = 30;
        var pixels = new byte[width * height * 4];
        for (var index = 0; index < pixels.Length; index += 4)
        {
            pixels[index] = 0x20;
            pixels[index + 1] = 0x80;
            pixels[index + 2] = 0xE0;
            pixels[index + 3] = 0xFF;
        }

        EncodedVideoSample? encoded = null;
        var quality = new VideoQuality(height, fps, 1_500_000);
        for (var frameIndex = 0; frameIndex < 12 && encoded is null; frameIndex++)
        {
            var frame = new VideoFrame(width, height, pixels,
                TimeSpan.FromTicks(frameIndex * TimeSpan.TicksPerSecond / fps));
            encoded = encoder.Encode(frame, quality);
        }

        Assert.True(encoded.HasValue, "AV1 encoder produced no output within 12 input frames.");
        var sample = encoded!.Value;
        Assert.Equal(VideoCodec.Av1, sample.Codec);
        Assert.Equal(width, sample.Width);
        Assert.Equal(height, sample.Height);
        Assert.True(sample.Duration > TimeSpan.Zero);
        Assert.True(sample.Data.Length > 0);

        VideoFrame? decoded = null;
        for (var attempt = 0; attempt < 8 && decoded is null; attempt++)
            decoded = decoder.Decode(sample);
        Assert.NotNull(decoded);
        Assert.Equal(width, decoded!.Width);
        Assert.Equal(height, decoded.Height);
        Assert.Equal(width * height * 4, decoded.Bgra.Length);
    }

    private static EncodedVideoSample EncodeUntilOutput(
        MediaFoundationAv1Encoder encoder,
        VideoQuality quality,
        int width,
        int height,
        int fps)
    {
        var pixels = CreateFrame(width, height);
        for (var frameIndex = 0; frameIndex < 12; frameIndex++)
        {
            var frame = new VideoFrame(width, height, pixels,
                TimeSpan.FromTicks(frameIndex * TimeSpan.TicksPerSecond / fps));
            if (encoder.Encode(frame, quality) is { } sample)
                return sample;
        }

        throw new InvalidOperationException("AV1 encoder produced no output within 12 input frames.");
    }

    private static byte[] CreateFrame(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var index = 0; index < pixels.Length; index += 4)
        {
            pixels[index] = 0x20;
            pixels[index + 1] = 0x80;
            pixels[index + 2] = 0xE0;
            pixels[index + 3] = 0xFF;
        }
        return pixels;
    }
}

/// <summary>Uses xUnit's discovery-time skip metadata for compatibility with the pinned v2 runner.</summary>
public sealed class HardwareAv1MftFactAttribute : FactAttribute
{
    public HardwareAv1MftFactAttribute()
    {
        try
        {
            var capabilities = new MediaFoundationAv1CapabilityProbe().Detect();
            if (!capabilities.Encoders.Contains(VideoCodec.Av1)
                || !capabilities.Decoders.Contains(VideoCodec.Av1))
            {
                Skip = "No usable hardware AV1 MFT pair on this host: "
                    + capabilities.RejectionReasons.GetValueOrDefault(
                        VideoCodec.Av1,
                        "MFTEnumEx returned no usable hardware AV1 encoder or decoder activation.");
            }
        }
        catch (Exception exception)
        {
            Skip = $"Hardware AV1 MFT integration unavailable: {exception.GetType().Name}: {exception.Message}";
        }
    }
}

public sealed class HardwareAv1EncoderFactAttribute : FactAttribute
{
    public HardwareAv1EncoderFactAttribute()
    {
        try
        {
            var capabilities = new MediaFoundationAv1CapabilityProbe().Detect();
            if (!capabilities.Encoders.Contains(VideoCodec.Av1))
            {
                Skip = "No usable hardware AV1 encoder on this host: "
                    + capabilities.RejectionReasons.GetValueOrDefault(
                        VideoCodec.Av1,
                        "MFTEnumEx returned no hardware AV1 encoder activation.");
            }
        }
        catch (Exception exception)
        {
            Skip = $"Hardware AV1 encoder integration unavailable: {exception.GetType().Name}: {exception.Message}";
        }
    }
}
