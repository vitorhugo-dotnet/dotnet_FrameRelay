using SIPSorcery.Net;
using Xunit;

namespace SonicDesktopRelay.Rtc.Tests;

public sealed class Av1RtpAccessUnitAssemblerTests
{
    [Fact]
    public void Assembles_a_single_packet_temporal_unit()
    {
        var assembler = new Av1RtpAccessUnitAssembler();
        var expected = CreateTemporalUnit(24);
        var packet = Assert.Single(AV1Packetiser.Packetize(expected, 1200));

        var result = assembler.Push(10, 100, packet.IsLast, packet.Payload);

        Assert.NotNull(result);
        Assert.Equal(expected, result.Value.Data);
        Assert.True(result.Value.IsKeyFrame);
    }

    [Fact]
    public void Reassembles_fragmented_temporal_unit_and_accepts_reordering()
    {
        var assembler = new Av1RtpAccessUnitAssembler();
        var expected = CreateTemporalUnit(80);
        var packets = AV1Packetiser.Packetize(expected, 18);
        Assert.True(packets.Count > 2);

        Av1AssembledAccessUnit? result = assembler.Push(
            (ushort)(100 + packets.Count - 1), 200, packets[^1].IsLast, packets[^1].Payload);
        for (var i = packets.Count - 2; i >= 0; i--)
            result = assembler.Push((ushort)(100 + i), 200, packets[i].IsLast, packets[i].Payload) ?? result;

        Assert.NotNull(result);
        Assert.Equal(expected, result.Value.Data);
        Assert.True(assembler.RtpPacketsReordered > 0);
    }

    [Fact]
    public void Drops_a_sequence_gap_without_reconstructing_partial_data()
    {
        var assembler = new Av1RtpAccessUnitAssembler();
        var packets = AV1Packetiser.Packetize(CreateTemporalUnit(80), 18);
        Assert.True(packets.Count > 2);
        var drops = new List<Av1AccessUnitDrop>();
        assembler.AccessUnitDropped += drops.Add;

        for (var i = 0; i < packets.Count; i++)
        {
            if (i == 1) continue;
            var result = assembler.Push((ushort)(i + 1), 300, packets[i].IsLast, packets[i].Payload);
            Assert.Null(result);
        }
        assembler.Push(500, 301, false, AV1Packetiser.Packetize(CreateTemporalUnit(24), 1200)[0].Payload);

        Assert.Contains(drops, drop => drop.Reason == "rtp-sequence-gap");
    }

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 0x04 })]
    [InlineData(new byte[] { 0x00, 0x80 })]
    [InlineData(new byte[] { 0x10, 0x80 })]
    public void Drops_empty_or_malformed_av1_payloads(byte[] payload)
    {
        var assembler = new Av1RtpAccessUnitAssembler();
        var drops = new List<Av1AccessUnitDrop>();
        assembler.AccessUnitDropped += drops.Add;

        Assert.Null(assembler.Push(1, 1, true, payload));

        Assert.NotEmpty(drops);
    }

    [Fact]
    public void Rejects_access_units_that_exceed_the_retained_byte_limit()
    {
        var assembler = new Av1RtpAccessUnitAssembler(maxRetainedBytes: 16);
        var drops = new List<Av1AccessUnitDrop>();
        assembler.AccessUnitDropped += drops.Add;
        var packets = AV1Packetiser.Packetize(CreateTemporalUnit(80), 18);

        foreach (var (packet, index) in packets.Select((value, index) => (value, index)))
            Assert.Null(assembler.Push((ushort)(index + 1), 400, packet.IsLast, packet.Payload));

        Assert.Contains(drops, drop => drop.Reason == "access-unit-size-limit");
        Assert.Equal(0, assembler.RetainedBytes);
    }

    private static byte[] CreateTemporalUnit(int payloadSize)
    {
        var obu = new byte[payloadSize + 2];
        obu[0] = 0x0A; // Sequence header OBU, with an explicit size field.
        obu[1] = (byte)payloadSize;
        for (var i = 2; i < obu.Length; i++) obu[i] = (byte)(i & 0x7f);
        return obu;
    }
}
