using System.Runtime.InteropServices;
using SonicDesktopRelay.Media;
using SonicDesktopRelay.Media.Windows;
using Vortice.MediaFoundation;

namespace SonicDesktopRelay.Media.Windows.Tests;

public sealed class MediaFoundationH264DecoderTests
{
    [Theory]
    [InlineData(unchecked((int)0xC00D6D72), false)]
    [InlineData(unchecked((int)0xC00D6D61), false)]
    [InlineData(unchecked((int)0x80004005), true)]
    public void RuntimeFallback_classifies_decoder_process_output_hresult(
        int hresult,
        bool shouldFallback)
    {
        Assert.Equal(
            shouldFallback,
            MediaFoundationTransformRetryPolicy.IsHardDecoderOutputFailure(hresult));
    }

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
        var selectedTransform = decoder.TransformInfo;
        var rejectionCount = decoder.RejectionLog.Count;

        var frame = decoder.Decode(
            new EncodedVideoSample(new byte[] { 1, 2, 3, 4 }, TimeSpan.Zero, false, 16, 16));

        Assert.Null(frame);
        Assert.Equal(selectedTransform, decoder.TransformInfo);
        Assert.Equal(rejectionCount, decoder.RejectionLog.Count);
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
    public void Caller_allocated_output_sample_is_disposed_exactly_once()
    {
        var native = new FakeNativeSample();
        using var caller = new FakeSampleWrapper(native);
        using var returnedAlias = new FakeSampleWrapper(native);
        var events = new FakeDisposable();

        MediaFoundationOutputSampleLifetime.DisposeOwnedResources(
            OutputSampleAllocationMode.CallerAllocated,
            caller,
            returnedAlias,
            events);

        Assert.Equal(1, native.ReleaseCount);
        Assert.True(caller.IsDisposed);
        Assert.False(returnedAlias.IsDisposed);
        Assert.Equal(1, events.DisposeCount);
    }

    [Fact]
    public void Mft_allocated_output_sample_is_disposed_exactly_once()
    {
        var native = new FakeNativeSample();
        using var returned = new FakeSampleWrapper(native);
        var events = new FakeDisposable();

        MediaFoundationOutputSampleLifetime.DisposeOwnedResources(
            OutputSampleAllocationMode.MftAllocated,
            callerAllocatedSample: null,
            processOutputSample: returned,
            events);

        Assert.Equal(1, native.ReleaseCount);
        Assert.True(returned.IsDisposed);
        Assert.Equal(1, events.DisposeCount);
    }

    [Fact]
    public void Different_managed_wrappers_for_one_caller_sample_are_not_double_disposed()
    {
        var native = new FakeNativeSample();
        using var caller = new FakeSampleWrapper(native);
        using var processOutputAlias = new FakeSampleWrapper(native);

        MediaFoundationOutputSampleLifetime.DisposeOwnedResources(
            OutputSampleAllocationMode.CallerAllocated,
            caller,
            processOutputAlias,
            outputEvents: null);

        Assert.Equal(1, native.ReleaseCount);
        Assert.True(caller.IsDisposed);
        Assert.False(processOutputAlias.IsDisposed);
    }

    [Fact]
    public void Provides_samples_requires_the_mft_owned_path()
    {
        const int flags = (int)OutputStreamInfoFlags.OutputStreamProvidesSamples;

        Assert.False(MediaFoundationOutputSampleLifetime.ShouldSupplyCallerSample(flags));
        Assert.Equal(
            OutputSampleAllocationMode.MftAllocated,
            MediaFoundationOutputSampleLifetime.ResolveAllocationMode(
                flags,
                callerSuppliedSample: false));
        Assert.Throws<InvalidOperationException>(
            () => MediaFoundationOutputSampleLifetime.ResolveAllocationMode(
                flags,
                callerSuppliedSample: true));
    }

    [Fact]
    public void Can_provide_samples_follows_the_actual_sample_supplier()
    {
        const int flags = (int)OutputStreamInfoFlags.OutputStreamCanProvideSamples;

        Assert.True(MediaFoundationOutputSampleLifetime.ShouldSupplyCallerSample(flags));
        Assert.Equal(
            OutputSampleAllocationMode.CallerAllocated,
            MediaFoundationOutputSampleLifetime.ResolveAllocationMode(
                flags,
                callerSuppliedSample: true));
        Assert.Equal(
            OutputSampleAllocationMode.MftAllocated,
            MediaFoundationOutputSampleLifetime.ResolveAllocationMode(
                flags,
                callerSuppliedSample: false));
    }

    [Fact]
    public void Caller_required_allocation_rejects_a_missing_caller_sample()
    {
        const int flags = 0;

        Assert.True(MediaFoundationOutputSampleLifetime.ShouldSupplyCallerSample(flags));
        Assert.Equal(
            OutputSampleAllocationMode.CallerAllocated,
            MediaFoundationOutputSampleLifetime.ResolveAllocationMode(
                flags,
                callerSuppliedSample: true));
        Assert.Throws<InvalidOperationException>(
            () => MediaFoundationOutputSampleLifetime.ResolveAllocationMode(
                flags,
                callerSuppliedSample: false));
    }

    [Fact]
    public void Need_more_input_cleanup_releases_the_caller_sample_and_events_once()
    {
        var native = new FakeNativeSample();
        using var caller = new FakeSampleWrapper(native);
        using var processOutputAlias = new FakeSampleWrapper(native);
        var events = new FakeDisposable();

        MediaFoundationOutputSampleLifetime.DisposeOwnedResources(
            OutputSampleAllocationMode.CallerAllocated,
            caller,
            processOutputAlias,
            events);

        Assert.Equal(1, native.ReleaseCount);
        Assert.Equal(1, events.DisposeCount);
    }

    [Fact]
    public void Stream_change_cleanup_does_not_double_release_the_caller_sample()
    {
        var native = new FakeNativeSample();
        using var caller = new FakeSampleWrapper(native);
        using var processOutputAlias = new FakeSampleWrapper(native);

        MediaFoundationOutputSampleLifetime.DisposeOwnedResources(
            OutputSampleAllocationMode.CallerAllocated,
            caller,
            processOutputAlias,
            outputEvents: null);

        Assert.Equal(1, native.ReleaseCount);
        Assert.False(processOutputAlias.IsDisposed);
    }

    [Fact]
    public void Output_events_are_disposed_independently_when_no_sample_is_returned()
    {
        var events = new FakeDisposable();

        MediaFoundationOutputSampleLifetime.DisposeOwnedResources(
            OutputSampleAllocationMode.MftAllocated,
            callerAllocatedSample: null,
            processOutputSample: null,
            events);

        Assert.Equal(1, events.DisposeCount);
    }

    [Fact]
    public void Successful_output_uses_the_owned_sample_until_conversion_finishes()
    {
        var callerNative = new FakeNativeSample();
        var returnedNative = new FakeNativeSample();
        using var caller = new FakeSampleWrapper(callerNative);
        using var returned = new FakeSampleWrapper(returnedNative);

        var callerSelected = MediaFoundationOutputSampleLifetime.SelectSampleForConversion(
            OutputSampleAllocationMode.CallerAllocated,
            caller,
            returned);
        var mftSelected = MediaFoundationOutputSampleLifetime.SelectSampleForConversion(
            OutputSampleAllocationMode.MftAllocated,
            caller,
            returned);

        Assert.Same(caller, callerSelected);
        Assert.Same(returned, mftSelected);
        Assert.False(caller.IsDisposed);
        Assert.False(returned.IsDisposed);
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
    private sealed class FakeNativeSample
    {
        public int ReleaseCount { get; set; }
    }

    private sealed class FakeSampleWrapper : IDisposable
    {
        private readonly FakeNativeSample _native;

        public FakeSampleWrapper(FakeNativeSample native)
        {
            _native = native;
        }

        public bool IsDisposed { get; private set; }

        public void Dispose()
        {
            if (IsDisposed)
                return;

            IsDisposed = true;
            _native.ReleaseCount++;
        }
    }

    private sealed class FakeDisposable : IDisposable
    {
        public int DisposeCount { get; private set; }

        public void Dispose()
        {
            DisposeCount++;
        }
    }
}
