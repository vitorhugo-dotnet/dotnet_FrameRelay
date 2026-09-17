using Xunit;

namespace SonicDesktopRelay.Rtc.Tests;

public sealed class ReceivedAudioTimelineTests
{
    [Fact]
    public void Consecutive_opus_frames_advance_by_their_media_duration()
    {
        var timeline = new ReceivedAudioTimeline();

        var first = timeline.Map(new byte[] { 1, 2 }, durationMilliseconds: 20);
        var second = timeline.Map(new byte[] { 3, 4 }, durationMilliseconds: 20);

        Assert.Equal(TimeSpan.Zero, first.Timestamp);
        Assert.Equal(TimeSpan.FromMilliseconds(20), second.Timestamp);
        Assert.Equal(TimeSpan.FromMilliseconds(20), first.Duration);
        Assert.Equal(960, first.SampleCount);
        Assert.Equal(960, second.SampleCount);
    }

    [Fact]
    public void Timestamp_is_based_on_media_duration_not_payload_size()
    {
        var timeline = new ReceivedAudioTimeline();

        timeline.Map(new byte[8], durationMilliseconds: 20);
        var second = timeline.Map(new byte[200], durationMilliseconds: 40);
        var third = timeline.Map(new byte[1], durationMilliseconds: 20);

        Assert.Equal(TimeSpan.FromMilliseconds(20), second.Timestamp);
        Assert.Equal(TimeSpan.FromMilliseconds(60), third.Timestamp);
        Assert.Equal(1920, second.SampleCount);
    }
}
