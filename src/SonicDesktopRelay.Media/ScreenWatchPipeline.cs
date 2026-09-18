using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SonicDesktopRelay.Media;

public enum WatchState
{
    Waiting,
    Receiving,

    /// <summary>
    /// The connection is up but no frame has arrived for a while. Distinct from disconnected
    /// on purpose: the two have different causes and different fixes, and conflating them
    /// sends the user looking in the wrong place.
    /// </summary>
    Stalled,

    Failed
}

/// <summary>
/// sample → decode → one frame event, for the whole session. The mirror image of
/// <see cref="ScreenPublishPipeline"/>: a viewer has exactly one publisher, so there is
/// exactly one decoder.
/// </summary>
public sealed class ScreenWatchPipeline(
    IVideoDecoder decoder,
    TimeProvider time,
    ILogger<ScreenWatchPipeline>? logger = null) : IDisposable
{
    private static readonly TimeSpan StallAfter = TimeSpan.FromSeconds(4);
    private readonly ILogger<ScreenWatchPipeline> _logger =
        logger ?? NullLogger<ScreenWatchPipeline>.Instance;

    private DateTimeOffset? _lastFrameAt;
    private long _lastAccessUnitUtcTicks;
    private long _videoAccessUnitsReceived;
    private long _decodedFrames;
    private long _keyAccessUnitsReceived;
    private long _nullDecodeResults;
    private long _keyFrameRequests;
    private long _maximumAccessUnitBytes;
    private bool _stallKeyFrameAsked;
    private WatchState _state = WatchState.Waiting;

    public event Action<VideoFrame>? FrameDecoded;

    public event Action<WatchState>? StateChanged;

    public event Action? KeyFrameNeeded;

    public WatchState State => _state;

    public string DecoderName => decoder.Name;

    public long VideoAccessUnitsReceived => Interlocked.Read(ref _videoAccessUnitsReceived);

    public long DecodedFrames => Interlocked.Read(ref _decodedFrames);

    public long KeyAccessUnitsReceived => Interlocked.Read(ref _keyAccessUnitsReceived);

    public long NullDecodeResults => Interlocked.Read(ref _nullDecodeResults);

    public long KeyFrameRequests => Interlocked.Read(ref _keyFrameRequests);

    public long MaximumAccessUnitBytes => Interlocked.Read(ref _maximumAccessUnitBytes);

    public DateTimeOffset? LastAccessUnitAt
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastAccessUnitUtcTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    public DateTimeOffset? LastDecodedFrameAt => _lastFrameAt;

    /// <summary>
    /// Reason attached to a terminal media failure. A Failed state without a reason is a
    /// diagnostics bug because it makes the UI claim decoding failed while hiding the evidence.
    /// </summary>
    public string? LastFailure { get; private set; }

    public void Submit(EncodedVideoSample sample)
    {
        Interlocked.Increment(ref _videoAccessUnitsReceived);
        Interlocked.Exchange(ref _lastAccessUnitUtcTicks, time.GetUtcNow().UtcTicks);
        UpdateMaximum(ref _maximumAccessUnitBytes, sample.Data.Length);
        if (sample.IsKeyFrame)
            Interlocked.Increment(ref _keyAccessUnitsReceived);

        // Keep transport-side counters moving even after a terminal decoder exception. This is
        // diagnostic-only: it lets the next reproduction prove whether RTP/access units continue
        // after the visible picture freezes without changing recovery semantics.
        if (_state == WatchState.Failed) return;

        VideoFrame? frame;
        try
        {
            frame = decoder.Decode(sample);
        }
        catch (Exception exception)
        {
            LastFailure =
                $"{exception.GetType().Name} (0x{exception.HResult:X8}): {exception.Message}";

            _logger.LogError(
                exception,
                "Decoder exception escaped into ScreenWatchPipeline. " +
                "hresult=0x{HResult:X8} accessUnits={VideoAccessUnits} decodedFrames={DecodedFrames} " +
                "accessUnitBytes={AccessUnitBytes} keyFrame={IsKeyFrame}",
                exception.HResult,
                VideoAccessUnitsReceived,
                DecodedFrames,
                sample.Data.Length,
                sample.IsKeyFrame);

            SetState(WatchState.Failed);
            return;
        }

        if (frame is null)
        {
            // A decoder returning no frame is not transport loss. Media Foundation legitimately
            // does this when the transform needs more input, so treating null as packet loss
            // creates false PLI storms. RTP integrity/recovery is handled before Decode().
            Interlocked.Increment(ref _nullDecodeResults);
            return;
        }

        _lastFrameAt = time.GetUtcNow();
        Interlocked.Increment(ref _decodedFrames);
        _stallKeyFrameAsked = false;
        SetState(WatchState.Receiving);
        FrameDecoded?.Invoke(frame);
    }

    /// <summary>Called on a timer by the host; keeps the clock out of this class.</summary>
    public void CheckForStall()
    {
        if (_state is WatchState.Failed or WatchState.Waiting) return;
        if (_lastFrameAt is not { } last) return;
        if (time.GetUtcNow() - last < StallAfter) return;

        SetState(WatchState.Stalled);

        // One PLI per stall, not one per tick: flooding the publisher with keyframe requests
        // is the worst thing to do to a link that is already failing to deliver.
        if (_stallKeyFrameAsked) return;
        _stallKeyFrameAsked = true;
        RequestKeyFrame("stall");
    }

    private void RequestKeyFrame(string reason)
    {
        Interlocked.Increment(ref _keyFrameRequests);
        _logger.LogWarning(
            "Viewer requested a recovery keyframe. reason={Reason} accessUnits={VideoAccessUnits} " +
            "decodedFrames={DecodedFrames} nullDecodes={NullDecodeResults}",
            reason,
            VideoAccessUnitsReceived,
            DecodedFrames,
            NullDecodeResults);
        KeyFrameNeeded?.Invoke();
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

    private void SetState(WatchState state)
    {
        if (_state == state) return;
        var previous = _state;
        _state = state;

        _logger.LogInformation(
            "Watch media state changed {PreviousState} -> {NewState}. accessUnits={VideoAccessUnits} " +
            "decodedFrames={DecodedFrames} lastFailure={LastFailure}",
            previous,
            state,
            VideoAccessUnitsReceived,
            DecodedFrames,
            LastFailure);

        StateChanged?.Invoke(state);
    }

    public void Dispose() => decoder.Dispose();
}
