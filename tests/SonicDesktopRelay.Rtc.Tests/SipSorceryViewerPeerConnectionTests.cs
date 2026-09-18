using SonicDesktopRelay.Rtc;
using Xunit;

namespace SonicDesktopRelay.Rtc.Tests;

public sealed class SipSorceryViewerPeerConnectionTests
{
    private static readonly IceServerSettings Ice = new(
        [new IceServer("stun:stun.example.com:3478", null, null)], ForceRelay: false);

    /// <summary>
    /// A real sendonly H.264 offer, generated once by <c>SipSorceryPeerConnectionFactory</c> —
    /// the very thing this peer has to answer in production. Hand-writing SDP here would test
    /// the test rather than the code. The gathered host candidates were stripped: they are the
    /// generating machine's addresses, and this project does not commit ICE candidates.
    /// </summary>
    private const string PublisherOfferSdp = """
        v=0
        o=- 30217 0 IN IP4 127.0.0.1
        s=sipsorcery
        t=0 0
        a=group:BUNDLE 0
        m=video 9 UDP/TLS/RTP/SAVP 96
        c=IN IP4 0.0.0.0
        a=ice-ufrag:VLHA
        a=ice-pwd:AWSHZXETDOQXLERXBIMZYEWO
        a=fingerprint:sha-256 11:79:C1:6E:F2:F7:FD:7A:AB:31:07:4A:E2:6B:CC:F7:48:F9:A6:00:50:CC:C8:CC:F7:57:8D:4D:99:CB:1C:ED
        a=setup:actpass
        a=ice-options:ice2,trickle
        a=mid:0
        a=rtpmap:96 H264/90000
        a=rtcp-fb:96 transport-cc
        a=fmtp:96 packetization-mode=1
        a=rtcp-mux
        a=rtcp:9 IN IP4 0.0.0.0
        a=sendonly
        a=ssrc:1811937970 cname:2d5cf20a-34c0-4955-93a0-2bb6fbb3241f
        """;

    [Fact]
    public async Task The_answer_accepts_a_recvonly_h264_video_track()
    {
        var factory = new SipSorceryViewerPeerConnectionFactory(Ice);
        await using var peer = factory.Create();

        var answer = await peer.CreateAnswerAsync(PublisherOfferSdp, CancellationToken.None);

        Assert.Contains("m=video", answer);
        Assert.Contains("H264", answer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("a=recvonly", answer);
    }

    [Fact]
    public async Task The_answer_accepts_opus_as_audio_first_and_recvonly()
    {
        var publisherFactory = new SipSorceryPeerConnectionFactory(Ice);
        await using var publisher = publisherFactory.Create(Guid.NewGuid());
        var offer = await publisher.CreateOfferAsync(CancellationToken.None);

        var viewerFactory = new SipSorceryViewerPeerConnectionFactory(Ice);
        await using var viewer = viewerFactory.Create();
        var answer = await viewer.CreateAnswerAsync(offer, CancellationToken.None);

        var audioStart = answer.IndexOf("m=audio", StringComparison.OrdinalIgnoreCase);
        var videoStart = answer.IndexOf("m=video", StringComparison.OrdinalIgnoreCase);

        Assert.True(audioStart >= 0, "The answer must contain an audio m-line.");
        Assert.True(videoStart >= 0, "The answer must contain a video m-line.");
        Assert.True(audioStart < videoStart, "Audio must remain the BUNDLE-tagged first m-line.");

        var audioSection = answer[audioStart..videoStart];
        Assert.DoesNotContain("m=audio 0", audioSection, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("OPUS", audioSection, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("a=recvonly", audioSection, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_malformed_offer_is_rejected_with_a_clear_failure()
    {
        var factory = new SipSorceryViewerPeerConnectionFactory(Ice);
        await using var peer = factory.Create();

        await Assert.ThrowsAnyAsync<Exception>(
            () => peer.CreateAnswerAsync("this is not sdp", CancellationToken.None));
    }

    [Fact]
    public async Task Requesting_a_keyframe_before_connection_does_not_throw()
    {
        var factory = new SipSorceryViewerPeerConnectionFactory(Ice);
        await using var peer = factory.Create();

        Assert.Null(Record.Exception(peer.RequestKeyFrame));
    }

    [Fact]
    public async Task A_renegotiation_offer_is_answered_on_the_same_peer()
    {
        var factory = new SipSorceryViewerPeerConnectionFactory(Ice);
        await using var peer = factory.Create();
        await peer.CreateAnswerAsync(PublisherOfferSdp, CancellationToken.None);

        var second = await peer.CreateAnswerAsync(PublisherOfferSdp, CancellationToken.None);

        Assert.Contains("a=recvonly", second);
    }
}
