using SIPSorcery.Net;

namespace SonicDesktopRelay.Rtc;

internal sealed record Av1AccessUnitDrop(string Reason, uint Timestamp);

internal readonly record struct Av1AssembledAccessUnit(
    byte[] Data,
    uint Timestamp,
    bool IsKeyFrame,
    bool HasVcl);

/// <summary>
/// Bounds and validates one AV1 RTP temporal unit before handing its payloads to the
/// SIPSorcery 10.0.16 depacketiser. SIPSorcery's depacketiser sorts sequence numbers but does
/// not reject gaps, so continuity is checked here before any reconstruction takes place.
/// </summary>
internal sealed class Av1RtpAccessUnitAssembler
{
    public const int DefaultMaxRetainedBytes = 8 * 1024 * 1024;
    public const int DefaultMaxPacketsPerAccessUnit = 4096;

    private readonly object _gate = new();
    private readonly List<Packet> _packets = [];
    private readonly int _maxRetainedBytes;
    private readonly int _maxPacketsPerAccessUnit;
    private uint? _timestamp;
    private ushort? _lastArrivalSequence;
    private int _retainedBytes;
    private uint? _discardTimestamp;
    private bool? _reducedStillPictureHeader;

    public Av1RtpAccessUnitAssembler(
        int maxRetainedBytes = DefaultMaxRetainedBytes,
        int maxPacketsPerAccessUnit = DefaultMaxPacketsPerAccessUnit)
    {
        if (maxRetainedBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxRetainedBytes));
        if (maxPacketsPerAccessUnit <= 0) throw new ArgumentOutOfRangeException(nameof(maxPacketsPerAccessUnit));
        _maxRetainedBytes = maxRetainedBytes;
        _maxPacketsPerAccessUnit = maxPacketsPerAccessUnit;
    }

    public event Action<Av1AccessUnitDrop>? AccessUnitDropped;

    public long RtpPacketsReceived { get; private set; }
    public long RtpPacketsReordered { get; private set; }
    public long RtpSequenceGaps { get; private set; }
    public long RtpPacketsLost { get; private set; }
    public long AccessUnitsReceived { get; private set; }
    public long IncompleteAccessUnitsDropped { get; private set; }
    public long CorruptAccessUnitsDropped { get; private set; }
    public int RetainedBytes
    {
        get { lock (_gate) return _retainedBytes; }
    }

    public Av1AssembledAccessUnit? Push(ushort sequenceNumber, uint timestamp, bool marker, byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        lock (_gate)
        {
            RtpPacketsReceived++;
            if (_discardTimestamp == timestamp) return null;
            if (_discardTimestamp is not null && _discardTimestamp != timestamp)
                _discardTimestamp = null;
            if (_timestamp is { } oldTimestamp && oldTimestamp != timestamp && _packets.Count > 0)
                DropAndReset(
                    _packets.Any(packet => packet.Marker) ? "rtp-sequence-gap" : "timestamp-changed-before-marker",
                    oldTimestamp,
                    incomplete: true);

            if (payload.Length < 2 || (payload[0] & 0x07) != 0)
            {
                DropAndReset("malformed-av1-payload", timestamp, incomplete: false);
                return null;
            }

            if (payload.Length > _maxRetainedBytes)
            {
                DropAndReset("access-unit-size-limit", timestamp, incomplete: false);
                return null;
            }

            if (_lastArrivalSequence is { } previous && IsBefore(sequenceNumber, previous))
                RtpPacketsReordered++;
            _lastArrivalSequence = sequenceNumber;
            _timestamp ??= timestamp;
            if (_packets.Count >= _maxPacketsPerAccessUnit || _retainedBytes + payload.Length > _maxRetainedBytes)
            {
                DropAndReset("access-unit-size-limit", timestamp, incomplete: false);
                return null;
            }

            var copy = payload.ToArray();
            _packets.Add(new Packet(sequenceNumber, marker, copy));
            _retainedBytes += copy.Length;
            if (!_packets.Any(packet => packet.Marker)) return null;

            return Complete(timestamp);
        }
    }

    private Av1AssembledAccessUnit? Complete(uint timestamp)
    {
        var ordered = _packets.OrderBy(packet => packet.SequenceNumber, SequenceComparer.Instance).ToArray();
        if (ordered.Length == 0 || ordered[^1].Marker == false || ordered.Count(packet => packet.Marker) != 1)
            return DropAndReset("invalid-marker-position", timestamp, incomplete: false);
        if (ordered.Select(packet => packet.SequenceNumber).Distinct().Count() != ordered.Length)
            return DropAndReset("duplicate-sequence", timestamp, incomplete: false);

        // The marker can arrive before earlier fragments on a reordered path. A leading Z bit
        // proves that the first buffered packet is a continuation, so wait within the existing
        // byte/packet bounds for its beginning instead of prematurely rejecting the unit.
        if ((ordered[0].Payload[0] & 0x80) != 0)
            return null;

        for (var i = 1; i < ordered.Length; i++)
        {
            if (ordered[i].SequenceNumber != unchecked((ushort)(ordered[i - 1].SequenceNumber + 1)))
                return null;
        }

        var previousContinues = false;
        for (var i = 0; i < ordered.Length; i++)
        {
            var payload = ordered[i].Payload;
            var continuesPrevious = (payload[0] & 0x80) != 0;
            var continuesNext = (payload[0] & 0x40) != 0;
            var startsSequence = (payload[0] & 0x08) != 0;
            if (continuesPrevious != previousContinues || (i > 0 && startsSequence) || !ValidateObuElements(payload))
                return DropAndReset("malformed-av1-payload", timestamp, incomplete: false);
            previousContinues = continuesNext;
        }

        if (previousContinues)
            return DropAndReset("incomplete-fragmented-obu", timestamp, incomplete: true);

        byte[]? accessUnit = null;
        var startsNewSequence = (ordered[0].Payload[0] & 0x08) != 0;
        var depacketiser = new AV1Depacketiser();
        try
        {
            foreach (var packet in ordered)
            {
                using var result = depacketiser.ProcessRTPPayload(
                    packet.Payload,
                    packet.SequenceNumber,
                    timestamp,
                    packet.Marker ? 1 : 0,
                    out _);
                if (result is not null) accessUnit = result.ToArray();
            }
        }
        catch (Exception)
        {
            return DropAndReset("malformed-av1-payload", timestamp, incomplete: false);
        }

        Reset();
        if (accessUnit is null || accessUnit.Length == 0 || accessUnit.Length > _maxRetainedBytes
            || !ValidateTemporalUnit(accessUnit))
            return DropAndReset("malformed-av1-payload", timestamp, incomplete: false);

        var (hasFrame, isKeyFrame) = InspectFrame(accessUnit, startsNewSequence);
        AccessUnitsReceived++;
        return new Av1AssembledAccessUnit(accessUnit, timestamp, isKeyFrame, hasFrame);
    }

    private (bool HasFrame, bool IsKeyFrame) InspectFrame(byte[] temporalUnit, bool startsNewSequence)
    {
        if (startsNewSequence)
            _reducedStillPictureHeader = null;

        var hasFrame = false;
        var isKeyFrame = false;
        foreach (var obu in AV1Packetiser.ParseObus(temporalUnit))
        {
            switch (AV1Packetiser.GetObuType(obu))
            {
                case AV1Packetiser.AV1ObuType.SequenceHeader:
                    _reducedStillPictureHeader = TryReadReducedStillPictureHeader(obu, out var reduced)
                        ? reduced
                        : null;
                    break;

                case AV1Packetiser.AV1ObuType.FrameHeader:
                case AV1Packetiser.AV1ObuType.Frame:
                    hasFrame = true;
                    isKeyFrame |= IsKeyFrameObu(obu, _reducedStillPictureHeader);
                    break;
            }
        }

        return (hasFrame, isKeyFrame);
    }

    private static bool TryReadReducedStillPictureHeader(byte[] obu, out bool reduced)
    {
        reduced = false;
        if (!TryGetObuPayload(obu, out var payload) || payload.IsEmpty)
            return false;

        // seq_profile (3 bits), still_picture (1 bit), reduced_still_picture_header (1 bit).
        reduced = (payload[0] & 0x08) != 0;
        return true;
    }

    private static bool IsKeyFrameObu(byte[] obu, bool? reducedStillPictureHeader)
    {
        if (reducedStillPictureHeader is not { } reduced || !TryGetObuPayload(obu, out var payload))
            return false;
        if (reduced)
            return true;

        var bits = new Av1BitReader(payload);
        if (!bits.TryReadBit(out var showExistingFrame) || showExistingFrame)
            return false;

        // frame_type is the next two bits after show_existing_frame; KEY_FRAME is zero.
        return bits.TryReadBits(2, out var frameType) && frameType == 0;
    }

    private static bool TryGetObuPayload(byte[] obu, out ReadOnlySpan<byte> payload)
    {
        payload = default;
        if (obu.Length == 0) return false;

        var offset = 1;
        if ((obu[0] & 0x04) != 0) offset++;
        if ((obu[0] & 0x02) != 0)
        {
            if (!AV1Packetiser.TryReadLeb128(obu, ref offset, out var size, out _)
                || size < 0 || size > obu.Length - offset)
                return false;
            payload = obu.AsSpan(offset, size);
            return true;
        }

        payload = obu.AsSpan(offset);
        return true;
    }

    private ref struct Av1BitReader
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _position;

        public Av1BitReader(ReadOnlySpan<byte> data)
        {
            _data = data;
            _position = 0;
        }

        public bool TryReadBit(out bool value)
        {
            value = false;
            if (_position >= _data.Length * 8) return false;
            value = (_data[_position / 8] & (1 << (7 - (_position % 8)))) != 0;
            _position++;
            return true;
        }

        public bool TryReadBits(int count, out int value)
        {
            value = 0;
            for (var i = 0; i < count; i++)
            {
                if (!TryReadBit(out var bit)) return false;
                value = (value << 1) | (bit ? 1 : 0);
            }
            return true;
        }
    }

    private static bool ValidateObuElements(byte[] payload)
    {
        var count = (payload[0] >> 4) & 0x03;
        var startsFragment = (payload[0] & 0x80) != 0;
        var endsFragment = (payload[0] & 0x40) != 0;
        var offset = 1;
        if (count == 0)
        {
            var elements = 0;
            while (offset < payload.Length)
            {
                if (!AV1Packetiser.TryReadLeb128(payload, ref offset, out var length, out _)
                    || length <= 0 || length > payload.Length - offset)
                    return false;
                var isFragment = (startsFragment && elements == 0)
                                 || (endsFragment && offset + length == payload.Length);
                if (!isFragment && !ValidateObu(payload.AsSpan(offset, length))) return false;
                offset += length;
                elements++;
            }
            return elements > 0 && offset == payload.Length;
        }

        for (var i = 0; i < count; i++)
        {
            int length;
            if (i == count - 1)
                length = payload.Length - offset;
            else if (!AV1Packetiser.TryReadLeb128(payload, ref offset, out length, out _))
                return false;

            if (length <= 0 || length > payload.Length - offset) return false;
            var isFragment = (startsFragment && i == 0)
                             || (endsFragment && i == count - 1);
            if (!isFragment && !ValidateObu(payload.AsSpan(offset, length))) return false;
            offset += length;
        }

        return offset == payload.Length;
    }

    private static bool ValidateTemporalUnit(byte[] temporalUnit)
    {
        try
        {
            var obus = AV1Packetiser.ParseObus(temporalUnit).ToArray();
            return obus.Length > 0 && obus.All(obu => ValidateObu(obu));
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool ValidateObu(ReadOnlySpan<byte> obu)
    {
        if (obu.IsEmpty || (obu[0] & 0x81) != 0 || ((obu[0] >> 3) & 0x0f) == 0)
            return false;

        var offset = 1;
        if ((obu[0] & 0x04) != 0)
        {
            if (offset >= obu.Length || (obu[offset] & 0x07) != 0) return false;
            offset++;
        }

        if ((obu[0] & 0x02) == 0) return true;
        long value = 0;
        var shift = 0;
        var count = 0;
        while (offset < obu.Length && count++ < 8)
        {
            var current = obu[offset++];
            value |= (long)(current & 0x7f) << shift;
            if ((current & 0x80) == 0)
                return value <= int.MaxValue && value == obu.Length - offset;
            shift += 7;
        }
        return false;
    }

    private Av1AssembledAccessUnit? DropAndReset(string reason, uint timestamp, bool incomplete)
    {
        if (reason == "rtp-sequence-gap")
        {
            RtpSequenceGaps++;
            RtpPacketsLost += CountMissingPackets();
        }
        Reset();
        _discardTimestamp = timestamp;
        return Drop(reason, timestamp, incomplete);
    }

    private int CountMissingPackets()
    {
        if (_packets.Count < 2) return 1;
        var ordered = _packets.OrderBy(packet => packet.SequenceNumber, SequenceComparer.Instance).ToArray();
        var missing = 0;
        for (var i = 1; i < ordered.Length; i++)
        {
            var distance = unchecked((ushort)(ordered[i].SequenceNumber - ordered[i - 1].SequenceNumber));
            if (distance > 1) missing += distance - 1;
        }
        return Math.Max(1, missing);
    }

    private Av1AssembledAccessUnit? Drop(string reason, uint timestamp, bool incomplete)
    {
        if (incomplete) IncompleteAccessUnitsDropped++;
        else CorruptAccessUnitsDropped++;
        AccessUnitDropped?.Invoke(new Av1AccessUnitDrop(reason, timestamp));
        return null;
    }

    private void Reset()
    {
        _packets.Clear();
        _timestamp = null;
        _lastArrivalSequence = null;
        _retainedBytes = 0;
    }

    private static bool IsBefore(ushort candidate, ushort reference) =>
        unchecked((short)(candidate - reference)) < 0;

    private sealed record Packet(ushort SequenceNumber, bool Marker, byte[] Payload);

    private sealed class SequenceComparer : IComparer<ushort>
    {
        public static SequenceComparer Instance { get; } = new();
        public int Compare(ushort x, ushort y) => unchecked((short)(x - y));
    }
}
