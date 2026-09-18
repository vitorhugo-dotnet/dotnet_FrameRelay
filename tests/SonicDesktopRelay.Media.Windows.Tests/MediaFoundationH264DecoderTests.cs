using System.Runtime.InteropServices;
using SonicDesktopRelay.Media;
using SonicDesktopRelay.Media.Windows;

namespace SonicDesktopRelay.Media.Windows.Tests;

public sealed class MediaFoundationH264DecoderTests
{
    private static bool Available =>
        MediaFoundationH264Encoder.IsSupported && MediaFoundationH264Decoder.IsSupported;

    [Fact]
    public void A_frame_encoded_by_the_publisher_decodes_back_to_the_same_size()
    {
        if (!Available) return;

        using var encoder = new MediaFoundationH264Encoder();
        using var decoder = new MediaFoundationH264Decoder();

        var frame = DecodeUntilOutput(encoder, decoder, 640, 360);

        Assert.Equal(640, frame.Width);
        Assert.Equal(360, frame.Height);
        Assert.Equal(640 * 360 * 4, frame.Bgra.Length);
    }

    [Fact]
    public void Decoder_accepts_h264_from_rtp_without_dimensions()
    {
        if (!Available) return;

        using var encoder = new MediaFoundationH264Encoder();
        using var decoder = new MediaFoundationH264Decoder();

        var frame = DecodeUntilOutput(
            encoder,
            decoder,
            640,
            360,
            stripTransportDimensions: true);

        Assert.Equal(640, frame.Width);
        Assert.Equal(360, frame.Height);
    }

    [Fact]
    public void Dimensionless_rtp_decoder_handles_a_mid_stream_resolution_increase()
    {
        if (!Available) return;

        using var encoder = new MediaFoundationH264Encoder();
        using var decoder = new MediaFoundationH264Decoder();
        DecodeUntilOutput(encoder, decoder, 320, 180, stripTransportDimensions: true);

        var frame = DecodeUntilOutput(
            encoder,
            decoder,
            640,
            360,
            stripTransportDimensions: true);

        Assert.Equal(640, frame.Width);
        Assert.Equal(360, frame.Height);
    }

    [Fact]
    public void A_decoder_handles_a_mid_stream_resolution_change()
    {
        if (!Available) return;

        using var encoder = new MediaFoundationH264Encoder();
        using var decoder = new MediaFoundationH264Decoder();
        DecodeUntilOutput(encoder, decoder, 640, 360);

        var frame = DecodeUntilOutput(encoder, decoder, 320, 180);

        Assert.Equal(320, frame.Width);
        Assert.Equal(180, frame.Height);
    }

    [Fact]
    public void Garbage_input_returns_null_rather_than_throwing()
    {
        if (!MediaFoundationH264Decoder.IsSupported) return;

        using var decoder = new MediaFoundationH264Decoder();

        var frame = decoder.Decode(
            new EncodedVideoSample(new byte[] { 1, 2, 3, 4 }, TimeSpan.Zero, false, 16, 16));

        Assert.Null(frame);
    }

    [Fact]
    public void The_decoder_is_named_for_diagnostics()
    {
        if (!MediaFoundationH264Decoder.IsSupported) return;

        using var decoder = new MediaFoundationH264Decoder();

        Assert.False(string.IsNullOrWhiteSpace(decoder.Name));
        Assert.Contains("264", decoder.Name, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_conversion_buffer_is_reused_between_frames_of_the_same_size()
    {
        if (!Available) return;

        using var encoder = new MediaFoundationH264Encoder();
        using var decoder = new MediaFoundationH264Decoder();

        var first = DecodeUntilOutput(encoder, decoder, 640, 360);
        var second = DecodeUntilOutput(encoder, decoder, 640, 360);

        Assert.True(MemoryMarshal.TryGetArray(first.Bgra, out var a));
        Assert.True(MemoryMarshal.TryGetArray(second.Bgra, out var b));
        Assert.Same(a.Array, b.Array);
    }

    [Fact]
    public void Decoder_survives_well_past_the_previous_401_access_unit_failure_point()
    {
        if (!Available) return;

        const int width = 640;
        const int height = 360;
        const int corpusAccessUnits = 30;
        const int targetAccessUnits = 650;
        var pixels = new byte[checked(width * height * 4)];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 0x20;
            pixels[i + 1] = 0x80;
            pixels[i + 2] = 0xE0;
            pixels[i + 3] = 0xFF;
        }

        var quality = new VideoQuality(height, 30, 1_500_000);
        var corpus = new List<EncodedVideoSample>(corpusAccessUnits);

        // Keep the stress phase decoder-only. The publisher encoder has its own native
        // ProcessOutput lifetime and is intentionally out of scope for this regression.
        using (var encoder = new MediaFoundationH264Encoder())
        {
            for (var frameIndex = 0;
                 frameIndex < corpusAccessUnits + 60 && corpus.Count < corpusAccessUnits;
                 frameIndex++)
            {
                var timestamp = TimeSpan.FromTicks(
                    frameIndex * TimeSpan.TicksPerSecond / quality.FramesPerSecond);
                var encoded = encoder.Encode(
                    new VideoFrame(width, height, pixels, timestamp),
                    quality);
                if (encoded is { } accessUnit)
                    corpus.Add(accessUnit);
            }
        }

        Assert.Equal(corpusAccessUnits, corpus.Count);
        Assert.True(corpus[0].IsKeyFrame);

        using var decoder = new MediaFoundationH264Decoder();
        var decodedFrames = 0;

        for (var accessUnitIndex = 0; accessUnitIndex < targetAccessUnits; accessUnitIndex++)
        {
            var source = corpus[accessUnitIndex % corpus.Count];
            var timestamp = TimeSpan.FromTicks(
                accessUnitIndex * TimeSpan.TicksPerSecond / quality.FramesPerSecond);
            var accessUnit = source with { Timestamp = timestamp };

            if (decoder.Decode(accessUnit) is not null)
                decodedFrames++;
        }

        Assert.True(
            decodedFrames > 396,
            $"Decoder produced only {decodedFrames} frames from {targetAccessUnits} access units.");
        Assert.Null(decoder.LastFailure);
    }

    private static VideoFrame DecodeUntilOutput(
        MediaFoundationH264Encoder encoder,
        MediaFoundationH264Decoder decoder,
        int width,
        int height,
        bool stripTransportDimensions = false)
    {
        var pixels = new byte[checked(width * height * 4)];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 0x20;
            pixels[i + 1] = 0x80;
            pixels[i + 2] = 0xE0;
            pixels[i + 3] = 0xFF;
        }

        var quality = new VideoQuality(height, 30, 1_500_000);
        for (var i = 0; i < 20; i++)
        {
            var timestamp = TimeSpan.FromTicks(i * TimeSpan.TicksPerSecond / quality.FramesPerSecond);
            var sample = encoder.Encode(new VideoFrame(width, height, pixels, timestamp), quality);
            if (sample is not { } encoded) continue;

            if (stripTransportDimensions)
                encoded = encoded with { Width = 0, Height = 0 };

            var decoded = decoder.Decode(encoded);
            if (decoded is not null) return decoded;
        }

        throw new InvalidOperationException($"Media Foundation decoder produced no frame. Last failure: {decoder.LastFailure ?? "none"}");
    }
}
