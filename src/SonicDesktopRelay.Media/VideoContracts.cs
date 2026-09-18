namespace SonicDesktopRelay.Media;

public readonly record struct MonitorInfo(string Id, string Name, int Width, int Height, bool IsPrimary);

/// <summary>
/// The session's single quality target. There is one for the whole session, not one per
/// viewer: the screen is encoded once and handed to everyone, so quality is a property of
/// the encode, not of a connection.
/// </summary>
public sealed record VideoQuality(int MaxHeight, int FramesPerSecond, int TargetBitsPerSecond)
{
    // Desktop sharing is resolution-sensitive. Spend bitrate before pixels: transient or
    // moderate congestion should keep text at native 1080p whenever the encoder can do so.
    private static readonly VideoQuality[] Ladder =
    [
        new(1080, 30, 4_000_000),
        new(1080, 30, 3_000_000),
        new(1080, 30, 2_000_000),
        new(720, 30, 2_000_000),
        new(720, 30, 1_500_000),
        new(540, 20, 1_000_000),
        new(360, 15, 600_000)
    ];

    public static VideoQuality Default => Ladder[0];

    /// <summary>
    /// The next rung down, or the floor. Degrading is driven by the worst viewer's RTCP, so
    /// it must terminate: a session on a bad link settles at 360p rather than spiralling.
    /// </summary>
    public VideoQuality Reduced()
    {
        var index = Array.FindIndex(Ladder, x => x == this);
        if (index < 0) return Ladder[^1];
        return index >= Ladder.Length - 1 ? Ladder[^1] : Ladder[index + 1];
    }

    /// <summary>The next rung toward the configured/default desktop quality, or the ceiling.</summary>
    public VideoQuality Improved()
    {
        var index = Array.FindIndex(Ladder, x => x == this);
        if (index <= 0) return Ladder[0];
        return Ladder[index - 1];
    }

    /// <summary>
    /// Output dimensions for a source of this size: never upscaled, aspect preserved, and
    /// both values even — H.264 4:2:0 chroma subsampling cannot represent odd dimensions.
    /// </summary>
    public (int Width, int Height) ScaleFor(int sourceWidth, int sourceHeight)
    {
        var height = Math.Min(MaxHeight, sourceHeight);
        var width = (int)Math.Round(sourceWidth * (height / (double)sourceHeight));
        return (MakeEven(width), MakeEven(height));
    }

    private static int MakeEven(int value) => value % 2 == 0 ? value : value - 1;
}

/// <summary>One captured frame, BGRA8888, top-down, tightly packed.</summary>
public sealed class VideoFrame(int width, int height, ReadOnlyMemory<byte> bgra, TimeSpan timestamp)
{
    public int Width { get; } = width;

    public int Height { get; } = height;

    public ReadOnlyMemory<byte> Bgra { get; } = bgra;

    public TimeSpan Timestamp { get; } = timestamp;
}

public readonly record struct EncodedVideoSample(
    ReadOnlyMemory<byte> Data,
    TimeSpan Timestamp,
    bool IsKeyFrame,
    int Width,
    int Height);


public enum KeyFrameRequestReason
{
    Manual,
    InitialConnection,
    RtcpPli,
    RtcpFir,
    PacketLoss,
    QualityChange
}
