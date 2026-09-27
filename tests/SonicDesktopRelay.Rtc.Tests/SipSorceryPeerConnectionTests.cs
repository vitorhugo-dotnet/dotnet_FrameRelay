using SonicDesktopRelay.Media;
using SonicDesktopRelay.Rtc;
using SIPSorcery.Net;
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
        Assert.Contains("packetization-mode=1", sdp, StringComparison.OrdinalIgnoreCase);
        Assert.True(
            sdp.IndexOf("m=audio", StringComparison.Ordinal) <
            sdp.IndexOf("m=video", StringComparison.Ordinal));
        Assert.Equal(2, sdp.Split("a=sendonly", StringSplitOptions.None).Length - 1);
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
