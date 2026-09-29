namespace SonicDesktopRelay.Media;

/// <summary>Chooses the session's one encoded codec from publisher and viewer capabilities.</summary>
public static class VideoCodecNegotiator
{
    private static readonly (int Level, long MaxPicSize, int MaxWidth, int MaxHeight, long MaxDisplayRate)[] Av1Levels =
    [
        (0, 147_456, 2_048, 1_152, 4_423_680),
        (1, 278_784, 2_816, 1_584, 8_363_520),
        (4, 665_856, 4_352, 2_448, 19_975_680),
        (5, 1_065_024, 5_504, 3_096, 31_950_720),
        (8, 2_359_296, 6_144, 3_456, 70_778_880),
        (9, 2_359_296, 6_144, 3_456, 141_557_760),
        (12, 8_912_896, 8_192, 4_352, 267_386_880),
        (13, 8_912_896, 8_192, 4_352, 534_773_760),
        (14, 8_912_896, 8_192, 4_352, 1_069_547_520),
        (15, 8_912_896, 8_192, 4_352, 1_069_547_520),
        (16, 35_651_584, 16_384, 8_704, 1_069_547_520),
        (17, 35_651_584, 16_384, 8_704, 2_139_095_040),
        (18, 35_651_584, 16_384, 8_704, 4_278_190_080),
        (19, 35_651_584, 16_384, 8_704, 4_278_190_080)
    ];

    /// <summary>
    /// Returns the least AV1 sequence level whose Annex A picture dimensions and display rate
    /// contain this encoded workload. int.MaxValue is a fail-closed unsupported-workload marker.
    /// </summary>
    public static VideoCodecConstraints RequiredAv1Constraints(int width, int height, int framesPerSecond)
    {
        if (width <= 0 || height <= 0 || framesPerSecond <= 0)
            return new VideoCodecConstraints("0", int.MaxValue);

        var pictureSize = (long)width * height;
        var displayRate = pictureSize * framesPerSecond;
        var level = Av1Levels.FirstOrDefault(item =>
            pictureSize <= item.MaxPicSize
            && width <= item.MaxWidth
            && height <= item.MaxHeight
            && displayRate <= item.MaxDisplayRate);
        return new VideoCodecConstraints("0", level == default ? int.MaxValue : level.Level);
    }

    public static VideoCodecSelection Select(
        VideoCodecCapabilities publisher,
        IReadOnlyCollection<VideoCodecCapabilities> viewers,
        VideoCodecConstraints requiredAv1)
    {
        ArgumentNullException.ThrowIfNull(publisher);
        ArgumentNullException.ThrowIfNull(viewers);
        ArgumentNullException.ThrowIfNull(requiredAv1);

        if (viewers.Count == 0)
            return H264("no-active-viewers");
        if (!publisher.Encoders.Contains(VideoCodec.Av1))
            return H264("publisher-av1-encoder-unavailable");
        if (viewers.Any(viewer => !viewer.Decoders.Contains(VideoCodec.Av1)))
            return H264("viewer-av1-decoder-unavailable");

        if (!publisher.EncoderConstraints.TryGetValue(VideoCodec.Av1, out var encoderConstraints)
            || viewers.Any(viewer => !viewer.DecoderConstraints.ContainsKey(VideoCodec.Av1)))
            return H264("av1-configuration-unavailable");

        if (!ProfileMatches(encoderConstraints, requiredAv1)
            || viewers.Any(viewer => !ProfileMatches(viewer.DecoderConstraints[VideoCodec.Av1], requiredAv1)))
            return H264("av1-profile-incompatible");
        if (encoderConstraints.MaxLevel < requiredAv1.MaxLevel
            || viewers.Any(viewer => viewer.DecoderConstraints[VideoCodec.Av1].MaxLevel < requiredAv1.MaxLevel))
            return H264("av1-level-insufficient");

        return new VideoCodecSelection(VideoCodec.Av1, null);
    }

    private static bool ProfileMatches(VideoCodecConstraints actual, VideoCodecConstraints required) =>
        string.Equals(actual.Profile, required.Profile, StringComparison.OrdinalIgnoreCase);

    private static VideoCodecSelection H264(string reason) => new(VideoCodec.H264, reason);
}
