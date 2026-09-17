using SonicDesktopRelay.Media;
using SonicDesktopRelay.Media.Windows;

namespace SonicDesktopRelay.Media.Windows.Tests;

public sealed class MediaFoundationH264EncoderTests
{
    [Fact]
    public void The_selected_encoder_is_named()
    {
        if (!MediaFoundationH264Encoder.IsSupported) return;

        using var encoder = new MediaFoundationH264Encoder();

        Assert.False(string.IsNullOrWhiteSpace(encoder.Name));
        Assert.Contains("264", encoder.Name, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_first_encoded_access_unit_is_a_keyframe()
    {
        if (!MediaFoundationH264Encoder.IsSupported) return;

        using var encoder = new MediaFoundationH264Encoder();

        var sample = EncodeUntilOutput(encoder, 640, 360);

        Assert.True(sample.IsKeyFrame);
        Assert.True(sample.Data.Length > 4);
        Assert.True(sample.Data.Span.StartsWith(new byte[] { 0, 0, 0, 1 }));
    }

    [Fact]
    public void Encoding_honours_the_quality_height()
    {
        if (!MediaFoundationH264Encoder.IsSupported) return;

        using var encoder = new MediaFoundationH264Encoder();

        var sample = EncodeUntilOutput(
            encoder,
            1280,
            720,
            new VideoQuality(360, 30, 800_000));

        Assert.Equal(640, sample.Width);
        Assert.Equal(360, sample.Height);
    }

    [Fact]
    public void A_requested_keyframe_is_produced_without_restarting_the_session()
    {
        if (!MediaFoundationH264Encoder.IsSupported) return;

        using var encoder = new MediaFoundationH264Encoder();
        EncodeUntilOutput(encoder, 640, 360);
        EncodeUntilOutput(encoder, 640, 360);

        encoder.RequestKeyFrame();

        var sample = EncodeUntil(
            encoder,
            640,
            360,
            candidate => candidate.IsKeyFrame,
            attempts: 8);

        Assert.True(sample.IsKeyFrame);
    }

    [Fact]
    public void A_resolution_change_reconfigures_and_emits_a_keyframe()
    {
        if (!MediaFoundationH264Encoder.IsSupported) return;

        using var encoder = new MediaFoundationH264Encoder();
        EncodeUntilOutput(encoder, 640, 360);

        var sample = EncodeUntilOutput(encoder, 320, 180);

        Assert.Equal(320, sample.Width);
        Assert.Equal(180, sample.Height);
        Assert.True(sample.IsKeyFrame);
    }

    [Fact]
    public void Candidate_rejections_are_available_for_diagnostics()
    {
        if (!MediaFoundationH264Encoder.IsSupported) return;

        using var encoder = new MediaFoundationH264Encoder();

        Assert.NotNull(encoder.RejectionLog);
    }

    private static EncodedVideoSample EncodeUntilOutput(
        MediaFoundationH264Encoder encoder,
        int width,
        int height,
        VideoQuality? quality = null) =>
        EncodeUntil(encoder, width, height, _ => true, 10, quality);

    private static EncodedVideoSample EncodeUntil(
        MediaFoundationH264Encoder encoder,
        int width,
        int height,
        Func<EncodedVideoSample, bool> predicate,
        int attempts,
        VideoQuality? quality = null)
    {
        var pixels = new byte[checked(width * height * 4)];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 0x20;
            pixels[i + 1] = 0x80;
            pixels[i + 2] = 0xE0;
            pixels[i + 3] = 0xFF;
        }

        var target = quality ?? new VideoQuality(height, 30, 1_500_000);
        for (var i = 0; i < attempts; i++)
        {
            var frame = new VideoFrame(
                width,
                height,
                pixels,
                TimeSpan.FromTicks(i * TimeSpan.TicksPerSecond / target.FramesPerSecond));
            var sample = encoder.Encode(frame, target);
            if (sample is { } encoded && predicate(encoded))
                return encoded;
        }

        throw new InvalidOperationException($"Media Foundation encoder produced no matching output after {attempts} frame(s).");
    }
}
