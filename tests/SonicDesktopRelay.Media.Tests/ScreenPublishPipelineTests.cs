using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace SonicDesktopRelay.Media.Tests;

public sealed class ScreenPublishPipelineTests
{
    private static readonly MonitorInfo Monitor = new("\\\\.\\DISPLAY1", "Primary", 1920, 1080, true);

    [Fact]
    public async Task A_shared_media_clock_stamps_video_at_pipeline_ingress()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var clock = new MediaSessionClock(time);
        var capture = new FakeCapture();
        var encoder = new FakeEncoder();
        await using var pipeline = new ScreenPublishPipeline(capture, encoder, clock);
        await pipeline.StartAsync(Monitor, CancellationToken.None);

        time.Advance(TimeSpan.FromMilliseconds(100));
        capture.Emit();

        Assert.NotNull(encoder.LastFrame);
        Assert.Equal(TimeSpan.FromMilliseconds(100), encoder.LastFrame!.Timestamp);
    }

    [Fact]
    public async Task Each_captured_frame_produces_one_encoded_sample()
    {
        var capture = new FakeCapture();
        var encoder = new FakeEncoder();
        await using var pipeline = new ScreenPublishPipeline(capture, encoder);
        var samples = new List<EncodedVideoSample>();
        pipeline.SampleEncoded += samples.Add;
        await pipeline.StartAsync(Monitor, CancellationToken.None);

        capture.Emit();
        capture.Emit();

        Assert.Equal(2, samples.Count);
    }

    [Fact]
    public async Task One_encode_serves_every_subscriber()
    {
        var capture = new FakeCapture();
        var encoder = new FakeEncoder();
        await using var pipeline = new ScreenPublishPipeline(capture, encoder);
        var first = 0;
        var second = 0;
        var third = 0;
        pipeline.SampleEncoded += _ => first++;
        pipeline.SampleEncoded += _ => second++;
        pipeline.SampleEncoded += _ => third++;
        await pipeline.StartAsync(Monitor, CancellationToken.None);

        capture.Emit();

        // Three viewers, one encode. This is the whole point of the design.
        Assert.Equal(1, encoder.EncodeCalls);
        Assert.Equal(1, first);
        Assert.Equal(1, second);
        Assert.Equal(1, third);
    }

    [Fact]
    public async Task A_frame_the_encoder_swallows_publishes_nothing()
    {
        var capture = new FakeCapture();
        var encoder = new FakeEncoder { ReturnNull = true };
        await using var pipeline = new ScreenPublishPipeline(capture, encoder);
        var samples = 0;
        pipeline.SampleEncoded += _ => samples++;
        await pipeline.StartAsync(Monitor, CancellationToken.None);

        capture.Emit();

        Assert.Equal(0, samples);
    }

    [Fact]
    public async Task A_frame_arriving_before_start_is_ignored()
    {
        var capture = new FakeCapture();
        var encoder = new FakeEncoder();
        await using var pipeline = new ScreenPublishPipeline(capture, encoder);
        var samples = 0;
        pipeline.SampleEncoded += _ => samples++;

        capture.Emit();

        Assert.Equal(0, samples);
        Assert.Equal(0, encoder.EncodeCalls);
    }

    [Fact]
    public async Task Stopping_stops_publishing()
    {
        var capture = new FakeCapture();
        var encoder = new FakeEncoder();
        await using var pipeline = new ScreenPublishPipeline(capture, encoder);
        var samples = 0;
        pipeline.SampleEncoded += _ => samples++;
        await pipeline.StartAsync(Monitor, CancellationToken.None);

        await pipeline.StopAsync();
        capture.Emit();

        Assert.Equal(0, samples);
    }

    [Fact]
    public async Task A_keyframe_request_reaches_the_encoder()
    {
        var capture = new FakeCapture();
        var encoder = new FakeEncoder();
        await using var pipeline = new ScreenPublishPipeline(capture, encoder);
        await pipeline.StartAsync(Monitor, CancellationToken.None);

        pipeline.RequestKeyFrame();

        Assert.Equal(1, encoder.KeyFrameRequests);
    }

    [Fact]
    public async Task Poor_reception_degrades_the_session_quality()
    {
        var capture = new FakeCapture();
        var encoder = new FakeEncoder();
        await using var pipeline = new ScreenPublishPipeline(capture, encoder);
        await pipeline.StartAsync(Monitor, CancellationToken.None);

        pipeline.ReportPoorReception();

        Assert.Equal(720, pipeline.Quality.MaxHeight);
    }

    [Fact]
    public async Task Degrading_also_forces_a_keyframe_so_viewers_resync_at_the_new_size()
    {
        var capture = new FakeCapture();
        var encoder = new FakeEncoder();
        await using var pipeline = new ScreenPublishPipeline(capture, encoder);
        await pipeline.StartAsync(Monitor, CancellationToken.None);

        pipeline.ReportPoorReception();

        Assert.Equal(1, encoder.KeyFrameRequests);
    }

    [Fact]
    public async Task An_encoder_that_throws_stops_the_pipeline_instead_of_spinning()
    {
        var capture = new FakeCapture();
        var encoder = new FakeEncoder { Throw = true };
        await using var pipeline = new ScreenPublishPipeline(capture, encoder);
        Exception? failure = null;
        pipeline.Failed += e => failure = e;
        await pipeline.StartAsync(Monitor, CancellationToken.None);

        capture.Emit();
        capture.Emit();

        Assert.NotNull(failure);
        // One throw is enough; the pipeline must not keep feeding a broken encoder.
        Assert.Equal(1, encoder.EncodeCalls);
    }


    [Fact]
    public async Task Diagnostics_track_capture_encode_keyframes_and_last_activity()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var capture = new FakeCapture();
        var encoder = new FakeEncoder();
        await using var pipeline = new ScreenPublishPipeline(capture, encoder, time: time);
        await pipeline.StartAsync(Monitor, CancellationToken.None);

        capture.Emit();
        time.Advance(TimeSpan.FromMilliseconds(40));
        capture.Emit();

        Assert.Equal(2, pipeline.FramesCaptured);
        Assert.Equal(2, pipeline.EncodedAccessUnits);
        Assert.Equal(2, pipeline.KeyframesProduced);
        Assert.Equal(DateTimeOffset.UnixEpoch + TimeSpan.FromMilliseconds(40), pipeline.LastCapturedFrameAt);
        Assert.Equal(DateTimeOffset.UnixEpoch + TimeSpan.FromMilliseconds(40), pipeline.LastEncodedAccessUnitAt);
        Assert.Equal(8, pipeline.MaximumAccessUnitBytes);
    }

    [Fact]
    public async Task Diagnostics_count_keyframe_requests_sent_to_the_encoder()
    {
        var capture = new FakeCapture();
        var encoder = new FakeEncoder();
        await using var pipeline = new ScreenPublishPipeline(capture, encoder);
        await pipeline.StartAsync(Monitor, CancellationToken.None);

        pipeline.RequestKeyFrame();
        pipeline.ReportPoorReception();

        Assert.Equal(2, pipeline.KeyFrameRequests);
    }

    private sealed class FakeCapture : IScreenCaptureSource
    {
        public MonitorInfo Monitor { get; private set; }

        public event Action<VideoFrame>? FrameCaptured;

        public Task StartAsync(MonitorInfo monitor, VideoQuality quality, CancellationToken ct)
        {
            Monitor = monitor;
            return Task.CompletedTask;
        }

        public Task StopAsync() => Task.CompletedTask;

        public void Emit() => FrameCaptured?.Invoke(
            new VideoFrame(1920, 1080, new byte[16], TimeSpan.Zero));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeEncoder : IVideoEncoder
    {
        public string Name => "fake";

        public int EncodeCalls { get; private set; }

        public int KeyFrameRequests { get; private set; }

        public VideoFrame? LastFrame { get; private set; }

        public bool ReturnNull { get; init; }

        public bool Throw { get; init; }

        public EncodedVideoSample? Encode(VideoFrame frame, VideoQuality quality)
        {
            EncodeCalls++;
            LastFrame = frame;
            if (Throw) throw new InvalidOperationException("encoder failed");
            return ReturnNull
                ? null
                : new EncodedVideoSample(new byte[8], frame.Timestamp, true, frame.Width, frame.Height);
        }

        public void RequestKeyFrame() => KeyFrameRequests++;

        public void Dispose()
        {
        }
    }
}
