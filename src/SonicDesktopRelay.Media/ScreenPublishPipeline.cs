using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SonicDesktopRelay.Media;

/// <summary>
/// capture → encode → one event, for the whole session. Every viewer subscribes to the same
/// <see cref="SampleEncoded"/>, so adding the fourth viewer costs a subscription rather than
/// a fourth encoder.
/// </summary>
public sealed class ScreenPublishPipeline(
    IScreenCaptureSource capture,
    IVideoEncoder encoder,
    MediaSessionClock? clock = null,
    TimeProvider? time = null,
    ILogger<ScreenPublishPipeline>? logger = null) : IAsyncDisposable
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ILogger<ScreenPublishPipeline> _logger =
        logger ?? NullLogger<ScreenPublishPipeline>.Instance;

    private bool _running;
    private long _framesCaptured;
    private long _encodedAccessUnits;
    private long _keyframesProduced;
    private long _keyFrameRequests;
    private long _maximumAccessUnitBytes;
    private long _lastCapturedUtcTicks;
    private long _lastEncodedUtcTicks;

    public event Action<EncodedVideoSample>? SampleEncoded;

    public event Action<Exception>? Failed;

    public VideoQuality Quality { get; private set; } = VideoQuality.Default;

    public string EncoderName => encoder.Name;

    public long FramesCaptured => Interlocked.Read(ref _framesCaptured);

    public long EncodedAccessUnits => Interlocked.Read(ref _encodedAccessUnits);

    public long KeyframesProduced => Interlocked.Read(ref _keyframesProduced);

    public long KeyFrameRequests => Interlocked.Read(ref _keyFrameRequests);

    public long MaximumAccessUnitBytes => Interlocked.Read(ref _maximumAccessUnitBytes);

    public DateTimeOffset? LastCapturedFrameAt => ReadTimestamp(ref _lastCapturedUtcTicks);

    public DateTimeOffset? LastEncodedAccessUnitAt => ReadTimestamp(ref _lastEncodedUtcTicks);

    public string? LastFailure { get; private set; }

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

    public void RequestKeyFrame()
    {
        Interlocked.Increment(ref _keyFrameRequests);
        _logger.LogInformation(
            "Publisher keyframe requested. requests={KeyFrameRequests} encodedAccessUnits={EncodedAccessUnits}",
            KeyFrameRequests,
            EncodedAccessUnits);
        encoder.RequestKeyFrame();
    }

    /// <summary>
    /// Called when any viewer's RTCP shows sustained loss. Quality is global, so the worst
    /// connection sets it for everyone — the alternative is a second encode per viewer.
    /// </summary>
    public void ReportPoorReception()
    {
        // Loss means at least one decoder may have fallen out of sync. Request recovery even
        // when quality is already at its floor.
        RequestKeyFrame();

        var reduced = Quality.Reduced();
        if (reduced == Quality) return;
        Quality = reduced;
    }

    private void OnFrame(VideoFrame frame)
    {
        if (!_running) return;

        Interlocked.Increment(ref _framesCaptured);
        Interlocked.Exchange(ref _lastCapturedUtcTicks, _time.GetUtcNow().UtcTicks);

        EncodedVideoSample? sample;
        try
        {
            var stampedFrame = clock is null
                ? frame
                : new VideoFrame(frame.Width, frame.Height, frame.Bgra, clock.Now);
            sample = encoder.Encode(stampedFrame, Quality);
        }
        catch (Exception e)
        {
            LastFailure = $"{e.GetType().Name} (0x{e.HResult:X8}): {e.Message}";
            _logger.LogError(
                e,
                "Encoder exception stopped ScreenPublishPipeline. hresult=0x{HResult:X8} " +
                "framesCaptured={FramesCaptured} encodedAccessUnits={EncodedAccessUnits}",
                e.HResult,
                FramesCaptured,
                EncodedAccessUnits);

            // Stop before reporting: a failing encoder called once per frame at 30 Hz turns
            // one fault into a flood, and the session is over either way.
            _running = false;
            capture.FrameCaptured -= OnFrame;
            Failed?.Invoke(e);
            return;
        }

        if (sample is not { } encoded) return;

        Interlocked.Increment(ref _encodedAccessUnits);
        Interlocked.Exchange(ref _lastEncodedUtcTicks, _time.GetUtcNow().UtcTicks);
        UpdateMaximum(ref _maximumAccessUnitBytes, encoded.Data.Length);
        if (encoded.IsKeyFrame)
            Interlocked.Increment(ref _keyframesProduced);

        SampleEncoded?.Invoke(encoded);
    }

    private static DateTimeOffset? ReadTimestamp(ref long source)
    {
        var ticks = Interlocked.Read(ref source);
        return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
    }

    private static void UpdateMaximum(ref long target, long candidate)
    {
        var current = Interlocked.Read(ref target);
        while (candidate > current)
        {
            var observed = Interlocked.CompareExchange(ref target, candidate, current);
            if (observed == current) return;
            current = observed;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        await capture.DisposeAsync();
        encoder.Dispose();
    }
}
