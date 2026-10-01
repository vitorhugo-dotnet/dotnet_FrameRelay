namespace SonicDesktopRelay.Media.WebSocket;

/// <summary>An independent encoder tap. It owns copied capture frames and never changes the RTC encoder.</summary>
public sealed class ActivityH264Output : IAsyncDisposable
{
    private readonly IScreenCaptureSource capture;
    private readonly IVideoEncoder encoder;
    private readonly MediaSessionClock clock;
    private readonly Func<VideoQuality> quality;
    private readonly Func<bool> enabled;
    private readonly VideoFrameEncodeQueue queue;
    private readonly object gate = new();
    private bool failed, disposed;
    public event Action<EncodedVideoSample>? SampleEncoded;
    public event Action<Exception>? Failed;
    public ActivityH264Output(IScreenCaptureSource capture, IVideoEncoder encoder, MediaSessionClock clock,
        Func<VideoQuality> quality, Func<bool> enabled)
    {
        this.capture = capture; this.encoder = encoder; this.clock = clock; this.quality = quality; this.enabled = enabled;
        queue = new VideoFrameEncodeQueue(Encode); capture.FrameCaptured += OnFrame;
    }
    private void OnFrame(VideoFrame frame)
    {
        if (!failed && !disposed && enabled()) queue.Enqueue(frame); // VideoFrameEncodeQueue copies before returning.
    }
    private void Encode(VideoFrame frame)
    {
        try
        {
            EncodedVideoSample? sample;
            lock (gate)
            {
                if (disposed || failed || !enabled()) return;
                sample = encoder.Encode(new VideoFrame(frame.Width, frame.Height, frame.Bgra, clock.Now), quality());
            }
            if (sample is { } value && enabled()) SampleEncoded?.Invoke(value);
        }
        catch (Exception ex) { failed = true; Failed?.Invoke(ex); }
    }
    public void RequestKeyFrame() { lock (gate) { if (!disposed && !failed) encoder.RequestKeyFrame(); } }
    public async ValueTask DisposeAsync()
    {
        lock (gate) { disposed = true; }
        capture.FrameCaptured -= OnFrame; await queue.DisposeAsync();
        lock (gate) encoder.Dispose();
    }
}
