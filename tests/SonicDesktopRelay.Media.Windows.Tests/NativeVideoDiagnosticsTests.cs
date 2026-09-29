using SonicDesktopRelay.Media;
using SonicDesktopRelay.Media.Windows;
using SonicDesktopRelay.App;
using SonicDesktopRelay.ApiClient;
using System.Reflection;

namespace SonicDesktopRelay.Media.Windows.Tests;

public sealed class NativeVideoDiagnosticsTests
{
    [Fact]
    public void Encoder_diagnostics_project_the_live_selected_transform()
    {
        if (!MediaFoundationH264Encoder.IsSupported) return;

        using var encoder = new MediaFoundationH264Encoder();
        var pixels = new byte[640 * 360 * 4];
        encoder.Encode(
            new VideoFrame(640, 360, pixels, TimeSpan.Zero),
            new VideoQuality(360, 30, 1_500_000));

        var diagnostics = encoder.Diagnostics;

        Assert.Equal("Media Foundation", diagnostics.Backend);
        Assert.False(string.IsNullOrWhiteSpace(diagnostics.TransformName));
        Assert.Equal("NV12", diagnostics.InputFormat);
        Assert.Equal("H264", diagnostics.OutputFormat);
        Assert.Equal(640, diagnostics.Width);
        Assert.Equal(360, diagnostics.Height);
        Assert.Equal(30, diagnostics.FramesPerSecond);
        Assert.Equal(1_500_000, diagnostics.Bitrate);
        Assert.Equal(encoder.RejectionLog, diagnostics.RejectionReasons);

        var text = diagnostics.ToString();
        Assert.DoesNotContain("turn:", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http://", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credential", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Decoder_diagnostics_project_the_live_selected_transform()
    {
        if (!MediaFoundationH264Decoder.IsSupported) return;

        using var decoder = new MediaFoundationH264Decoder();

        var diagnostics = decoder.Diagnostics;

        Assert.Equal("Media Foundation", diagnostics.Backend);
        Assert.False(string.IsNullOrWhiteSpace(diagnostics.TransformName));
        Assert.Equal("H264", diagnostics.InputFormat);
        Assert.Equal("NV12", diagnostics.OutputFormat);
        Assert.Equal(decoder.RejectionLog, diagnostics.RejectionReasons);
    }

    [Fact]
    public async Task Publish_host_metrics_report_only_negotiated_codecs_without_transport_secrets()
    {
        if (!MediaFoundationH264Encoder.IsSupported) return;

        using var encoder = new MediaFoundationH264Encoder();
        encoder.Encode(new VideoFrame(640, 360, new byte[640 * 360 * 4], TimeSpan.Zero),
            new VideoQuality(360, 30, 1_500_000));
        await using var host = new RtcVideoPublishHost(new IceApiClient(new HttpClient()), () => null);
        typeof(RtcVideoPublishHost).GetField("_encoder", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(host, encoder);
        typeof(RtcVideoPublishHost).GetProperty(nameof(RtcVideoPublishHost.EncoderName))!
            .SetValue(host, encoder.Name);

        var metrics = Assert.IsType<SonicDesktopRelay.Presentation.SessionMediaMetrics>(host.CurrentMetrics);

        Assert.Null(metrics.Codec);
        Assert.Null(metrics.NegotiatedCodec);
        Assert.Equal("H264", metrics.LocalSupportedCodecs);
        Assert.Equal(encoder.Diagnostics.TransformName, metrics.VideoImplementation);
        Assert.Equal(encoder.Diagnostics.Acceleration, metrics.VideoAcceleration);
        Assert.Null(metrics.EncodeDurationMilliseconds);
        Assert.DoesNotContain("sdp", metrics.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("candidate:", metrics.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Watch_host_metrics_leave_codec_unknown_without_negotiation_or_decoded_frames()
    {
        if (!MediaFoundationH264Decoder.IsSupported) return;

        var h264 = new MediaFoundationH264Decoder();
        var capabilities = new VideoCodecCapabilities(
            new HashSet<VideoCodec>(), new HashSet<VideoCodec>(),
            new Dictionary<VideoCodec, VideoCodecConstraints>(),
            new Dictionary<VideoCodec, VideoCodecConstraints>(),
            new Dictionary<VideoCodec, string>());
        var decoderType = typeof(RtcVideoWatchHost).GetNestedType("CodecSwitchingDecoder", BindingFlags.NonPublic)!;
        var decoder = (IVideoDecoder)Activator.CreateInstance(
            decoderType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: [h264, (Func<MediaFoundationAv1Decoder>)(() => new MediaFoundationAv1Decoder()), capabilities],
            culture: null)!;
        var pipeline = new ScreenWatchPipeline(decoder, TimeProvider.System);
        await using var host = new RtcVideoWatchHost(new IceApiClient(new HttpClient()), () => null);
        typeof(RtcVideoWatchHost).GetField("_decoder", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(host, decoder);
        typeof(RtcVideoWatchHost).GetField("_pipeline", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(host, pipeline);

        var metrics = Assert.IsType<SonicDesktopRelay.Presentation.SessionMediaMetrics>(host.CurrentMetrics);

        Assert.Equal("H264", metrics.LocalSupportedCodecs);
        Assert.Null(metrics.Codec);
        Assert.Null(metrics.NegotiatedCodec);
        Assert.Null(metrics.CommonSupportedCodecs);
    }
}
