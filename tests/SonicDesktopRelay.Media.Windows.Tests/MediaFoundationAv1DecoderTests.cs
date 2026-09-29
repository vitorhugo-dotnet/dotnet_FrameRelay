using SonicDesktopRelay.Media.Windows;

namespace SonicDesktopRelay.Media.Windows.Tests;

public sealed class MediaFoundationAv1DecoderTests
{
    [Fact]
    public void MFT_output_timestamp_is_preferred_for_reordered_output()
    {
        var tracker = new MediaFoundationOutputTimestampTracker();
        var firstSubmitted = TimeSpan.FromMilliseconds(10);
        var secondSubmitted = TimeSpan.FromMilliseconds(20);
        tracker.Submitted(firstSubmitted);
        tracker.Submitted(secondSubmitted);

        Assert.Equal(secondSubmitted, tracker.ForOutput(hasMftTimestamp: true, secondSubmitted));
        Assert.Equal(firstSubmitted, tracker.ForOutput(hasMftTimestamp: true, firstSubmitted));
    }

    [Fact]
    public void Missing_MFT_output_timestamp_uses_the_oldest_submission_timestamp()
    {
        var tracker = new MediaFoundationOutputTimestampTracker();
        var first = TimeSpan.FromMilliseconds(10);
        var second = TimeSpan.FromMilliseconds(20);
        tracker.Submitted(first);
        tracker.Submitted(second);

        Assert.Equal(first, tracker.ForOutput(hasMftTimestamp: false, default));
        Assert.Equal(second, tracker.ForOutput(hasMftTimestamp: false, default));
        Assert.Null(tracker.ForOutput(hasMftTimestamp: false, default));
    }

    [Fact]
    public void Clearing_after_transform_reconfiguration_discards_old_timestamps()
    {
        var tracker = new MediaFoundationOutputTimestampTracker();
        tracker.Submitted(TimeSpan.FromMilliseconds(10));
        tracker.Clear();

        Assert.Null(tracker.ForOutput(hasMftTimestamp: false, default));
    }
}
