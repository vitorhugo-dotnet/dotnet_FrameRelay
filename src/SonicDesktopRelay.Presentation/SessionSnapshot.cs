using SonicDesktopRelay.Media;
using SonicDesktopRelay.Signaling;

namespace SonicDesktopRelay.Presentation;

public enum SessionPhase
{
    Idle,
    Preparing,
    Sharing,
    Joining,
    Watching,
    Ending,
    Failed
}

/// <summary>
/// Everything the UI is allowed to know, as one immutable value. Screens bind to this rather
/// than reaching into the runtime, so no page can hold a stale private copy of the state.
/// </summary>
public sealed record SessionSnapshot(
    SessionPhase Phase,
    string? Code,
    Guid? SessionId,
    int ViewerCount,
    SignalingState Signaling,
    string? Error,
    /// <summary>The codec that actually opened, e.g. "h264_nvenc". Null when not sharing.</summary>
    string? EncoderName = null,
    int FramesPerSecond = 0,
    int VideoHeight = 0,
    /// <summary>
    /// What the inbound media is doing, when watching. Deliberately separate from
    /// <see cref="Phase"/>: a stall is the media stopping while the session stays perfectly
    /// healthy, and folding it into the phase would send the user to debug the wrong thing.
    /// </summary>
    WatchState? Watching = null,
    /// <summary>The decoder that actually opened, e.g. "h264". Null when not watching.</summary>
    string? DecoderName = null,
    SessionMediaMetrics? Metrics = null)
{
    public static SessionSnapshot Idle { get; } =
        new(SessionPhase.Idle, null, null, 0, SignalingState.Disconnected, null);

    public bool IsBusy => Phase is SessionPhase.Preparing or SessionPhase.Joining or SessionPhase.Ending;
}

/// <summary>
/// UI-safe values from the active media host. The video rates are measured reception values;
/// publisher quality settings occupy separate target fields. Null means no valid sample.
/// </summary>
public sealed record SessionMediaMetrics(
    int? Width = null,
    int? Height = null,
    double? VideoBitrateBitsPerSecond = null,
    double? VideoFramesPerSecond = null,
    double? LatencyMilliseconds = null,
    string? Codec = null,
    string? Transport = null,
    double? TargetVideoBitrateBitsPerSecond = null,
    double? TargetVideoFramesPerSecond = null);

public sealed record CreatedSession(Guid SessionId, string Code);

/// <summary>
/// A backend refusal, carrying the API's machine-readable code so the runtime can put it in
/// the snapshot without the presentation layer depending on HTTP types.
/// </summary>
public sealed class SessionApiFailure(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public interface ISessionApi
{
    Task<CreatedSession> CreateScreenShareAsync(int maxViewers, CancellationToken ct);

    Task<Guid> JoinAsync(string code, CancellationToken ct);

    Task<Guid> JoinByIdAsync(Guid sessionId, CancellationToken ct);

    Task EndAsync(Guid sessionId, CancellationToken ct);
}

/// <summary>
/// What the runtime needs from the media stack, declared here so Presentation never references
/// Rtc or Media.Windows. The App composes the real one.
/// </summary>
public interface IVideoPublishHost : IAsyncDisposable
{
    event Action<string>? CaptureTargetClosed;

    string? EncoderName { get; }

    Task StartAsync(CaptureTarget target, VideoPublishProfile profile, CancellationToken ct);

    Task StopAsync();

    Task AddViewerAsync(Guid participantId, CancellationToken ct);

    Task RemoveViewerAsync(Guid participantId);

    Task HandleSignalingAsync(SignalingEnvelope envelope, CancellationToken ct);
}

/// <summary>
/// The viewer's mirror of <see cref="IVideoPublishHost"/>. There is no viewer list here: a
/// viewer has exactly one publisher, so there is exactly one peer and one decoder.
/// </summary>
public interface IVideoWatchHost : IAsyncDisposable
{
    string? DecoderName { get; }

    /// <summary>Waiting, receiving, stalled or failed. Raised off the UI thread.</summary>
    event Action<WatchState>? WatchStateChanged;

    /// <summary>
    /// An actionable WebRTC offer/answer failure. The value must never contain SDP,
    /// ICE candidate contents, credentials or media payloads.
    /// </summary>
    event Action<string>? NegotiationFailed;

    Task StartAsync(CancellationToken ct);

    Task StopAsync();

    Task HandleSignalingAsync(SignalingEnvelope envelope, CancellationToken ct);
}
