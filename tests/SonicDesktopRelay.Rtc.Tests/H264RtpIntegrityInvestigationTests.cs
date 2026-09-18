using Xunit;

namespace SonicDesktopRelay.Rtc.Tests;

public sealed class H264RtpIntegrityTests
{
    private const uint Timestamp = 90_000;

    [Fact]
    public void Contiguous_fu_a_fragments_emit_one_valid_access_unit()
    {
        var assembler = new H264RtpAccessUnitAssembler();

        Assert.Null(Push(assembler, 100, FuStart(0x11)));
        Assert.Null(Push(assembler, 101, FuMiddle(0x22)));
        Assert.Null(Push(assembler, 102, FuMiddle(0x33)));

        var completed = Push(assembler, 103, FuEnd(0x44), marker: true);

        Assert.NotNull(completed);
        Assert.True(completed.Value.IsIdr);
        Assert.Equal(0, assembler.IncompleteAccessUnitsDropped);
    }

    [Fact]
    public void Missing_middle_rtp_packet_drops_the_access_unit()
    {
        var assembler = new H264RtpAccessUnitAssembler();
        var drops = new List<H264AccessUnitDrop>();
        assembler.AccessUnitDropped += drops.Add;

        Push(assembler, 100, FuStart(0x11));
        Push(assembler, 101, FuMiddle(0x22));
        Push(assembler, 103, FuMiddle(0x33));
        var completed = Push(assembler, 104, FuEnd(0x44), marker: true);

        Assert.Null(completed);
        var drop = Assert.Single(drops);
        Assert.Equal(H264AccessUnitDropKind.Incomplete, drop.Kind);
        Assert.Equal("rtp-sequence-gap", drop.Reason);
        Assert.Equal(1, drop.MissingPackets);
        Assert.Equal(1, assembler.RtpPacketsLost);
        Assert.Equal(1, assembler.IncompleteAccessUnitsDropped);
    }

    [Fact]
    public void Gap_between_complete_access_units_is_still_reported_as_transport_loss()
    {
        var assembler = new H264RtpAccessUnitAssembler();
        var gaps = new List<H264RtpGap>();
        assembler.RtpGapDetected += gaps.Add;

        var first = assembler.Push(100, Timestamp, marker: true, [0x65, 0x11]);
        var second = assembler.Push(102, Timestamp * 2, marker: true, [0x41, 0x22]);

        Assert.NotNull(first);
        Assert.NotNull(second);
        var gap = Assert.Single(gaps);
        Assert.Equal((ushort)100, gap.PreviousSequence);
        Assert.Equal((ushort)102, gap.NextSequence);
        Assert.Equal(1, gap.MissingPackets);
        Assert.Equal(1, assembler.RtpPacketsLost);
    }

    [Fact]
    public void Parameter_set_access_unit_is_safe_non_vcl_metadata()
    {
        var assembler = new H264RtpAccessUnitAssembler();

        var completed = assembler.Push(100, Timestamp, marker: true, [0x67, 0x42, 0x00, 0x1F]);

        Assert.NotNull(completed);
        Assert.False(completed.Value.IsIdr);
        Assert.False(completed.Value.HasVcl);
    }

    [Fact]
    public void Missing_fu_a_start_is_rejected()
    {
        var assembler = new H264RtpAccessUnitAssembler();

        Push(assembler, 100, FuMiddle(0x11));
        var completed = Push(assembler, 101, FuEnd(0x22), marker: true);

        Assert.Null(completed);
        Assert.Equal(1, assembler.IncompleteAccessUnitsDropped);
    }

    [Fact]
    public void Missing_fu_a_end_is_rejected()
    {
        var assembler = new H264RtpAccessUnitAssembler();

        Push(assembler, 100, FuStart(0x11));
        var completed = Push(assembler, 101, FuMiddle(0x22), marker: true);

        Assert.Null(completed);
        Assert.Equal(1, assembler.IncompleteAccessUnitsDropped);
    }

    [Fact]
    public void Sequence_number_wrap_is_contiguous()
    {
        var assembler = new H264RtpAccessUnitAssembler();

        Push(assembler, 65534, FuStart(0x11));
        Push(assembler, 65535, FuMiddle(0x22));
        Push(assembler, 0, FuMiddle(0x33));
        var completed = Push(assembler, 1, FuEnd(0x44), marker: true);

        Assert.NotNull(completed);
        Assert.Equal(0, assembler.RtpSequenceGaps);
        Assert.Equal(0, assembler.RtpPacketsLost);
    }

    [Fact]
    public void Out_of_order_but_complete_packets_are_reordered_without_false_loss()
    {
        var assembler = new H264RtpAccessUnitAssembler();

        Push(assembler, 100, FuStart(0x11));
        Push(assembler, 102, FuMiddle(0x33));
        Push(assembler, 101, FuMiddle(0x22));
        var completed = Push(assembler, 103, FuEnd(0x44), marker: true);

        Assert.NotNull(completed);
        Assert.True(assembler.RtpPacketsReordered > 0);
        Assert.Equal(0, assembler.RtpPacketsLost);
    }

    private static H264AssembledAccessUnit? Push(
        H264RtpAccessUnitAssembler assembler,
        ushort sequence,
        byte[] payload,
        bool marker = false) =>
        assembler.Push(sequence, Timestamp, marker, payload);

    // FU indicator: NRI=3 + type=28 (FU-A). Reconstructed NAL type is 5 (IDR).
    private static byte[] FuStart(byte data) => [0x7C, 0x85, data];
    private static byte[] FuMiddle(byte data) => [0x7C, 0x05, data];
    private static byte[] FuEnd(byte data) => [0x7C, 0x45, data];
}

public sealed class H264RtpIntegrityRecoveryTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.UnixEpoch;

    [Fact]
    public void Repeated_loss_signals_create_one_episode_and_coalesce_immediate_pli()
    {
        var recovery = new ViewerVideoRecoveryGate(TimeSpan.FromSeconds(1));

        recovery.BeginRecovery();
        Assert.True(recovery.TryRequestPli(Start));

        recovery.BeginRecovery();
        Assert.False(recovery.TryRequestPli(Start + TimeSpan.FromMilliseconds(100)));

        Assert.Equal(1, recovery.RecoveryEpisodes);
        Assert.Equal(1, recovery.RecoveryKeyframesRequested);
    }

    [Fact]
    public void Dependent_access_units_are_suppressed_while_recovering()
    {
        var recovery = new ViewerVideoRecoveryGate(TimeSpan.FromSeconds(1));
        recovery.BeginRecovery();

        Assert.False(recovery.ShouldDeliver(isIdr: false, hasVcl: true));
        Assert.False(recovery.ShouldDeliver(isIdr: false, hasVcl: true));

        Assert.Equal(2, recovery.SuspectAccessUnitsSuppressed);
        Assert.True(recovery.Active);
    }

    [Fact]
    public void Parameter_sets_are_delivered_without_ending_recovery()
    {
        var recovery = new ViewerVideoRecoveryGate(TimeSpan.FromSeconds(1));
        recovery.BeginRecovery();

        Assert.True(recovery.ShouldDeliver(isIdr: false, hasVcl: false));
        Assert.True(recovery.Active);
        Assert.Equal(0, recovery.SuspectAccessUnitsSuppressed);
    }

    [Fact]
    public void Clean_idr_resumes_delivery_and_rearms_future_recovery()
    {
        var recovery = new ViewerVideoRecoveryGate(TimeSpan.FromSeconds(1));
        recovery.BeginRecovery();

        Assert.True(recovery.ShouldDeliver(isIdr: true, hasVcl: true));
        Assert.False(recovery.Active);
        Assert.True(recovery.ShouldDeliver(isIdr: false, hasVcl: true));

        recovery.BeginRecovery();
        Assert.True(recovery.TryRequestPli(Start + TimeSpan.FromSeconds(2)));
        Assert.Equal(2, recovery.RecoveryEpisodes);
    }
}
