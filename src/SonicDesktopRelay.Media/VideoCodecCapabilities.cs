namespace SonicDesktopRelay.Media;

/// <summary>Profile and maximum level supported for a codec direction.</summary>
public sealed record VideoCodecConstraints(string Profile, int MaxLevel);

/// <summary>Hardware codec support reported by one local peer.</summary>
public sealed record VideoCodecCapabilities(
    IReadOnlySet<VideoCodec> Encoders,
    IReadOnlySet<VideoCodec> Decoders,
    IReadOnlyDictionary<VideoCodec, VideoCodecConstraints> EncoderConstraints,
    IReadOnlyDictionary<VideoCodec, VideoCodecConstraints> DecoderConstraints,
    IReadOnlyDictionary<VideoCodec, string> RejectionReasons);

/// <summary>Codec selected for a shared session stream, including why fallback was used.</summary>
public sealed record VideoCodecSelection(VideoCodec Codec, string? FallbackReason);
