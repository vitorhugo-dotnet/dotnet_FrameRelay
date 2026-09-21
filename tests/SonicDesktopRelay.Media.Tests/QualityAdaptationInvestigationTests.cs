using Microsoft.Extensions.Time.Testing;
using SonicDesktopRelay.Media;
using Xunit;

namespace SonicDesktopRelay.Media.Tests;

public sealed class QualityAdaptationTests
{
    private static readonly MonitorInfo Monitor =
        new("display", "Primary", 1920, 1080, true);

    [Fact]
    public void A_720p_profile_starts_at_the_highest_allowed_720p_rung()
    {
        var profile = new VideoPublishProfile(MaxHeight: 720, MaxFramesPerSecond: 30);

        var quality = VideoQuality.InitialFor(profile);

        Assert.Equal(720, quality.MaxHeight);
        Assert.Equal(30, quality.FramesPerSecond);
        Assert.Equal(2_000_000, quality.TargetBitsPerSecond);
    }

    [Fact]
    public async Task Stable_recovery_never_exceeds_the_user_profile_ceiling()
    {
        var profile = new VideoPublishProfile(MaxHeight: 720, MaxFramesPerSecond: 30);
        var harness = await Harness.CreateAsync(profile);

        ReportSustainedPoor(harness); // 720p 2M -> 720p 1.5M

        harness.Time.Advance(TimeSpan.FromSeconds(15));
        harness.Pipeline.ReportReception(0);
        harness.Time.Advance(TimeSpan.FromSeconds(15));
        harness.Pipeline.ReportReception(0);
        harness.Time.Advance(TimeSpan.FromSeconds(15));
        harness.Pipeline.ReportReception(0);

        Assert.Equal(720, harness.Pipeline.Quality.MaxHeight);
        Assert.Equal(30, harness.Pipeline.Quality.FramesPerSecond);
        Assert.Equal(2_000_000, harness.Pipeline.Quality.TargetBitsPerSecond);
        await harness.DisposeAsync();
    }

    [Fact]
    public async Task One_poor_reception_report_does_not_reduce_resolution()
    {
        var harness = await Harness.CreateAsync();

        harness.Pipeline.ReportReception(0.10);

        Assert.Equal(1080, harness.Pipeline.Quality.MaxHeight);
        Assert.Equal(4_000_000, harness.Pipeline.Quality.TargetBitsPerSecond);
        await harness.DisposeAsync();
    }

    [Fact]
    public async Task Short_burst_cannot_cascade_resolution_levels()
    {
        var harness = await Harness.CreateAsync();

        harness.Pipeline.ReportReception(0.10);
        harness.Pipeline.ReportReception(0.10);
        harness.Pipeline.ReportReception(0.10);

        Assert.Equal(1080, harness.Pipeline.Quality.MaxHeight);
        Assert.Equal(4_000_000, harness.Pipeline.Quality.TargetBitsPerSecond);
        await harness.DisposeAsync();
    }

    [Fact]
    public async Task Sustained_poor_reception_reduces_bitrate_before_resolution()
    {
        var harness = await Harness.CreateAsync();

        ReportSustainedPoor(harness);

        Assert.Equal(1080, harness.Pipeline.Quality.MaxHeight);
        Assert.Equal(3_000_000, harness.Pipeline.Quality.TargetBitsPerSecond);
        await harness.DisposeAsync();
    }

    [Fact]
    public async Task Continued_sustained_loss_eventually_reduces_resolution_one_step_at_a_time()
    {
        var harness = await Harness.CreateAsync();

        ReportSustainedPoor(harness); // 1080p 4M -> 1080p 3M
        AdvancePastCooldownAndReportPoor(harness); // -> 1080p 2M
        AdvancePastCooldownAndReportPoor(harness); // -> 720p 2M

        Assert.Equal(720, harness.Pipeline.Quality.MaxHeight);
        Assert.Equal(2_000_000, harness.Pipeline.Quality.TargetBitsPerSecond);
        await harness.DisposeAsync();
    }

    [Fact]
    public async Task Quality_changes_respect_cooldown()
    {
        var harness = await Harness.CreateAsync();
        ReportSustainedPoor(harness);

        // Enough reports and duration, but still inside the 15 second post-change cooldown.
        harness.Pipeline.ReportReception(0.10);
        harness.Time.Advance(TimeSpan.FromSeconds(2.5));
        harness.Pipeline.ReportReception(0.10);
        harness.Time.Advance(TimeSpan.FromSeconds(2.5));
        harness.Pipeline.ReportReception(0.10);

        Assert.Equal(1080, harness.Pipeline.Quality.MaxHeight);
        Assert.Equal(3_000_000, harness.Pipeline.Quality.TargetBitsPerSecond);
        await harness.DisposeAsync();
    }

    [Fact]
    public async Task Stable_reception_gradually_recovers_toward_default_quality()
    {
        var harness = await Harness.CreateAsync();
        ReportSustainedPoor(harness);
        Assert.Equal(3_000_000, harness.Pipeline.Quality.TargetBitsPerSecond);

        harness.Time.Advance(TimeSpan.FromSeconds(15));
        harness.Pipeline.ReportReception(0);
        harness.Time.Advance(TimeSpan.FromSeconds(15));
        harness.Pipeline.ReportReception(0);
        harness.Time.Advance(TimeSpan.FromSeconds(15));
        harness.Pipeline.ReportReception(0);

        Assert.Equal(VideoQuality.Default, harness.Pipeline.Quality);
        await harness.DisposeAsync();
    }

    [Fact]
    public async Task Stable_reports_from_one_viewer_do_not_recover_quality_for_an_unrecovered_viewer()
    {
        var harness = await Harness.CreateAsync();
        var degradedViewer = Guid.Parse("6f9619ff-8b86-d011-b42d-00cf4fc96401");
        var stableViewer = Guid.Parse("6f9619ff-8b86-d011-b42d-00cf4fc96402");

        harness.Pipeline.ReportReception(degradedViewer, 0.10);
        harness.Time.Advance(TimeSpan.FromSeconds(2.5));
        harness.Pipeline.ReportReception(degradedViewer, 0.10);
        harness.Time.Advance(TimeSpan.FromSeconds(2.5));
        harness.Pipeline.ReportReception(degradedViewer, 0.10);
        Assert.Equal(3_000_000, harness.Pipeline.Quality.TargetBitsPerSecond);

        harness.Time.Advance(TimeSpan.FromSeconds(15));
        harness.Pipeline.ReportReception(stableViewer, 0);
        harness.Time.Advance(TimeSpan.FromSeconds(15));
        harness.Pipeline.ReportReception(stableViewer, 0);
        harness.Time.Advance(TimeSpan.FromSeconds(15));
        harness.Pipeline.ReportReception(stableViewer, 0);

        Assert.Equal(1080, harness.Pipeline.Quality.MaxHeight);
        Assert.Equal(3_000_000, harness.Pipeline.Quality.TargetBitsPerSecond);
        await harness.DisposeAsync();
    }

    [Fact]
    public async Task Recovery_does_not_immediately_oscillate_back_down()
    {
        var harness = await Harness.CreateAsync();
        ReportSustainedPoor(harness);

        harness.Time.Advance(TimeSpan.FromSeconds(15));
        harness.Pipeline.ReportReception(0);
        harness.Time.Advance(TimeSpan.FromSeconds(15));
        harness.Pipeline.ReportReception(0);
        harness.Time.Advance(TimeSpan.FromSeconds(15));
        harness.Pipeline.ReportReception(0);
        Assert.Equal(VideoQuality.Default, harness.Pipeline.Quality);

        harness.Pipeline.ReportReception(0.10);
        harness.Time.Advance(TimeSpan.FromSeconds(2.5));
        harness.Pipeline.ReportReception(0.10);
        harness.Time.Advance(TimeSpan.FromSeconds(2.5));
        harness.Pipeline.ReportReception(0.10);

        Assert.Equal(VideoQuality.Default, harness.Pipeline.Quality);
        await harness.DisposeAsync();
    }

    [Fact]
    public async Task Pli_recovery_by_itself_never_changes_quality()
    {
        var harness = await Harness.CreateAsync();

        harness.Pipeline.RequestKeyFrame(KeyFrameRequestReason.RtcpPli);
        harness.Pipeline.RequestKeyFrame(KeyFrameRequestReason.RtcpPli);

        Assert.Equal(VideoQuality.Default, harness.Pipeline.Quality);
        Assert.Equal(1, harness.Encoder.KeyFrameRequests);
        Assert.Equal(1, harness.Pipeline.CoalescedKeyFrameRequests);
        Assert.Equal(2, harness.Pipeline.PliReceived);
        await harness.DisposeAsync();
    }

    [Fact]
    public async Task Produced_keyframe_rearms_coalesced_recovery_requests()
    {
        var harness = await Harness.CreateAsync();

        harness.Pipeline.RequestKeyFrame(KeyFrameRequestReason.RtcpPli);
        harness.Pipeline.RequestKeyFrame(KeyFrameRequestReason.PacketLoss);
        Assert.Equal(1, harness.Encoder.KeyFrameRequests);

        harness.Capture.EmitKeyFrame();
        Assert.True(SpinWait.SpinUntil(() => harness.Pipeline.KeyframesProduced == 1, TimeSpan.FromSeconds(1)));
        harness.Pipeline.RequestKeyFrame(KeyFrameRequestReason.RtcpPli);

        Assert.Equal(2, harness.Encoder.KeyFrameRequests);
        await harness.DisposeAsync();
    }

    private static void ReportSustainedPoor(Harness harness)
    {
        harness.Pipeline.ReportReception(0.10);
        harness.Time.Advance(TimeSpan.FromSeconds(2.5));
        harness.Pipeline.ReportReception(0.10);
        harness.Time.Advance(TimeSpan.FromSeconds(2.5));
        harness.Pipeline.ReportReception(0.10);
    }

    private static void AdvancePastCooldownAndReportPoor(Harness harness)
    {
        harness.Time.Advance(TimeSpan.FromSeconds(15));
        ReportSustainedPoor(harness);
    }

    private sealed class Harness(
        FakeTimeProvider time,
        FakeCapture capture,
        FakeEncoder encoder,
        ScreenPublishPipeline pipeline) : IAsyncDisposable
    {
        public FakeTimeProvider Time { get; } = time;
        public FakeCapture Capture { get; } = capture;
        public FakeEncoder Encoder { get; } = encoder;
        public ScreenPublishPipeline Pipeline { get; } = pipeline;

        public static async Task<Harness> CreateAsync(VideoPublishProfile? profile = null)
        {
            var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
            var capture = new FakeCapture();
            var encoder = new FakeEncoder();
            var pipeline = new ScreenPublishPipeline(
                capture,
                encoder,
                time: time,
                profile: profile ?? VideoPublishProfile.Default);
            await pipeline.StartAsync(Monitor, CancellationToken.None);
            return new Harness(time, capture, encoder, pipeline);
        }

        public ValueTask DisposeAsync() => Pipeline.DisposeAsync();
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

        public void SetFrameRate(int framesPerSecond) { }

        public Task StopAsync() => Task.CompletedTask;

        public void EmitKeyFrame() =>
            FrameCaptured?.Invoke(new VideoFrame(1920, 1080, new byte[16], TimeSpan.Zero));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeEncoder : IVideoEncoder
    {
        public string Name => "fake";
        public int KeyFrameRequests { get; private set; }

        public EncodedVideoSample? Encode(VideoFrame frame, VideoQuality quality) =>
            new(new byte[8], frame.Timestamp, IsKeyFrame: true, frame.Width, frame.Height);

        public void RequestKeyFrame() => KeyFrameRequests++;
        public void Dispose() { }
    }
}
