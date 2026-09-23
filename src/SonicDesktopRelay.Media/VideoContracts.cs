namespace SonicDesktopRelay.Media;

public readonly record struct MonitorInfo(string Id, string Name, int Width, int Height, bool IsPrimary);

/// <summary>
/// The publisher's user-selected ceiling. Adaptive quality may move below these values, never above them.
/// </summary>
public sealed record VideoPublishProfile(int MaxHeight, int MaxFramesPerSecond)
{
    public static VideoPublishProfile Default { get; } = new(1080, 30);
}

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

    public static VideoQuality InitialFor(VideoPublishProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.MaxHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(profile), "MaxHeight must be positive.");
        if (profile.MaxFramesPerSecond <= 0)
            throw new ArgumentOutOfRangeException(nameof(profile), "MaxFramesPerSecond must be positive.");

        var rung = Ladder.FirstOrDefault(x => x.MaxHeight <= profile.MaxHeight) ?? Ladder[^1];
        return ApplyProfile(rung, profile);
    }

    /// <summary>
    /// The next rung down, or the floor. Degrading is driven by the worst viewer's RTCP, so
    /// it must terminate: a session on a bad link settles at 360p rather than spiralling.
    /// </summary>
    public VideoQuality Reduced() => Reduced(VideoPublishProfile.Default);

    public VideoQuality Reduced(VideoPublishProfile profile)
    {
        var index = FindLadderIndex(this);
        var rung = index < 0 || index >= Ladder.Length - 1 ? Ladder[^1] : Ladder[index + 1];
        return ApplyProfile(rung, profile);
    }

    /// <summary>The next rung toward the configured/user ceiling, or that ceiling.</summary>
    public VideoQuality Improved() => Improved(VideoPublishProfile.Default);

    public VideoQuality Improved(VideoPublishProfile profile)
    {
        var ceilingIndex = FindLadderIndex(InitialFor(profile));
        var index = FindLadderIndex(this);
        if (index < 0) return InitialFor(profile);
        if (index <= ceilingIndex) return InitialFor(profile);
        return ApplyProfile(Ladder[index - 1], profile);
    }

    private static int FindLadderIndex(VideoQuality quality) =>
        Array.FindIndex(Ladder, x =>
            x.MaxHeight == quality.MaxHeight
            && x.TargetBitsPerSecond == quality.TargetBitsPerSecond);

    private static VideoQuality ApplyProfile(VideoQuality rung, VideoPublishProfile profile)
    {
        var fps = rung.FramesPerSecond >= 30
            ? profile.MaxFramesPerSecond
            : Math.Min(rung.FramesPerSecond, profile.MaxFramesPerSecond);
        return rung with { FramesPerSecond = fps };
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
    int Height,
    TimeSpan Duration)
{
    private static readonly TimeSpan LegacyThirtyFpsDuration =
        TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 30);

    /// <summary>
    /// Compatibility constructor for receive/test paths that do not use sample duration.
    /// Publisher encoders must use the six-argument constructor so RTC timing follows the
    /// effective configured cadence instead of silently assuming 30 FPS.
    /// </summary>
    public EncodedVideoSample(
        ReadOnlyMemory<byte> Data,
        TimeSpan Timestamp,
        bool IsKeyFrame,
        int Width,
        int Height)
        : this(Data, Timestamp, IsKeyFrame, Width, Height, LegacyThirtyFpsDuration)
    {
    }
}


public enum KeyFrameRequestReason
{
    Manual,
    InitialConnection,
    RtcpPli,
    RtcpFir,
    /// <summary>A stale or dropped encoded sample in the local send queue needs a clean point.</summary>
    PacketLoss,
    QualityChange
}
