namespace SonicDesktopRelay.Media;

/// <summary>Chooses the session's one encoded codec from publisher and viewer capabilities.</summary>
public static class VideoCodecNegotiator
{
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
