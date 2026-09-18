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
    public async Task One_poor_reception_sample_does_not_degrade_the_session_quality()
    {
        var capture = new FakeCapture();
        var encoder = new FakeEncoder();
        await using var pipeline = new ScreenPublishPipeline(capture, encoder);
        await pipeline.StartAsync(Monitor, CancellationToken.None);

        pipeline.ReportReception(0.10);

        Assert.Equal(VideoQuality.Default, pipeline.Quality);
    }

    [Fact]
    public async Task A_real_quality_change_forces_a_clean_encoder_recovery_point()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var capture = new FakeCapture();
        var encoder = new FakeEncoder();
        await using var pipeline = new ScreenPublishPipeline(capture, encoder, time: time);
        await pipeline.StartAsync(Monitor, CancellationToken.None);

        pipeline.ReportReception(0.10);
        time.Advance(TimeSpan.FromSeconds(2.5));
        pipeline.ReportReception(0.10);
        time.Advance(TimeSpan.FromSeconds(2.5));
        pipeline.ReportReception(0.10);

        Assert.Equal(1, encoder.KeyFrameRequests);
        Assert.Equal(3_000_000, pipeline.Quality.TargetBitsPerSecond);
    }

    [Fact]
    public async Task Adaptive_fps_change_updates_capture_rate_without_restarting_capture()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var capture = new FakeCapture();
        var encoder = new FakeEncoder();
        await using var pipeline = new ScreenPublishPipeline(capture, encoder, time: time);
        await pipeline.StartAsync(Monitor, CancellationToken.None);

        void ReportSustainedPoor()
        {
            pipeline.ReportReception(0.10);
            time.Advance(TimeSpan.FromSeconds(2.5));
            pipeline.ReportReception(0.10);
            time.Advance(TimeSpan.FromSeconds(2.5));
            pipeline.ReportReception(0.10);
        }

        ReportSustainedPoor(); // 1080p 4M -> 1080p 3M
        time.Advance(TimeSpan.FromSeconds(15));
        ReportSustainedPoor(); // -> 1080p 2M
        time.Advance(TimeSpan.FromSeconds(15));
        ReportSustainedPoor(); // -> 720p 2M
        time.Advance(TimeSpan.FromSeconds(15));
        ReportSustainedPoor(); // -> 720p 1.5M
        time.Advance(TimeSpan.FromSeconds(15));
        ReportSustainedPoor(); // -> 540p 20 FPS

        Assert.Equal(1, capture.StartCalls);
        Assert.Contains(20, capture.FrameRateUpdates);
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
    public async Task Diagnostics_measure_encode_duration_at_the_existing_pipeline_boundary()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var capture = new FakeCapture();
        var encoder = new FakeEncoder
        {
            DuringEncode = () => time.Advance(TimeSpan.FromMilliseconds(12))
        };
        await using var pipeline = new ScreenPublishPipeline(capture, encoder, time: time);
        await pipeline.StartAsync(Monitor, CancellationToken.None);

        capture.Emit();

        Assert.Equal(TimeSpan.FromMilliseconds(12), pipeline.LastEncodeDuration);
    }

    [Fact]
    public async Task Diagnostics_measure_keyframe_request_to_production_latency()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var capture = new FakeCapture();
        var encoder = new FakeEncoder();
        await using var pipeline = new ScreenPublishPipeline(capture, encoder, time: time);
        await pipeline.StartAsync(Monitor, CancellationToken.None);

        pipeline.RequestKeyFrame(KeyFrameRequestReason.RtcpPli);
        time.Advance(TimeSpan.FromMilliseconds(275));
        capture.Emit();

        Assert.Equal(TimeSpan.FromMilliseconds(275), pipeline.LastKeyFrameRecoveryLatency);
    }

    [Fact]
    public async Task Diagnostics_count_keyframe_requests_sent_to_the_encoder()
    {
        var capture = new FakeCapture();
        var encoder = new FakeEncoder();
        await using var pipeline = new ScreenPublishPipeline(capture, encoder);
        await pipeline.StartAsync(Monitor, CancellationToken.None);

        pipeline.RequestKeyFrame(KeyFrameRequestReason.RtcpPli);
        pipeline.RequestKeyFrame(KeyFrameRequestReason.PacketLoss);

        Assert.Equal(1, pipeline.KeyFrameRequests);
        Assert.Equal(2, pipeline.KeyFrameRequestSignals);
        Assert.Equal(1, pipeline.CoalescedKeyFrameRequests);
        Assert.Equal(1, pipeline.PliReceived);
    }

    private sealed class FakeCapture : IScreenCaptureSource
    {
        public MonitorInfo Monitor { get; private set; }

        public int StartCalls { get; private set; }

        public List<int> FrameRateUpdates { get; } = [];

        public event Action<VideoFrame>? FrameCaptured;

        public Task StartAsync(MonitorInfo monitor, VideoQuality quality, CancellationToken ct)
        {
            Monitor = monitor;
            StartCalls++;
            return Task.CompletedTask;
        }

        public void SetFrameRate(int framesPerSecond) => FrameRateUpdates.Add(framesPerSecond);

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

        public Action? DuringEncode { get; init; }

        public EncodedVideoSample? Encode(VideoFrame frame, VideoQuality quality)
        {
            EncodeCalls++;
            LastFrame = frame;
            DuringEncode?.Invoke();
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
