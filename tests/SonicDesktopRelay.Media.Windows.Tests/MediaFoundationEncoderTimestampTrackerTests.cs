using SonicDesktopRelay.Media.Windows;

namespace SonicDesktopRelay.Media.Windows.Tests;

public sealed class MediaFoundationEncoderTimestampTrackerTests
{
    [Fact]
    public void Delayed_MFT_output_uses_the_submitted_frame_timestamp_and_metadata()
    {
        var tracker = new MediaFoundationEncoderTimestampTracker();
        var frameN = new MediaFoundationEncoderTiming(
            TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(40), 640, 360);
        var frameNPlusOne = new MediaFoundationEncoderTiming(
            TimeSpan.FromMilliseconds(80), TimeSpan.FromMilliseconds(40), 1280, 720);
        tracker.Submitted(frameN);
        tracker.Submitted(frameNPlusOne);

        var output = tracker.ForOutput(hasMftTimestamp: true, frameN.Timestamp);

        Assert.Equal(frameN, output);
        Assert.Equal(frameNPlusOne, tracker.ForOutput(hasMftTimestamp: false, default));
    }

    [Fact]
    public void Missing_MFT_timestamp_uses_oldest_pending_input_in_FIFO_order()
    {
        var tracker = new MediaFoundationEncoderTimestampTracker();
        var expected = new MediaFoundationEncoderTiming(
            TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(40), 640, 360);
        tracker.Submitted(expected);

        Assert.Equal(expected, tracker.ForOutput(hasMftTimestamp: false, default));
        Assert.Null(tracker.ForOutput(hasMftTimestamp: false, default));
    }

    [Fact]
    public void MFT_timestamp_is_preserved_when_no_exact_submission_match_exists()
    {
        var tracker = new MediaFoundationEncoderTimestampTracker();
        var submitted = new MediaFoundationEncoderTiming(
            TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(40), 640, 360);
        tracker.Submitted(submitted);

        Assert.Equal(submitted with { Timestamp = TimeSpan.FromMilliseconds(41) },
            tracker.ForOutput(hasMftTimestamp: true, TimeSpan.FromMilliseconds(41)));
    }
}
