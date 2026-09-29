using SonicDesktopRelay.Media;
using SonicDesktopRelay.Media.Windows;

namespace SonicDesktopRelay.Media.Windows.Tests;

public sealed class MediaFoundationAv1CapabilityProbeTests
{
    private static readonly Guid Av1Subtype = new("31305641-0000-0010-8000-00AA00389B71");

    [Fact]
    public void Absent_transforms_do_not_advertise_AV1()
    {
        var capabilities = new MediaFoundationAv1CapabilityProbe().Detect(new Catalog());

        Assert.DoesNotContain(VideoCodec.Av1, capabilities.Encoders);
        Assert.DoesNotContain(VideoCodec.Av1, capabilities.Decoders);
    }

    [Fact]
    public void Software_only_transforms_are_rejected()
    {
        var catalog = new Catalog(
            new Candidate(Av1TransformDirection.Encoder, isHardware: false),
            new Candidate(Av1TransformDirection.Decoder, isHardware: false));

        var capabilities = new MediaFoundationAv1CapabilityProbe().Detect(catalog);

        Assert.DoesNotContain(VideoCodec.Av1, capabilities.Encoders);
        Assert.DoesNotContain(VideoCodec.Av1, capabilities.Decoders);
        Assert.Contains("software-transform-rejected", capabilities.RejectionReasons[VideoCodec.Av1]);
    }

    [Fact]
    public void Hardware_transforms_are_advertised_only_after_configuration_succeeds()
    {
        var catalog = new Catalog(
            new Candidate(Av1TransformDirection.Encoder, isHardware: true),
            new Candidate(Av1TransformDirection.Decoder, isHardware: true));

        var capabilities = new MediaFoundationAv1CapabilityProbe().Detect(catalog);

        Assert.Contains(VideoCodec.Av1, capabilities.Encoders);
        Assert.Contains(VideoCodec.Av1, capabilities.Decoders);
        Assert.Equal("0", capabilities.EncoderConstraints[VideoCodec.Av1].Profile);
        Assert.Equal("0", capabilities.DecoderConstraints[VideoCodec.Av1].Profile);
        Assert.Equal(1, capabilities.EncoderConstraints[VideoCodec.Av1].MaxLevel);
        Assert.Equal(1, capabilities.DecoderConstraints[VideoCodec.Av1].MaxLevel);
    }

    [Fact]
    public void Activation_or_configuration_failure_is_not_advertised()
    {
        var catalog = new Catalog(
            new Candidate(Av1TransformDirection.Encoder, isHardware: true, fail: true),
            new Candidate(Av1TransformDirection.Decoder, isHardware: true, fail: true));

        var capabilities = new MediaFoundationAv1CapabilityProbe().Detect(catalog);

        Assert.DoesNotContain(VideoCodec.Av1, capabilities.Encoders);
        Assert.DoesNotContain(VideoCodec.Av1, capabilities.Decoders);
        Assert.Contains("activation/configuration failed", capabilities.RejectionReasons[VideoCodec.Av1]);
    }

    [Fact]
    public void Decoder_configuration_failure_advances_to_the_next_candidate()
    {
        var rejected = new Candidate(Av1TransformDirection.Decoder, isHardware: true, fail: true);
        var accepted = new Candidate(Av1TransformDirection.Decoder, isHardware: true);
        var catalog = new Catalog(
            new Candidate(Av1TransformDirection.Encoder, isHardware: true),
            rejected,
            accepted);

        var capabilities = new MediaFoundationAv1CapabilityProbe().Detect(catalog);

        Assert.Contains(VideoCodec.Av1, capabilities.Decoders);
        Assert.Contains("test MFT rejected media type", capabilities.RejectionReasons[VideoCodec.Av1]);
        Assert.True(rejected.Disposed);
        Assert.True(accepted.Disposed);
    }

    [Fact]
    public void Async_decoder_without_an_event_pump_is_rejected_and_candidates_are_disposed()
    {
        var encoder = new Candidate(Av1TransformDirection.Encoder, isHardware: true);
        var decoder = new Candidate(Av1TransformDirection.Decoder, isHardware: true, isAsync: true);
        var catalog = new Catalog(encoder, decoder);

        var capabilities = new MediaFoundationAv1CapabilityProbe().Detect(catalog);

        Assert.Contains(VideoCodec.Av1, capabilities.Encoders);
        Assert.DoesNotContain(VideoCodec.Av1, capabilities.Decoders);
        Assert.Contains("async AV1 decoder rejected", capabilities.RejectionReasons[VideoCodec.Av1]);
        Assert.True(encoder.Disposed);
        Assert.True(decoder.Disposed);
    }

    private sealed class Catalog(params Candidate[] candidates) : IAv1TransformCatalog
    {
        public IEnumerable<IAv1TransformCandidate> Enumerate(Av1TransformDirection direction) =>
            candidates.Where(candidate => candidate.Direction == direction);
    }

    private sealed class Candidate(
        Av1TransformDirection direction,
        bool isHardware,
        bool fail = false,
        bool isAsync = false) : IAv1TransformCandidate
    {
        public Av1TransformDirection Direction { get; } = direction;
        public string Name => "fake AV1 transform";
        public bool IsHardware { get; } = isHardware;
        public bool Disposed { get; private set; }
        public void ActivateAndConfigure(Guid av1Subtype, int width, int height, int fps)
        {
            Assert.Equal(Av1Subtype, av1Subtype);
            Assert.Equal((640, 360, 30), (width, height, fps));
            if (Direction == Av1TransformDirection.Decoder && isAsync)
                throw new NotSupportedException("async AV1 decoder rejected without an event pump");
            if (fail) throw new InvalidOperationException("test MFT rejected media type");
        }
        public void Dispose() => Disposed = true;
    }
}
