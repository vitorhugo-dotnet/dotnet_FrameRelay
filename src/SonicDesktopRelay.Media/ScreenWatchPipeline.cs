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
public sealed class ScreenWatchPipeline(IVideoDecoder decoder, TimeProvider time) : IDisposable
{
    private static readonly TimeSpan StallAfter = TimeSpan.FromSeconds(4);

    private DateTimeOffset? _lastFrameAt;
    private bool _decodeRecoveryAsked;
    private bool _stallKeyFrameAsked;
    private WatchState _state = WatchState.Waiting;

    public event Action<VideoFrame>? FrameDecoded;

    public event Action<WatchState>? StateChanged;

    public event Action? KeyFrameNeeded;

    public WatchState State => _state;

    public string DecoderName => decoder.Name;

    public void Submit(EncodedVideoSample sample)
    {
        if (_state == WatchState.Failed) return;

        VideoFrame? frame;
        try
        {
            frame = decoder.Decode(sample);
        }
        catch (Exception)
        {
            SetState(WatchState.Failed);
            return;
        }

        if (frame is null)
        {
            // Once decoding has started, a swallowed frame means the decoder lost sync. Ask
            // immediately rather than waiting for the stall timer, but only once until a good
            // frame proves recovery.
            if (_state == WatchState.Receiving && !_decodeRecoveryAsked)
            {
                _decodeRecoveryAsked = true;
                KeyFrameNeeded?.Invoke();
            }

            return;
        }

        _lastFrameAt = time.GetUtcNow();
        _decodeRecoveryAsked = false;
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
        KeyFrameNeeded?.Invoke();
    }

    private void SetState(WatchState state)
    {
        if (_state == state) return;
        _state = state;
        StateChanged?.Invoke(state);
    }

    public void Dispose() => decoder.Dispose();
}
