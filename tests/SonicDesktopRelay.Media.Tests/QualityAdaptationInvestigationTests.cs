using SonicDesktopRelay.Media;
using Xunit;

namespace SonicDesktopRelay.Media.Tests;

public sealed class QualityAdaptationInvestigationTests
{
    private static readonly MonitorInfo Monitor =
        new("display", "Primary", 1920, 1080, true);

    [Fact]
    public async Task One_poor_reception_report_must_not_reduce_resolution()
    {
        var capture = new FakeCapture();
        var encoder = new FakeEncoder();
        await using var pipeline = new ScreenPublishPipeline(capture, encoder);
        await pipeline.StartAsync(Monitor, CancellationToken.None);

        pipeline.ReportPoorReception();

        Assert.Equal(1080, pipeline.Quality.MaxHeight);
    }

    [Fact]
    public async Task Short_burst_must_not_cascade_through_resolution_levels()
    {
        var capture = new FakeCapture();
        var encoder = new FakeEncoder();
        await using var pipeline = new ScreenPublishPipeline(capture, encoder);
        await pipeline.StartAsync(Monitor, CancellationToken.None);

        pipeline.ReportPoorReception();
        pipeline.ReportPoorReception();
        pipeline.ReportPoorReception();

        Assert.Equal(1080, pipeline.Quality.MaxHeight);
    }

    private sealed class FakeCapture : IScreenCaptureSource
    {
        public MonitorInfo Monitor { get; private set; }
        public event Action<VideoFrame>? FrameCaptured { add { } remove { } }

        public Task StartAsync(MonitorInfo monitor, VideoQuality quality, CancellationToken ct)
        {
            Monitor = monitor;
            return Task.CompletedTask;
        }

        public Task StopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeEncoder : IVideoEncoder
    {
        public string Name => "fake";
        public EncodedVideoSample? Encode(VideoFrame frame, VideoQuality quality) => null;
        public void RequestKeyFrame() { }
        public void Dispose() { }
    }
}
