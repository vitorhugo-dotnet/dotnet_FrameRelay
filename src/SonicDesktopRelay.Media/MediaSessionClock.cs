namespace SonicDesktopRelay.Media;

public sealed class MediaSessionClock(TimeProvider timeProvider)
{
    private readonly long _started = timeProvider.GetTimestamp();

    public TimeSpan Now => timeProvider.GetElapsedTime(_started, timeProvider.GetTimestamp());
}
