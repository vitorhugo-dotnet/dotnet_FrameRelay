using SonicDesktopRelay.Media;
using SonicDesktopRelay.Media.Windows;

namespace SonicDesktopRelay.Media.Windows.Tests;

public sealed class MediaFoundationH264EncoderTests
{
    [Fact]
    public void RuntimeFallback_orders_hardware_before_software_and_preserves_order()
    {
        var software = new MediaFoundationTransformCandidate(Guid.NewGuid(), false);
        var firstHardware = new MediaFoundationTransformCandidate(Guid.NewGuid(), true);
        var secondHardware = new MediaFoundationTransformCandidate(Guid.NewGuid(), true);

        Assert.Equal(
            new[] { firstHardware, secondHardware, software },
            new MediaFoundationTransformRetryPolicy().OrderCandidates(
                new[] { software, firstHardware, secondHardware }));
    }

    [Fact]
    public void RuntimeFallback_excludes_only_the_failed_transform()
    {
        var failed = new MediaFoundationTransformCandidate(Guid.NewGuid(), true);
        var next = new MediaFoundationTransformCandidate(Guid.NewGuid(), true);
        var software = new MediaFoundationTransformCandidate(Guid.NewGuid(), false);
        var policy = new MediaFoundationTransformRetryPolicy();
        policy.ExcludeFailed(failed.Clsid);

        Assert.Equal(
            new[] { next, software },
            policy.OrderCandidates(new[] { failed, software, next }));
    }

    [Fact]
    public void RuntimeFallback_retries_hard_failure_once_and_does_not_retry_success()
    {
        var currentCalls = 0;
        var fallbackCalls = 0;
        var result = MediaFoundationTransformRetryPolicy.ExecuteWithSingleFallback(
            () => { currentCalls++; throw new InvalidOperationException("transform failed"); },
            static exception => exception is InvalidOperationException,
            () => { fallbackCalls++; return 42; });

        Assert.Equal(42, result);
        Assert.Equal(1, currentCalls);
        Assert.Equal(1, fallbackCalls);

        Assert.Equal(7, MediaFoundationTransformRetryPolicy.ExecuteWithSingleFallback(
            () => 7,
            static _ => true,
            () => throw new Xunit.Sdk.XunitException("Fallback must not run after success.")));
    }

    [Fact]
    public void RuntimeFallback_does_not_retry_non_hard_decoder_outcomes()
    {
        var fallbackCalls = 0;

        var result = MediaFoundationTransformRetryPolicy.ExecuteWithSingleFallback(
            () => (int?)null,
            static exception => exception is InvalidOperationException,
            () => { fallbackCalls++; return 1; });

        Assert.Null(result);
        Assert.Equal(0, fallbackCalls);
    }

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
    public void The_first_keyframe_contains_sps_and_pps_for_a_fresh_decoder()
    {
        if (!MediaFoundationH264Encoder.IsSupported) return;

        using var encoder = new MediaFoundationH264Encoder();

        var sample = EncodeUntilOutput(encoder, 640, 360);

        Assert.True(H264AccessUnit.ContainsSps(sample.Data.Span));
        Assert.True(H264AccessUnit.ContainsPps(sample.Data.Span));
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
