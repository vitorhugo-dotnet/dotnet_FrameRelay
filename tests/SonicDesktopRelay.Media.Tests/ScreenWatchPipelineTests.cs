using Microsoft.Extensions.Time.Testing;
using SonicDesktopRelay.Media;
using Xunit;

namespace SonicDesktopRelay.Media.Tests;

public sealed class ScreenWatchPipelineTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_fresh_pipeline_is_waiting_for_the_first_frame()
    {
        using var pipeline = new ScreenWatchPipeline(new FakeDecoder(), new FakeTimeProvider(Start));

        Assert.Equal(WatchState.Waiting, pipeline.State);
    }

    [Fact]
    public void A_decoded_sample_is_published_and_moves_the_state_to_receiving()
    {
        var time = new FakeTimeProvider(Start);
        using var pipeline = new ScreenWatchPipeline(new FakeDecoder(), time);
        var frames = new List<VideoFrame>();
        pipeline.FrameDecoded += frames.Add;

        pipeline.Submit(Sample());

        Assert.Single(frames);
        Assert.Equal(WatchState.Receiving, pipeline.State);
    }

    [Fact]
    public void A_sample_the_decoder_swallows_publishes_nothing()
    {
        using var pipeline = new ScreenWatchPipeline(new FakeDecoder { ReturnNull = true },
            new FakeTimeProvider(Start));
        var frames = 0;
        pipeline.FrameDecoded += _ => frames++;

        pipeline.Submit(Sample());

        Assert.Equal(0, frames);
        // Still waiting: a decoder buffering its first frames has not failed.
        Assert.Equal(WatchState.Waiting, pipeline.State);
    }

    [Fact]
    public void A_dropped_decode_after_receiving_asks_for_one_recovery_keyframe()
    {
        var decoder = new FakeDecoder();
        using var pipeline = new ScreenWatchPipeline(decoder, new FakeTimeProvider(Start));
        var requests = 0;
        pipeline.KeyFrameNeeded += () => requests++;
        pipeline.Submit(Sample());

        decoder.ReturnNull = true;
        pipeline.Submit(Sample());
        pipeline.Submit(Sample());

        Assert.Equal(1, requests);
        Assert.Equal(WatchState.Receiving, pipeline.State);
    }

    [Fact]
    public void A_good_frame_rearms_decode_loss_recovery()
    {
        var decoder = new FakeDecoder();
        using var pipeline = new ScreenWatchPipeline(decoder, new FakeTimeProvider(Start));
        var requests = 0;
        pipeline.KeyFrameNeeded += () => requests++;
        pipeline.Submit(Sample());

        decoder.ReturnNull = true;
        pipeline.Submit(Sample());
        decoder.ReturnNull = false;
        pipeline.Submit(Sample());
        decoder.ReturnNull = true;
        pipeline.Submit(Sample());

        Assert.Equal(2, requests);
    }

    [Fact]
    public void No_frame_for_five_seconds_is_reported_as_stalled_not_as_disconnected()
    {
        var time = new FakeTimeProvider(Start);
        using var pipeline = new ScreenWatchPipeline(new FakeDecoder(), time);
        pipeline.Submit(Sample());

        time.Advance(TimeSpan.FromSeconds(5));
        pipeline.CheckForStall();

        // The peer connection can be perfectly healthy while the media has stopped. Calling
        // that "disconnected" sends the user to debug the wrong thing.
        Assert.Equal(WatchState.Stalled, pipeline.State);
    }

    [Fact]
    public void A_stall_asks_the_publisher_for_a_keyframe()
    {
        var time = new FakeTimeProvider(Start);
        using var pipeline = new ScreenWatchPipeline(new FakeDecoder(), time);
        var requests = 0;
        pipeline.KeyFrameNeeded += () => requests++;
        pipeline.Submit(Sample());

        time.Advance(TimeSpan.FromSeconds(5));
        pipeline.CheckForStall();

        Assert.Equal(1, requests);
    }

    [Fact]
    public void A_frame_arriving_after_a_stall_returns_to_receiving()
    {
        var time = new FakeTimeProvider(Start);
        using var pipeline = new ScreenWatchPipeline(new FakeDecoder(), time);
        pipeline.Submit(Sample());
        time.Advance(TimeSpan.FromSeconds(5));
        pipeline.CheckForStall();

        pipeline.Submit(Sample());

        Assert.Equal(WatchState.Receiving, pipeline.State);
    }

    [Fact]
    public void A_brief_gap_is_not_a_stall()
    {
        var time = new FakeTimeProvider(Start);
        using var pipeline = new ScreenWatchPipeline(new FakeDecoder(), time);
        pipeline.Submit(Sample());

        time.Advance(TimeSpan.FromSeconds(2));
        pipeline.CheckForStall();

        Assert.Equal(WatchState.Receiving, pipeline.State);
    }

    [Fact]
    public void Repeated_stall_checks_ask_for_only_one_keyframe()
    {
        var time = new FakeTimeProvider(Start);
        using var pipeline = new ScreenWatchPipeline(new FakeDecoder(), time);
        var requests = 0;
        pipeline.KeyFrameNeeded += () => requests++;
        pipeline.Submit(Sample());
        time.Advance(TimeSpan.FromSeconds(5));

        pipeline.CheckForStall();
        pipeline.CheckForStall();
        pipeline.CheckForStall();

        // Asking once per tick would flood the publisher with PLIs precisely when the link
        // is already struggling.
        Assert.Equal(1, requests);
    }

    [Fact]
    public void A_decoder_that_throws_fails_the_pipeline_once()
    {
        using var pipeline = new ScreenWatchPipeline(new FakeDecoder { Throw = true },
            new FakeTimeProvider(Start));
        var decoder = 0;
        pipeline.StateChanged += s => { if (s == WatchState.Failed) decoder++; };

        pipeline.Submit(Sample());
        pipeline.Submit(Sample());

        Assert.Equal(WatchState.Failed, pipeline.State);
        Assert.Equal(1, decoder);
    }

    [Fact]
    public void The_decoder_name_is_exposed_for_diagnostics()
    {
        using var pipeline = new ScreenWatchPipeline(new FakeDecoder(), new FakeTimeProvider(Start));

        Assert.Equal("fake", pipeline.DecoderName);
    }

    [Fact]
    public void Diagnostics_count_received_access_units_even_when_decoder_produces_no_frame()
    {
        var time = new FakeTimeProvider(Start);
        using var pipeline = new ScreenWatchPipeline(
            new FakeDecoder { ReturnNull = true },
            time);

        pipeline.Submit(Sample());

        Assert.Equal(1, pipeline.VideoAccessUnitsReceived);
        Assert.Equal(0, pipeline.DecodedFrames);
        Assert.Null(pipeline.LastDecodedFrameAt);
    }

    [Fact]
    public void Diagnostics_record_decoded_frames_and_last_frame_time()
    {
        var time = new FakeTimeProvider(Start);
        using var pipeline = new ScreenWatchPipeline(new FakeDecoder(), time);

        pipeline.Submit(Sample());

        Assert.Equal(1, pipeline.VideoAccessUnitsReceived);
        Assert.Equal(1, pipeline.DecodedFrames);
        Assert.Equal(Start, pipeline.LastDecodedFrameAt);
    }

    private static EncodedVideoSample Sample() =>
        new(new byte[8], TimeSpan.Zero, true, 1920, 1080);

    private sealed class FakeDecoder : IVideoDecoder
    {
        public string Name => "fake";

        public bool ReturnNull { get; set; }

        public bool Throw { get; init; }

        public VideoFrame? Decode(EncodedVideoSample sample)
        {
            if (Throw) throw new InvalidOperationException("decoder failed");
            return ReturnNull ? null : new VideoFrame(sample.Width, sample.Height, new byte[16], sample.Timestamp);
        }

        public void Dispose()
        {
        }
    }
}
