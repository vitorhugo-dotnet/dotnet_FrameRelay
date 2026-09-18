namespace SonicDesktopRelay.Rtc;

/// <summary>
/// One metadata-only viewer WebRTC negotiation/state event. This type intentionally has no
/// property capable of carrying SDP, ICE candidate contents, TURN credentials or media bytes.
/// </summary>
public sealed record ViewerNegotiationDiagnosticEntry(
    DateTimeOffset Timestamp,
    string Event,
    string SignalingState,
    string IceGatheringState,
    string IceConnectionState,
    string ConnectionState,
    string? SetDescriptionResult = null,
    string? ExceptionType = null,
    string? Message = null,
    Guid? From = null,
    Guid? To = null);

/// <summary>
/// A deterministic offer/answer failure with the exact negotiation stage preserved for the UI.
/// </summary>
public sealed class ViewerNegotiationException(
    string stage,
    string reason,
    Exception? innerException = null) : Exception(reason, innerException)
{
    public string Stage { get; } = stage;
}
