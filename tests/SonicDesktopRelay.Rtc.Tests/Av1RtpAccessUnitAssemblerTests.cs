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
        Assert.False(result.Value.IsKeyFrame);
        Assert.False(result.Value.HasVcl);
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
        Assert.False(result.Value.HasVcl);
        Assert.False(result.Value.IsKeyFrame);
        Assert.True(assembler.RtpPacketsReordered > 0);
    }

    [Fact]
    public void Sequence_header_followed_by_frame_header_classifies_key_frame()
    {
        var assembler = new Av1RtpAccessUnitAssembler();
        var temporalUnit = CreateTemporalUnit(
            CreateSequenceHeader(reducedStillPictureHeader: false),
            CreateFrameHeader(showExistingFrame: false, frameType: 0));

        var result = PushSinglePacket(assembler, temporalUnit, sequence: 1, timestamp: 1);

        Assert.NotNull(result);
        Assert.True(result.Value.HasVcl);
        Assert.True(result.Value.IsKeyFrame);
    }

    [Fact]
    public void Key_frame_header_in_a_later_temporal_unit_uses_cached_sequence_header_mode()
    {
        var assembler = new Av1RtpAccessUnitAssembler();
        var first = PushSinglePacket(
            assembler,
            CreateTemporalUnit(CreateSequenceHeader(reducedStillPictureHeader: false)),
            sequence: 1,
            timestamp: 1);
        var second = PushSinglePacket(
            assembler,
            CreateTemporalUnit(CreateFrameHeader(showExistingFrame: false, frameType: 0)),
            sequence: 2,
            timestamp: 2);

        Assert.NotNull(first);
        Assert.False(first.Value.HasVcl);
        Assert.False(first.Value.IsKeyFrame);
        Assert.NotNull(second);
        Assert.True(second.Value.HasVcl);
        Assert.True(second.Value.IsKeyFrame);
    }

    [Fact]
    public void Drops_a_gap_between_single_packet_temporal_units_and_counts_the_lost_packet()
    {
        var assembler = new Av1RtpAccessUnitAssembler();
        var drops = new List<Av1AccessUnitDrop>();
        assembler.AccessUnitDropped += drops.Add;
        var payload = Assert.Single(AV1Packetiser.Packetize(CreateTemporalUnit(24), 1200)).Payload;

        Assert.NotNull(assembler.Push(10, 100, true, payload));
        Assert.Null(assembler.Push(12, 200, true, payload));

        Assert.Contains(drops, drop => drop.Reason == "rtp-sequence-gap" && drop.Timestamp == 200);
        Assert.Equal(1, assembler.RtpSequenceGaps);
        Assert.Equal(1, assembler.RtpPacketsLost);
        Assert.Equal(1, assembler.AccessUnitsReceived);
    }

    [Fact]
    public void Drops_a_temporal_unit_when_its_first_packet_was_lost_even_if_next_obu_starts_cleanly()
    {
        var assembler = new Av1RtpAccessUnitAssembler();
        var drops = new List<Av1AccessUnitDrop>();
        assembler.AccessUnitDropped += drops.Add;
        var first = Assert.Single(AV1Packetiser.Packetize(CreateTemporalUnit(24), 1200));
        var firstObuPacket = Assert.Single(AV1Packetiser.Packetize(
            CreateTemporalUnit(CreateSequenceHeader(reducedStillPictureHeader: true)), 1200));
        var nextObuPacket = Assert.Single(AV1Packetiser.Packetize(
            CreateTemporalUnit(CreateFrameHeader(showExistingFrame: false, frameType: 0)), 1200));
        Assert.Equal(0, firstObuPacket.Payload[0] & 0x80);
        Assert.Equal(0, nextObuPacket.Payload[0] & 0x80);

        Assert.NotNull(assembler.Push(100, 100, true, first.Payload));
        // Packet 101 held the first OBU with marker clear but was lost. The next OBU starts
        // cleanly (Z=0), so only RTP sequence continuity detects the missing temporal-unit data.
        var droppedResult = assembler.Push(102, 200, true, nextObuPacket.Payload);

        Assert.Null(droppedResult);
        Assert.Contains(drops, drop => drop.Reason == "rtp-sequence-gap" && drop.Timestamp == 200);
        Assert.Equal(1, assembler.RtpSequenceGaps);
        Assert.Equal(1, assembler.RtpPacketsLost);
    }

    [Fact]
    public void Sequence_continuity_accepts_wrap_from_65535_to_zero_between_temporal_units()
    {
        var assembler = new Av1RtpAccessUnitAssembler();
        var payload = Assert.Single(AV1Packetiser.Packetize(CreateTemporalUnit(24), 1200)).Payload;

        Assert.NotNull(assembler.Push(ushort.MaxValue, 100, true, payload));
        var next = assembler.Push(0, 200, true, payload);

        Assert.NotNull(next);
        Assert.Equal(0, assembler.RtpSequenceGaps);
        Assert.Equal(0, assembler.RtpPacketsLost);
    }

    [Fact]
    public void Sequence_header_only_temporal_unit_is_not_a_frame_or_key_frame()
    {
        var assembler = new Av1RtpAccessUnitAssembler();
        var result = PushSinglePacket(
            assembler,
            CreateTemporalUnit(CreateSequenceHeader(reducedStillPictureHeader: true)),
            sequence: 1,
            timestamp: 1);

        Assert.NotNull(result);
        Assert.False(result.Value.HasVcl);
        Assert.False(result.Value.IsKeyFrame);
    }

    [Fact]
    public void Frame_header_without_sequence_state_is_not_assumed_to_be_a_key_frame()
    {
        var assembler = new Av1RtpAccessUnitAssembler();
        var result = PushSinglePacket(
            assembler,
            CreateTemporalUnit(CreateFrameHeader(showExistingFrame: false, frameType: 0)),
            sequence: 1,
            timestamp: 1);

        Assert.NotNull(result);
        Assert.True(result.Value.HasVcl);
        Assert.False(result.Value.IsKeyFrame);
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

    private static Av1AssembledAccessUnit? PushSinglePacket(
        Av1RtpAccessUnitAssembler assembler,
        byte[] temporalUnit,
        ushort sequence,
        uint timestamp)
    {
        var packets = AV1Packetiser.Packetize(temporalUnit, 1200);
        var result = (Av1AssembledAccessUnit?)null;
        foreach (var packet in packets)
            result = assembler.Push(sequence++, timestamp, packet.IsLast, packet.Payload) ?? result;
        return result;
    }

    private static byte[] CreateSequenceHeader(bool reducedStillPictureHeader)
    {
        var syntax = reducedStillPictureHeader ? (byte)0x08 : (byte)0x00;
        return CreateObu(1, [syntax]);
    }

    private static byte[] CreateFrameHeader(bool showExistingFrame, int frameType)
    {
        var syntax = (byte)((showExistingFrame ? 1 : 0) << 7 | (frameType & 0x03) << 5);
        return CreateObu(3, [syntax]);
    }

    private static byte[] CreateObu(int type, byte[] payload) =>
        [(byte)((type << 3) | 0x02), (byte)payload.Length, .. payload];

    private static byte[] CreateTemporalUnit(params byte[][] obus) => obus.SelectMany(obu => obu).ToArray();
}
