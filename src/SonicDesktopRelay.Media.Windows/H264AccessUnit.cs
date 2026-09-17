namespace SonicDesktopRelay.Media.Windows;

internal static class H264AccessUnit
{
    private static readonly byte[] StartCode = [0, 0, 0, 1];

    internal static byte[] ToAnnexB(ReadOnlySpan<byte> source, int nalLengthSize = 4)
    {
        if (source.IsEmpty) return [];
        if (nalLengthSize is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(nalLengthSize));

        if (StartsWithStartCode(source))
            return source.ToArray();

        using var output = new MemoryStream(source.Length + 16);
        var offset = 0;
        while (offset < source.Length)
        {
            if (source.Length - offset < nalLengthSize)
                throw new InvalidDataException("H.264 access unit ends inside a NAL length prefix.");

            var length = 0;
            for (var i = 0; i < nalLengthSize; i++)
                length = checked((length << 8) | source[offset + i]);
            offset += nalLengthSize;

            if (length <= 0 || length > source.Length - offset)
                throw new InvalidDataException("H.264 NAL length exceeds the access-unit payload.");

            output.Write(StartCode);
            output.Write(source.Slice(offset, length));
            offset += length;
        }

        return output.ToArray();
    }

    internal static bool ContainsKeyFrame(ReadOnlySpan<byte> annexB) => ContainsNalType(annexB, 5);

    internal static bool ContainsSps(ReadOnlySpan<byte> annexB) => ContainsNalType(annexB, 7);

    internal static bool ContainsPps(ReadOnlySpan<byte> annexB) => ContainsNalType(annexB, 8);

    private static bool ContainsNalType(ReadOnlySpan<byte> data, int expectedType)
    {
        var offset = 0;
        while (TryFindStartCode(data, offset, out var start, out var size))
        {
            var nal = start + size;
            if (nal < data.Length && (data[nal] & 0x1F) == expectedType)
                return true;
            offset = nal + 1;
        }
        return false;
    }

    private static bool StartsWithStartCode(ReadOnlySpan<byte> data) =>
        data.Length >= 3 && data[0] == 0 && data[1] == 0
        && (data[2] == 1 || (data.Length >= 4 && data[2] == 0 && data[3] == 1));

    private static bool TryFindStartCode(ReadOnlySpan<byte> data, int offset, out int start, out int size)
    {
        for (var i = Math.Max(0, offset); i <= data.Length - 3; i++)
        {
            if (data[i] != 0 || data[i + 1] != 0) continue;
            if (data[i + 2] == 1)
            {
                start = i;
                size = 3;
                return true;
            }
            if (i <= data.Length - 4 && data[i + 2] == 0 && data[i + 3] == 1)
            {
                start = i;
                size = 4;
                return true;
            }
        }
        start = 0;
        size = 0;
        return false;
    }
}
