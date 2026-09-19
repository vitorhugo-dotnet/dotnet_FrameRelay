namespace SonicDesktopRelay.Rtc;

/// <summary>
/// Converts encoded video sample duration to the fixed 90 kHz WebRTC/RTP video clock.
/// Keeping this pure prevents transport code from silently assuming a specific FPS.
/// </summary>
public static class VideoRtpTiming
{
    public const uint ClockRate = 90_000;

    public static uint ToTimestampUnits(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(duration), "Video duration must be positive.");

        var units = Math.Round(
            duration.TotalSeconds * ClockRate,
            MidpointRounding.AwayFromZero);

        if (units < 1 || units > uint.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(duration), "Video duration is outside the RTP timestamp range.");

        return checked((uint)units);
    }
}
