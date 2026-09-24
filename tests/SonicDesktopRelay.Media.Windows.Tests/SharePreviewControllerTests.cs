using SonicDesktopRelay.App;
using SonicDesktopRelay.Media;

namespace SonicDesktopRelay.Media.Windows.Tests;

public sealed class SharePreviewControllerTests
{
    private static readonly CaptureTarget.Monitor Monitor =
        new(new MonitorInfo("D1", "Display", 1920, 1080, true));
    private static readonly CaptureTarget.Window Window =
        new(new WindowInfo((nint)2, 9, DateTime.UnixEpoch, "Editor", "editor", 800, 600));

    [Fact]
    public async Task Starts_selected_monitor_and_window_with_preview_quality()
    {
        var sources = new List<FakeCapture>();
        await using var preview = new SharePreviewController(_ =>
        {
            var source = new FakeCapture();
            sources.Add(source);
            return source;
        }, action => action());

        await preview.SetTargetAsync(Monitor);
        await preview.SetTargetAsync(Window);

        Assert.Equal(Monitor, sources[0].StartedTarget);
        Assert.Equal(Window, sources[1].StartedTarget);
        Assert.Equal(new VideoQuality(360, 15, 600_000), sources[1].StartedQuality);
        Assert.Equal(1, sources[0].StopCount);
        Assert.Equal(1, sources[0].DisposeCount);
    }

    [Fact]
    public async Task Replaced_source_cannot_publish_stale_frame_or_status()
    {
        var sources = new List<FakeCapture>();
        var queued = new Queue<Action>();
        await using var preview = new SharePreviewController(_ =>
        {
            var source = new FakeCapture();
            sources.Add(source);
            return source;
        }, queued.Enqueue);
        var frames = new List<VideoFrame>();
        preview.FrameCaptured += frames.Add;

        await preview.SetTargetAsync(Monitor);
        sources[0].EmitFrame(1);
        await preview.SetTargetAsync(Window);
        sources[0].EmitFrame(2);
        sources[0].Close("old target closed");
        sources[1].EmitFrame(3);
        while (queued.TryDequeue(out var action)) action();

        Assert.Single(frames);
        Assert.Equal((byte)3, frames[0].Bgra.Span[0]);
        Assert.DoesNotContain("old target closed", preview.PreviewStatus);
    }

    [Fact]
    public async Task Closed_target_stops_capture_and_reports_inline_status()
    {
        var source = new FakeCapture();
        await using var preview = new SharePreviewController(_ => source, action => action());

        await preview.SetTargetAsync(Window);
        source.Close("Window closed");
        await preview.WhenIdleAsync();

        Assert.Contains("Window closed", preview.PreviewStatus);
        Assert.Equal(1, source.DisposeCount);
    }

    [Fact]
    public async Task Startup_failure_reports_status_and_disposes_source()
    {
        var source = new FakeCapture { StartFailure = new InvalidOperationException("Capture denied") };
        await using var preview = new SharePreviewController(_ => source, action => action());

        await preview.SetTargetAsync(Monitor);

        Assert.Contains("Capture denied", preview.PreviewStatus);
        Assert.Equal(1, source.DisposeCount);
    }

    [Fact]
    public async Task Queues_only_one_ui_handoff_and_delivers_latest_pixels()
    {
        var source = new FakeCapture();
        var queued = new Queue<Action>();
        await using var preview = new SharePreviewController(_ => source, queued.Enqueue);
        var frames = new List<VideoFrame>();
        preview.FrameCaptured += frames.Add;
        await preview.SetTargetAsync(Monitor);
        queued.Clear();

        for (byte value = 0; value < 20; value++) source.EmitFrame(value);
        Assert.Single(queued);
        queued.Dequeue()();

        Assert.Single(frames);
        Assert.Equal((byte)19, frames[0].Bgra.Span[0]);
    }

    [Fact]
    public async Task Reduces_large_capture_frame_to_preview_height()
    {
        var source = new FakeCapture();
        await using var preview = new SharePreviewController(_ => source, action => action());
        VideoFrame? delivered = null;
        preview.FrameCaptured += frame => delivered = frame;
        await preview.SetTargetAsync(Monitor);

        source.EmitFrame(new VideoFrame(720, 720, new byte[720 * 720 * 4], TimeSpan.Zero));

        Assert.NotNull(delivered);
        Assert.Equal(360, delivered.Height);
        Assert.Equal(360, delivered.Width);
    }

    [Fact]
    public async Task In_flight_old_close_callback_cannot_stop_new_target()
    {
        var sources = new List<FakeCapture>();
        await using var preview = new SharePreviewController(_ =>
        {
            var source = new FakeCapture();
            sources.Add(source);
            return source;
        }, action => action());
        await preview.SetTargetAsync(Monitor);
        var oldClose = sources[0].CaptureCloseCallback();
        await preview.SetTargetAsync(Window);

        oldClose("stale close");
        await preview.WhenIdleAsync();

        Assert.Equal(0, sources[1].DisposeCount);
        Assert.Equal("Live preview", preview.PreviewStatus);
    }

    private sealed class FakeCapture : IScreenCaptureSource
    {
        public MonitorInfo Monitor => new("fake", "fake", 100, 100, true);
        public event Action<VideoFrame>? FrameCaptured;
        public event Action<string>? TargetClosed;
        public CaptureTarget? StartedTarget { get; private set; }
        public VideoQuality? StartedQuality { get; private set; }
        public Exception? StartFailure { get; init; }
        public int StopCount { get; private set; }
        public int DisposeCount { get; private set; }
        public Task StartAsync(MonitorInfo monitor, VideoQuality quality, CancellationToken ct) =>
            StartAsync(new CaptureTarget.Monitor(monitor), quality, ct);
        public Task StartAsync(CaptureTarget target, VideoQuality quality, CancellationToken ct)
        {
            StartedTarget = target;
            StartedQuality = quality;
            return StartFailure is null ? Task.CompletedTask : Task.FromException(StartFailure);
        }
        public void SetFrameRate(int framesPerSecond) { }
        public Task StopAsync() { StopCount++; return Task.CompletedTask; }
        public ValueTask DisposeAsync() { DisposeCount++; return ValueTask.CompletedTask; }
        public void EmitFrame(byte value) => FrameCaptured?.Invoke(new VideoFrame(1, 1, new byte[] { value, 0, 0, 255 }, TimeSpan.Zero));
        public void EmitFrame(VideoFrame frame) => FrameCaptured?.Invoke(frame);
        public void Close(string reason) => TargetClosed?.Invoke(reason);
        public Action<string> CaptureCloseCallback() => TargetClosed ?? throw new InvalidOperationException();
    }
}
