using Microsoft.Extensions.Time.Testing;

namespace SonicDesktopRelay.Media.Tests;

public sealed class MediaSessionClockTests
{
    [Fact]
    public void Now_is_monotonic_elapsed_time_from_one_session_origin()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var clock = new MediaSessionClock(time);

        Assert.Equal(TimeSpan.Zero, clock.Now);

        time.Advance(TimeSpan.FromMilliseconds(20));
        Assert.Equal(TimeSpan.FromMilliseconds(20), clock.Now);

        time.Advance(TimeSpan.FromMilliseconds(80));
        Assert.Equal(TimeSpan.FromMilliseconds(100), clock.Now);
    }
}
