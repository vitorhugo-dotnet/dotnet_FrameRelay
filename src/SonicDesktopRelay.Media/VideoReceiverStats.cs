namespace SonicDesktopRelay.Media;

/// <summary>Bounded, interval-scoped viewer reception telemetry.</summary>
public sealed record VideoReceiverStats(
    int Version,
    long IntervalMilliseconds,
    long RtpPacketsReceived,
    long RtpPacketsLost,
    long AccessUnitsReceived,
    long IncompleteAccessUnits,
    long DecodedFrames,
    double TargetFramesPerSecond);
