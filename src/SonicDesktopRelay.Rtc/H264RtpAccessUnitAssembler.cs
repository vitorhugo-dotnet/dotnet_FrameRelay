using SIPSorcery.Net;

namespace SonicDesktopRelay.Rtc;

internal enum H264AccessUnitDropKind
{
    Incomplete,
    Corrupt
}

internal sealed record H264AccessUnitDrop(
    H264AccessUnitDropKind Kind,
    string Reason,
    uint Timestamp,
    ushort? PreviousSequence,
    ushort? NextSequence,
    int MissingPackets);

internal readonly record struct H264AssembledAccessUnit(
    byte[] Data,
    uint Timestamp,
    bool IsIdr);

/// <summary>
/// Guards the SIPSorcery H.264 depacketizer from packet sets that are known to be incomplete.
///
/// SIPSorcery 10.0.16 reorders the packets belonging to a timestamp but does not verify RTP
/// sequence continuity before rebuilding FU-A NAL units. FrameRelay therefore validates the
/// packet set first and delegates the byte-level Annex-B reconstruction only after integrity is
/// proven. This is deliberately a guard around the existing stack, not a replacement RTP stack.
/// </summary>
internal sealed class H264RtpAccessUnitAssembler
{
    private readonly List<Packet> _packets = [];
    private uint? _timestamp;
    private ushort? _lastArrivalSequence;

    public event Action<H264AccessUnitDrop>? AccessUnitDropped;

    public long RtpPacketsReceived { get; private set; }
    public long RtpSequenceGaps { get; private set; }
    public long RtpPacketsLost { get; private set; }
    public long RtpPacketsReordered { get; private set; }
    public long IncompleteAccessUnitsDropped { get; private set; }
    public long CorruptAccessUnitsDropped { get; private set; }
    public uint? LastDroppedAccessUnitTimestamp { get; private set; }
    public string? LastRtpGap { get; private set; }

    public H264AssembledAccessUnit? Push(
        ushort sequenceNumber,
        uint timestamp,
        bool marker,
        byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        RtpPacketsReceived++;

        if (_timestamp is { } currentTimestamp && currentTimestamp != timestamp && _packets.Count > 0)
        {
            Drop(
                H264AccessUnitDropKind.Incomplete,
                "timestamp-changed-before-marker",
                currentTimestamp,
                null,
                null,
                0);
            ResetFrame();
        }

        _timestamp ??= timestamp;

        if (_lastArrivalSequence is { } previous
            && sequenceNumber != unchecked((ushort)(previous + 1))
            && IsBefore(sequenceNumber, previous))
        {
            RtpPacketsReordered++;
        }

        _lastArrivalSequence = sequenceNumber;
        _packets.Add(new Packet(sequenceNumber, marker, payload));

        if (!marker)
            return null;

        return Complete(timestamp);
    }

    private H264AssembledAccessUnit? Complete(uint timestamp)
    {
        if (_packets.Count == 0)
            return null;

        var bySequence = new Dictionary<ushort, Packet>(_packets.Count);
        foreach (var packet in _packets)
        {
            if (!bySequence.TryAdd(packet.SequenceNumber, packet))
            {
                Drop(
                    H264AccessUnitDropKind.Corrupt,
                    "duplicate-sequence",
                    timestamp,
                    packet.SequenceNumber,
                    packet.SequenceNumber,
                    0);
                ResetFrame();
                return null;
            }
        }

        var starts = bySequence.Values
            .Where(packet => !bySequence.ContainsKey(unchecked((ushort)(packet.SequenceNumber - 1))))
            .ToArray();

        if (starts.Length != 1)
        {
            var missing = Math.Max(1, starts.Length - 1);
            RtpSequenceGaps++;
            RtpPacketsLost += missing;
            LastRtpGap = $"timestamp={timestamp} discontinuities={starts.Length} missingAtLeast={missing}";
            Drop(
                H264AccessUnitDropKind.Incomplete,
                "rtp-sequence-gap",
                timestamp,
                null,
                null,
                missing);
            ResetFrame();
            return null;
        }

        var ordered = new List<Packet>(_packets.Count);
        var expected = starts[0].SequenceNumber;
        for (var index = 0; index < _packets.Count; index++)
        {
            if (!bySequence.TryGetValue(expected, out var packet))
            {
                var next = bySequence.Keys
                    .FirstOrDefault(candidate => IsAfter(candidate, expected));

                RtpSequenceGaps++;
                RtpPacketsLost++;
                LastRtpGap = $"timestamp={timestamp} previous={unchecked((ushort)(expected - 1))} next={next}";
                Drop(
                    H264AccessUnitDropKind.Incomplete,
                    "rtp-sequence-gap",
                    timestamp,
                    unchecked((ushort)(expected - 1)),
                    next,
                    1);
                ResetFrame();
                return null;
            }

            ordered.Add(packet);
            expected = unchecked((ushort)(expected + 1));
        }

        if (ordered.Count(packet => packet.Marker) != 1 || !ordered[^1].Marker)
        {
            Drop(
                H264AccessUnitDropKind.Corrupt,
                "invalid-marker-position",
                timestamp,
                null,
                null,
                0);
            ResetFrame();
            return null;
        }

        if (!ValidateH264Payloads(ordered, timestamp))
        {
            ResetFrame();
            return null;
        }

        byte[]? accessUnit = null;
        var depacketiser = new H264Depacketiser();

        foreach (var packet in ordered)
        {
            using var result = depacketiser.ProcessRTPPayload(
                packet.Payload,
                packet.SequenceNumber,
                timestamp,
                packet.Marker ? 1 : 0,
                out _);

            if (result is not null)
                accessUnit = result.ToArray();
        }

        ResetFrame();

        if (accessUnit is null || accessUnit.Length == 0)
        {
            Drop(
                H264AccessUnitDropKind.Corrupt,
                "depacketizer-produced-no-access-unit",
                timestamp,
                null,
                null,
                0);
            return null;
        }

        return new H264AssembledAccessUnit(accessUnit, timestamp, ContainsIdr(accessUnit));
    }

    private bool ValidateH264Payloads(IReadOnlyList<Packet> packets, uint timestamp)
    {
        var fuOpen = false;

        foreach (var packet in packets)
        {
            var payload = packet.Payload;
            if (payload.Length == 0)
                return DropCorrupt(timestamp, "empty-rtp-payload");

            var nalType = payload[0] & 0x1F;
            switch (nalType)
            {
                case >= 1 and <= 23:
                    if (fuOpen)
                        return DropIncomplete(timestamp, "missing-fua-end");
                    break;

                case 24: // STAP-A
                    if (fuOpen)
                        return DropIncomplete(timestamp, "missing-fua-end");
                    if (!ValidateStapA(payload))
                        return DropCorrupt(timestamp, "malformed-stap-a");
                    break;

                case 28: // FU-A
                    if (payload.Length < 3)
                        return DropCorrupt(timestamp, "malformed-fua");

                    var fuHeader = payload[1];
                    var start = (fuHeader & 0x80) != 0;
                    var end = (fuHeader & 0x40) != 0;
                    var reserved = (fuHeader & 0x20) != 0;
                    var fragmentedNalType = fuHeader & 0x1F;

                    if (reserved || fragmentedNalType == 0 || (start && end))
                        return DropCorrupt(timestamp, "malformed-fua-header");

                    if (start)
                    {
                        if (fuOpen)
                            return DropCorrupt(timestamp, "overlapping-fua-start");
                        fuOpen = true;
                    }
                    else if (!fuOpen)
                    {
                        return DropIncomplete(timestamp, "missing-fua-start");
                    }

                    if (end)
                        fuOpen = false;
                    break;

                default:
                    return DropCorrupt(timestamp, $"unsupported-h264-packetization-type-{nalType}");
            }
        }

        return !fuOpen || DropIncomplete(timestamp, "missing-fua-end");
    }

    private static bool ValidateStapA(byte[] payload)
    {
        if (payload.Length < 4)
            return false;

        var offset = 1;
        var nalCount = 0;
        while (offset < payload.Length)
        {
            if (offset + 2 > payload.Length)
                return false;

            var size = (payload[offset] << 8) | payload[offset + 1];
            offset += 2;
            if (size <= 0 || offset + size > payload.Length)
                return false;

            offset += size;
            nalCount++;
        }

        return nalCount > 0 && offset == payload.Length;
    }

    private bool DropIncomplete(uint timestamp, string reason)
    {
        Drop(H264AccessUnitDropKind.Incomplete, reason, timestamp, null, null, 0);
        return false;
    }

    private bool DropCorrupt(uint timestamp, string reason)
    {
        Drop(H264AccessUnitDropKind.Corrupt, reason, timestamp, null, null, 0);
        return false;
    }

    private void Drop(
        H264AccessUnitDropKind kind,
        string reason,
        uint timestamp,
        ushort? previousSequence,
        ushort? nextSequence,
        int missingPackets)
    {
        if (kind == H264AccessUnitDropKind.Incomplete)
            IncompleteAccessUnitsDropped++;
        else
            CorruptAccessUnitsDropped++;

        LastDroppedAccessUnitTimestamp = timestamp;
        AccessUnitDropped?.Invoke(new H264AccessUnitDrop(
            kind,
            reason,
            timestamp,
            previousSequence,
            nextSequence,
            missingPackets));
    }

    private void ResetFrame()
    {
        _packets.Clear();
        _timestamp = null;
        _lastArrivalSequence = null;
    }

    private static bool IsBefore(ushort candidate, ushort reference) =>
        unchecked((short)(candidate - reference)) < 0;

    private static bool IsAfter(ushort candidate, ushort reference) =>
        unchecked((short)(candidate - reference)) > 0;

    private static bool ContainsIdr(byte[] annexB)
    {
        for (var i = 0; i + 3 < annexB.Length; i++)
        {
            if (annexB[i] != 0 || annexB[i + 1] != 0)
                continue;

            int headerIndex;
            if (annexB[i + 2] == 1)
                headerIndex = i + 3;
            else if (annexB[i + 2] == 0 && i + 4 < annexB.Length && annexB[i + 3] == 1)
                headerIndex = i + 4;
            else
                continue;

            if ((annexB[headerIndex] & 0x1F) == 5)
                return true;
        }

        return false;
    }

    private sealed record Packet(ushort SequenceNumber, bool Marker, byte[] Payload);
}
