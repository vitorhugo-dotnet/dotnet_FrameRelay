using SIPSorcery.Net;
using Xunit;

namespace SonicDesktopRelay.Rtc.Tests;

public sealed class H264RtpIntegrityInvestigationTests
{
    private const uint Timestamp = 90_000;

    [Fact]
    public void Contiguous_fu_a_fragments_emit_one_access_unit()
    {
        var depacketiser = new H264Depacketiser();

        Assert.Null(Push(depacketiser, 100, FuStart(0x11), marker: 0));
        Assert.Null(Push(depacketiser, 101, FuMiddle(0x22), marker: 0));
        Assert.Null(Push(depacketiser, 102, FuMiddle(0x33), marker: 0));

        var completed = Push(depacketiser, 103, FuEnd(0x44), marker: 1);

        Assert.NotNull(completed);
        Assert.NotEmpty(completed!);
    }

    [Fact]
    public void Missing_middle_fu_a_fragment_must_not_emit_an_access_unit()
    {
        var depacketiser = new H264Depacketiser();

        Assert.Null(Push(depacketiser, 100, FuStart(0x11), marker: 0));
        Assert.Null(Push(depacketiser, 101, FuMiddle(0x22), marker: 0));
        Assert.Null(Push(depacketiser, 103, FuMiddle(0x33), marker: 0));

        var completed = Push(depacketiser, 104, FuEnd(0x44), marker: 1);

        // Desired FrameRelay contract: an AU with a proven RTP sequence gap must be dropped.
        // SIPSorcery 10.0.16 currently sorts and concatenates the surviving FU-A fragments,
        // which is the production corruption path this regression work must guard.
        Assert.Null(completed);
    }

    private static byte[]? Push(H264Depacketiser depacketiser, ushort sequence, byte[] payload, int marker)
    {
        using var result = depacketiser.ProcessRTPPayload(
            payload,
            sequence,
            Timestamp,
            marker,
            out _);
        return result?.ToArray();
    }

    // FU indicator: NRI=3 + type=28 (FU-A). Reconstructed NAL type is 5 (IDR).
    private static byte[] FuStart(byte data) => [0x7C, 0x85, data];
    private static byte[] FuMiddle(byte data) => [0x7C, 0x05, data];
    private static byte[] FuEnd(byte data) => [0x7C, 0x45, data];
}
