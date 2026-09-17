namespace SonicDesktopRelay.Media;

/// <summary>
/// capture → encode → one event, for the whole session. Every viewer subscribes to the same
/// <see cref="SampleEncoded"/>, so adding the fourth viewer costs a subscription rather than
/// a fourth encoder.
/// </summary>
public sealed class ScreenPublishPipeline(IScreenCaptureSource capture, IVideoEncoder encoder)
    : IAsyncDisposable
{
    private bool _running;

    public event Action<EncodedVideoSample>? SampleEncoded;

    public event Action<Exception>? Failed;

    public VideoQuality Quality { get; private set; } = VideoQuality.Default;

    public string EncoderName => encoder.Name;

    public async Task StartAsync(MonitorInfo monitor, CancellationToken ct)
    {
        if (_running) return;
        capture.FrameCaptured += OnFrame;
        await capture.StartAsync(monitor, Quality, ct);
        _running = true;
    }

    public async Task StopAsync()
    {
        if (!_running) return;
        _running = false;
        capture.FrameCaptured -= OnFrame;
        await capture.StopAsync();
    }

    public void RequestKeyFrame() => encoder.RequestKeyFrame();

    /// <summary>
    /// Called when any viewer's RTCP shows sustained loss. Quality is global, so the worst
    /// connection sets it for everyone — the alternative is a second encode per viewer.
    /// </summary>
    public void ReportPoorReception()
    {
        // Loss means at least one decoder may have fallen out of sync. Request recovery even
        // when quality is already at its floor.
        encoder.RequestKeyFrame();

        var reduced = Quality.Reduced();
        if (reduced == Quality) return;
        Quality = reduced;
    }

    private void OnFrame(VideoFrame frame)
    {
        if (!_running) return;

        EncodedVideoSample? sample;
        try
        {
            sample = encoder.Encode(frame, Quality);
        }
        catch (Exception e)
        {
            // Stop before reporting: a failing encoder called once per frame at 30 Hz turns
            // one fault into a flood, and the session is over either way.
            _running = false;
            capture.FrameCaptured -= OnFrame;
            Failed?.Invoke(e);
            return;
        }

        if (sample is not null) SampleEncoded?.Invoke(sample.Value);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        await capture.DisposeAsync();
        encoder.Dispose();
    }
}
