using SonicDesktopRelay.Media;
using SonicDesktopRelay.Rtc;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using Xunit;

namespace SonicDesktopRelay.Rtc.Tests;

public sealed class SipSorceryPeerConnectionTests
{
    private static readonly IceServerSettings Ice = new(
        [new IceServer("stun:stun.example.com:3478", null, null)], ForceRelay: false);

    [Fact]
    public async Task An_offer_advertises_audio_first_sendonly_opus_and_h264()
    {
        var factory = new SipSorceryPeerConnectionFactory(Ice);
        await using var peer = factory.Create(Guid.NewGuid());

        var sdp = await peer.CreateOfferAsync(CancellationToken.None);

        Assert.Contains("m=audio", sdp);
        Assert.Contains("opus/48000", sdp, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("m=video", sdp);
        Assert.Contains("H264", sdp, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AV1/90000", sdp, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("packetization-mode=1", sdp, StringComparison.OrdinalIgnoreCase);
        Assert.True(
            sdp.IndexOf("m=audio", StringComparison.Ordinal) <
            sdp.IndexOf("m=video", StringComparison.Ordinal));
        Assert.Equal(2, sdp.Split("a=sendonly", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public async Task Sipsorcery_negotiates_av1_and_keeps_h264_available_as_fallback()
    {
        var (offer, answer) = await NegotiateVideoFormatsAsync(
            [
                new VideoFormat(VideoCodecsEnum.AV1, 97, 90_000),
                new VideoFormat(VideoCodecsEnum.H264, 96, 90_000, "packetization-mode=1")
            ],
            [
                new VideoFormat(VideoCodecsEnum.AV1, 97, 90_000),
                new VideoFormat(VideoCodecsEnum.H264, 96, 90_000, "packetization-mode=1")
            ]);

        Assert.Contains("AV1/90000", offer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("H264/90000", offer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("AV1/90000", answer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("H264/90000", answer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Sipsorcery_h264_only_answer_keeps_video_when_offer_also_has_av1()
    {
        var (offer, answer) = await NegotiateVideoFormatsAsync(
            [
                new VideoFormat(VideoCodecsEnum.AV1, 97, 90_000),
                new VideoFormat(VideoCodecsEnum.H264, 96, 90_000, "packetization-mode=1")
            ],
            [new VideoFormat(VideoCodecsEnum.H264, 96, 90_000, "packetization-mode=1")]);

        Assert.Contains("AV1/90000", offer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("H264/90000", answer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AV1/90000", answer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("m=video 0", answer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Production_peers_negotiate_av1_when_both_local_capabilities_allow_it()
    {
        var publisherFactory = new SipSorceryPeerConnectionFactory(Ice, CreateCapabilities(encoderAv1: true));
        await using var publisher = publisherFactory.Create(Guid.NewGuid());
        var offer = await publisher.CreateOfferAsync(CancellationToken.None);

        var viewerFactory = new SipSorceryViewerPeerConnectionFactory(Ice, CreateCapabilities(decoderAv1: true));
        await using var viewer = viewerFactory.Create();
        var answer = await viewer.CreateAnswerAsync(offer, CancellationToken.None);
        await publisher.ApplyAnswerAsync(answer, CancellationToken.None);

        Assert.Contains("AV1/90000", offer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("H264/90000", offer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("AV1/90000", answer, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(VideoCodec.Av1, publisher.NegotiatedVideoCodec);
        Assert.Equal(VideoCodec.Av1, viewer.NegotiatedVideoCodec);
        Assert.Contains("profile=0;level-idx=13;tier=0", offer, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new VideoCodecConstraints("0", 13), publisher.NegotiatedVideoConstraints);
    }

    [Fact]
    public async Task Production_publisher_falls_back_to_h264_for_a_viewer_without_av1_decoder_capability()
    {
        var publisherFactory = new SipSorceryPeerConnectionFactory(Ice, CreateCapabilities(encoderAv1: true));
        await using var publisher = publisherFactory.Create(Guid.NewGuid());
        var offer = await publisher.CreateOfferAsync(CancellationToken.None);

        var viewerFactory = new SipSorceryViewerPeerConnectionFactory(Ice);
        await using var viewer = viewerFactory.Create();
        var answer = await viewer.CreateAnswerAsync(offer, CancellationToken.None);
        await publisher.ApplyAnswerAsync(answer, CancellationToken.None);

        Assert.Contains("AV1/90000", offer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("H264/90000", answer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AV1/90000", answer, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(VideoCodec.H264, publisher.NegotiatedVideoCodec);
        Assert.Equal(VideoCodec.H264, viewer.NegotiatedVideoCodec);
    }

    [Fact]
    public async Task Existing_peer_can_renegotiate_from_av1_to_h264_without_replacement()
    {
        var publisherFactory = new SipSorceryPeerConnectionFactory(Ice, CreateCapabilities(encoderAv1: true));
        await using var publisher = (SipSorceryPeerConnection)publisherFactory.Create(Guid.NewGuid());
        await using var viewer = new SipSorceryViewerPeerConnectionFactory(Ice, CreateCapabilities(decoderAv1: true)).Create();
        var initialOffer = await publisher.CreateOfferAsync(CancellationToken.None);
        Assert.False(IsAudioMediaNegotiated(publisher));
        var initialAnswer = await viewer.CreateAnswerAsync(initialOffer, CancellationToken.None);
        await publisher.ApplyAnswerAsync(initialAnswer, CancellationToken.None);
        Assert.Equal(VideoCodec.Av1, publisher.NegotiatedVideoCodec);
        Assert.True(IsAudioMediaNegotiated(publisher));

        var h264Offer = await publisher.CreateH264OfferAsync(CancellationToken.None);
        Assert.True(IsAudioMediaNegotiated(publisher));
        var h264Answer = await viewer.CreateAnswerAsync(h264Offer, CancellationToken.None);
        await publisher.ApplyAnswerAsync(h264Answer, CancellationToken.None);

        Assert.Contains("H264/90000", h264Offer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AV1/90000", h264Offer, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(VideoCodec.H264, publisher.NegotiatedVideoCodec);
        Assert.Equal(VideoCodec.H264, viewer.NegotiatedVideoCodec);
    }

    private static bool IsAudioMediaNegotiated(SipSorceryPeerConnection peer) =>
        (bool)typeof(SipSorceryPeerConnection)
            .GetField("_negotiated", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(peer)!;

    private static VideoCodecCapabilities CreateCapabilities(bool encoderAv1 = false, bool decoderAv1 = false)
    {
        var encoders = encoderAv1 ? new HashSet<VideoCodec> { VideoCodec.H264, VideoCodec.Av1 } : new HashSet<VideoCodec> { VideoCodec.H264 };
        var decoders = decoderAv1 ? new HashSet<VideoCodec> { VideoCodec.H264, VideoCodec.Av1 } : new HashSet<VideoCodec> { VideoCodec.H264 };
        var constraints = new Dictionary<VideoCodec, VideoCodecConstraints>
        {
            [VideoCodec.Av1] = new("0", 13)
        };
        return new VideoCodecCapabilities(encoders, decoders, constraints, constraints, new Dictionary<VideoCodec, string>());
    }

    private static async Task<(string Offer, string Answer)> NegotiateVideoFormatsAsync(
        IReadOnlyList<VideoFormat> publisherFormats,
        IReadOnlyList<VideoFormat> viewerFormats)
    {
        using var publisher = new RTCPeerConnection();
        using var viewer = new RTCPeerConnection();
        publisher.addTrack(new MediaStreamTrack(publisherFormats.ToList(), MediaStreamStatusEnum.SendOnly));
        viewer.addTrack(new MediaStreamTrack(viewerFormats.ToList(), MediaStreamStatusEnum.RecvOnly));

        var offer = publisher.createOffer();
        await publisher.setLocalDescription(offer);
        Assert.Equal(SetDescriptionResultEnum.OK, viewer.setRemoteDescription(
            new RTCSessionDescriptionInit { type = RTCSdpType.offer, sdp = offer.sdp }));

        var answer = viewer.createAnswer();
        await viewer.setLocalDescription(answer);

        return (offer.sdp, answer.sdp);
    }

    [Fact]
    public async Task The_peer_reports_the_participant_it_was_created_for()
    {
        var participantId = Guid.NewGuid();
        var factory = new SipSorceryPeerConnectionFactory(Ice);

        await using var peer = factory.Create(participantId);

        Assert.Equal(participantId, peer.ParticipantId);
    }

    [Theory]
    [InlineData(SDPMediaTypesEnum.audio, 11, RtcMediaKind.Audio)]
    [InlineData(SDPMediaTypesEnum.video, 22, RtcMediaKind.Video)]
    [InlineData(SDPMediaTypesEnum.video, 11, RtcMediaKind.Unknown)]
    [InlineData(SDPMediaTypesEnum.audio, 33, RtcMediaKind.Unknown)]
    public void Reception_reports_require_matching_track_media_and_ssrc(
        SDPMediaTypesEnum mediaType, uint reportSsrc, RtcMediaKind expected)
    {
        var kind = SipSorceryPeerConnection.ClassifyReceptionReportMediaKind(mediaType, reportSsrc, 11, 22);

        Assert.Equal(expected, kind);
    }

    [Fact]
    public async Task Forcing_relay_produces_a_relay_only_offer()
    {
        var factory = new SipSorceryPeerConnectionFactory(Ice with { ForceRelay = true });
        await using var peer = factory.Create(Guid.NewGuid());

        var sdp = await peer.CreateOfferAsync(CancellationToken.None);

        // With relay forced and no TURN server configured, no host candidates may leak.
        Assert.DoesNotContain("typ host", sdp);
    }

    [Fact]
    public async Task Sending_video_before_the_answer_arrives_does_not_throw()
    {
        var factory = new SipSorceryPeerConnectionFactory(Ice);
        await using var peer = factory.Create(Guid.NewGuid());
        await peer.CreateOfferAsync(CancellationToken.None);

        var exception = Record.Exception(() => peer.SendVideo(
            new EncodedVideoSample(new byte[8], TimeSpan.Zero, true, 1920, 1080)));

        Assert.Null(exception);
    }

    [Fact]
    public async Task Sending_audio_before_the_answer_arrives_does_not_throw()
    {
        var factory = new SipSorceryPeerConnectionFactory(Ice);
        await using var peer = factory.Create(Guid.NewGuid());
        await peer.CreateOfferAsync(CancellationToken.None);

        var exception = Record.Exception(() => peer.SendAudio(
            new EncodedAudioSample(
                new byte[] { 1, 2, 3 },
                SampleCount: 960,
                Duration: TimeSpan.FromMilliseconds(20),
                Timestamp: TimeSpan.Zero)));

        Assert.Null(exception);
    }
}
