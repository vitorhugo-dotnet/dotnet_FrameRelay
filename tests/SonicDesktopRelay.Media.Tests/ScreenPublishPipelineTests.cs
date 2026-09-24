using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace SonicDesktopRelay.Media.Tests;

public sealed class ScreenPublishPipelineTests
{
    private static readonly MonitorInfo Monitor = new("\\\\.\\DISPLAY1", "Primary", 1920, 1080, true);
    private static readonly WindowInfo Window = new(
        (nint)0x1234, 57, DateTime.UnixEpoch, "Editor", "editor.exe", 1280, 720);

    [Fact]
    public async Task Pipeline_starts_capture_from_a_window_target()
    {
        var capture = new FakeCapture();
        await using var pipeline = new ScreenPublishPipeline(capture, new FakeEncoder());
        var target = new CaptureTarget.Window(Window);

        await pipeline.StartAsync(target, CancellationToken.None);

        Assert.Equal(target, capture.StartedTarget);
    }

    [Fact]
    public async Task ReceiverStats_drive_shared_quality_after_sustained_poor_evidence()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var pipeline = new ScreenPublishPipeline(new FakeCapture(), new FakeEncoder(), time: time);
        await pipeline.StartAsync(Monitor, CancellationToken.None);
        var stats = new VideoReceiverStats(1, 2000, 90, 10, 100, 10, 40, 30);

        var viewer = Guid.NewGuid();
        pipeline.ReportReceiverStats(viewer, stats);
        time.Advance(TimeSpan.FromSeconds(2.5));
        pipeline.ReportReceiverStats(viewer, stats);
        time.Advance(TimeSpan.FromSeconds(2.5));
        pipeline.ReportReceiverStats(viewer, stats);

        Assert.Equal(3_000_000, pipeline.Quality.TargetBitsPerSecond);
    }

    [Fact]
    public async Task Expired_receiver_stats_block_quality_recovery()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        await using var pipeline = new ScreenPublishPipeline(new FakeCapture(), new FakeEncoder(), time: time);
        await pipeline.StartAsync(Monitor, CancellationToken.None);
        var viewer = Guid.NewGuid();
        var poor = new VideoReceiverStats(1, 2000, 90, 10, 100, 10, 40, 30);
        pipeline.ReportReceiverStats(viewer, poor);
        time.Advance(TimeSpan.FromSeconds(2.5));
        pipeline.ReportReceiverStats(viewer, poor);
        time.Advance(TimeSpan.FromSeconds(2.5));
        pipeline.ReportReceiverStats(viewer, poor);
        Assert.Equal(3_000_000, pipeline.Quality.TargetBitsPerSecond);

        time.Advance(TimeSpan.FromSeconds(10.1));
        for (var i = 0; i < 4; i++)
        {
            pipeline.ReportReception(viewer, 0);
            time.Advance(TimeSpan.FromSeconds(10));
        }

        Assert.Equal(3_000_000, pipeline.Quality.TargetBitsPerSecond);

        var healthy = new VideoReceiverStats(1, 2000, 100, 0, 100, 0, 60, 30);
        pipeline.ReportReceiverStats(viewer, healthy);
        Assert.Equal(3_000_000, pipeline.Quality.TargetBitsPerSecond);

        for (var i = 0; i < 3; i++)
        {
            time.Advance(TimeSpan.FromSeconds(10));
            pipeline.ReportReceiverStats(viewer, healthy);
        }
        Assert.Equal(4_000_000, pipeline.Quality.TargetBitsPerSecond);
    }

    [Fact]
    public async Task One_low_fps_interval_followed_by_healthy_reports_does_not_combine_with_later_rtcp_loss()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        await using var pipeline = new ScreenPublishPipeline(new FakeCapture(), new FakeEncoder(), time: time);
        await pipeline.StartAsync(Monitor, CancellationToken.None);
        var viewer = Guid.NewGuid();
        var lowFps = new VideoReceiverStats(1, 2000, 0, 0, 100, 0, 20, 30);
        pipeline.ReportReceiverStats(viewer, lowFps);

        time.Advance(TimeSpan.FromSeconds(2));
        pipeline.ReportReception(viewer, 0);
        time.Advance(TimeSpan.FromSeconds(2));
        pipeline.ReportReception(viewer, 0);
        time.Advance(TimeSpan.FromSeconds(2));
        pipeline.ReportReception(viewer, 0);
        Assert.Equal(VideoQuality.Default, pipeline.Quality);

        var healthy = new VideoReceiverStats(1, 2000, 100, 0, 100, 0, 60, 30);
        pipeline.ReportReceiverStats(viewer, healthy);
        time.Advance(TimeSpan.FromSeconds(2));
        pipeline.ReportReception(viewer, 0.1);
        time.Advance(TimeSpan.FromSeconds(2));
        pipeline.ReportReception(viewer, 0.1);
        time.Advance(TimeSpan.FromSeconds(2));
        pipeline.ReportReception(viewer, 0.1);

        Assert.Equal(VideoQuality.Default, pipeline.Quality);
    }

    [Fact]
    public async Task Low_fps_spanning_five_seconds_degrades_quality()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        await using var pipeline = new ScreenPublishPipeline(new FakeCapture(), new FakeEncoder(), time: time);
        await pipeline.StartAsync(Monitor, CancellationToken.None);
        var viewer = Guid.NewGuid();
        var lowFps = new VideoReceiverStats(1, 2000, 100, 0, 100, 0, 20, 30);
        pipeline.ReportReceiverStats(viewer, lowFps);
        time.Advance(TimeSpan.FromSeconds(2.5));
        pipeline.ReportReceiverStats(viewer, lowFps);
        time.Advance(TimeSpan.FromSeconds(2.5));
        pipeline.ReportReceiverStats(viewer, lowFps);

        Assert.Equal(3_000_000, pipeline.Quality.TargetBitsPerSecond);
    }

    [Fact]
    public async Task Recovery_requires_healthy_telemetry_from_every_viewer_for_thirty_seconds_and_respects_profile_ceiling()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var profile = new VideoPublishProfile(MaxHeight: 720, MaxFramesPerSecond: 30);
        await using var pipeline = new ScreenPublishPipeline(new FakeCapture(), new FakeEncoder(), time: time, profile: profile);
        await pipeline.StartAsync(Monitor, CancellationToken.None);
        var poorViewer = Guid.NewGuid();
        var otherViewer = Guid.NewGuid();
        pipeline.ReportReception(otherViewer, 0);
        pipeline.ReportReception(poorViewer, 0.1);
        time.Advance(TimeSpan.FromSeconds(2.5));
        pipeline.ReportReception(otherViewer, 0);
        pipeline.ReportReception(poorViewer, 0.1);
        time.Advance(TimeSpan.FromSeconds(2.5));
        pipeline.ReportReception(otherViewer, 0);
        pipeline.ReportReception(poorViewer, 0.1);
        Assert.Equal(1_500_000, pipeline.Quality.TargetBitsPerSecond);

        var healthy = new VideoReceiverStats(1, 2000, 100, 0, 100, 0, 60, 30);
        for (var i = 0; i < 4; i++)
        {
            pipeline.ReportReceiverStats(poorViewer, healthy);
            pipeline.ReportReceiverStats(otherViewer, healthy);
            if (i < 3) time.Advance(TimeSpan.FromSeconds(10));
        }

        Assert.Equal(VideoQuality.InitialFor(profile), pipeline.Quality);
        Assert.Equal(720, pipeline.Quality.MaxHeight);
    }

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

        Assert.True(SpinWait.SpinUntil(() => encoder.LastFrame is not null, TimeSpan.FromSeconds(1)));
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
        Assert.True(SpinWait.SpinUntil(() => encoder.EncodeCalls == 1, TimeSpan.FromSeconds(1)));
        capture.Emit();

        Assert.True(SpinWait.SpinUntil(() => encoder.EncodeCalls == 2, TimeSpan.FromSeconds(1)));
        Assert.Equal(2, samples.Count);
    }

    [Fact]
    public async Task Capture_returns_while_encoder_is_busy_and_encodes_the_latest_pending_frame()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var capture = new FakeCapture();
        var encoder = new FakeEncoder
        {
            DuringEncode = () =>
            {
                entered.SetResult();
                release.Wait();
            }
        };
        await using var pipeline = new ScreenPublishPipeline(capture, encoder);
        await pipeline.StartAsync(Monitor, CancellationToken.None);

        capture.Emit(TimeSpan.FromMilliseconds(1));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var captures = Task.Run(() => capture.Emit(TimeSpan.FromMilliseconds(2)));

        var completed = await Task.WhenAny(captures, Task.Delay(TimeSpan.FromSeconds(1)));
        Assert.Same(captures, completed);

        release.Set();
        await captures;

        Assert.True(SpinWait.SpinUntil(() => encoder.EncodeCalls >= 2, TimeSpan.FromSeconds(1)));
        Assert.Equal(
            [TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(2)],
            encoder.EncodedTimestamps);
    }

    [Fact]
    public async Task Capture_frame_ownership_survives_source_buffer_reuse()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var capture = new FakeCapture();
        var encoder = new FakeEncoder
        {
            DuringEncode = () =>
            {
                entered.SetResult();
                release.Wait();
            }
        };
        await using var pipeline = new ScreenPublishPipeline(capture, encoder);
        await pipeline.StartAsync(Monitor, CancellationToken.None);

        var sourceBuffer = new byte[] { 7, 8, 9, 10 };
        capture.Emit(new VideoFrame(2, 2, sourceBuffer, TimeSpan.Zero));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        sourceBuffer[0] = 99;
        release.Set();

        Assert.True(SpinWait.SpinUntil(() => encoder.FirstByteValues.Count == 1, TimeSpan.FromSeconds(1)));
        Assert.Equal(7, encoder.FirstByteValues[0]);
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
        pipeline.SampleEncoded += _ => Interlocked.Increment(ref first);
        pipeline.SampleEncoded += _ => Interlocked.Increment(ref second);
        pipeline.SampleEncoded += _ => Interlocked.Increment(ref third);
        await pipeline.StartAsync(Monitor, CancellationToken.None);

        capture.Emit();

        Assert.True(SpinWait.SpinUntil(() => encoder.EncodeCalls == 1, TimeSpan.FromSeconds(1)));
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref third) == 1, TimeSpan.FromSeconds(1)));
        // Three viewers, one encode. This is the whole point of the design.
        Assert.Equal(1, encoder.EncodeCalls);
        Assert.Equal(1, Volatile.Read(ref first));
        Assert.Equal(1, Volatile.Read(ref second));
        Assert.Equal(1, Volatile.Read(ref third));
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

        Assert.True(SpinWait.SpinUntil(() => encoder.EncodeCalls == 1, TimeSpan.FromSeconds(1)));
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

        Assert.True(SpinWait.SpinUntil(() => failure is not null, TimeSpan.FromSeconds(1)));
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
        Assert.True(SpinWait.SpinUntil(() => pipeline.EncodedAccessUnits == 1, TimeSpan.FromSeconds(1)));
        capture.Emit();

        Assert.True(SpinWait.SpinUntil(() => pipeline.EncodedAccessUnits == 2, TimeSpan.FromSeconds(1)));
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

        Assert.True(SpinWait.SpinUntil(() => pipeline.LastEncodeDuration is not null, TimeSpan.FromSeconds(1)));
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

        Assert.True(SpinWait.SpinUntil(() => pipeline.KeyframesProduced == 1, TimeSpan.FromSeconds(1)));
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

        public CaptureTarget? StartedTarget { get; private set; }

        public int StartCalls { get; private set; }

        public List<int> FrameRateUpdates { get; } = [];

        public event Action<VideoFrame>? FrameCaptured;

        public Task StartAsync(MonitorInfo monitor, VideoQuality quality, CancellationToken ct)
        {
            Monitor = monitor;
            StartedTarget = new CaptureTarget.Monitor(monitor);
            StartCalls++;
            return Task.CompletedTask;
        }

        public Task StartAsync(CaptureTarget target, VideoQuality quality, CancellationToken ct)
        {
            StartedTarget = target;
            if (target is CaptureTarget.Monitor monitor) Monitor = monitor.Info;
            StartCalls++;
            return Task.CompletedTask;
        }

        public void SetFrameRate(int framesPerSecond) => FrameRateUpdates.Add(framesPerSecond);

        public Task StopAsync() => Task.CompletedTask;

        public void Emit() => Emit(TimeSpan.Zero);

        public void Emit(TimeSpan timestamp) => FrameCaptured?.Invoke(
            new VideoFrame(1920, 1080, new byte[16], timestamp));

        public void Emit(VideoFrame frame) => FrameCaptured?.Invoke(frame);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeEncoder : IVideoEncoder
    {
        public string Name => "fake";

        private int _encodeCalls;

        public int EncodeCalls => Volatile.Read(ref _encodeCalls);

        public int KeyFrameRequests { get; private set; }

        public VideoFrame? LastFrame { get; private set; }

        public List<TimeSpan> EncodedTimestamps { get; } = [];

        public List<byte> FirstByteValues { get; } = [];

        public bool ReturnNull { get; init; }

        public bool Throw { get; init; }

        public Action? DuringEncode { get; init; }

        public EncodedVideoSample? Encode(VideoFrame frame, VideoQuality quality)
        {
            Interlocked.Increment(ref _encodeCalls);
            LastFrame = frame;
            EncodedTimestamps.Add(frame.Timestamp);
            FirstByteValues.Add(frame.Bgra.Span[0]);
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
