namespace SonicDesktopRelay.Signaling;

/// <summary>
/// The wire strings from dotnet_SonicRelay/docs/protocol.md. Server-generated types are
/// listed for recognition only — sending one is rejected by the server.
/// </summary>
public static class SignalingMessageTypes
{
    public const string SessionJoined = "session.joined";
    public const string SessionLeft = "session.left";
    public const string SessionEnded = "session.ended";
    public const string ParticipantDisconnected = "participant.disconnected";
    public const string ParticipantReconnected = "participant.reconnected";
    public const string ParticipantCapabilities = "participant.capabilities";
    public const string Error = "error";

    public const string PublisherReady = "publisher.ready";
    public const string ViewerReady = "viewer.ready";
    public const string WebRtcOffer = "webrtc.offer";
    public const string WebRtcAnswer = "webrtc.answer";
    public const string WebRtcIceCandidate = "webrtc.ice_candidate";
    public const string VideoReceiverStats = "video.receiver_stats";
    public const string WebRtcRenegotiate = "webrtc.renegotiate";
    public const string Ping = "ping";
    public const string Pong = "pong";
}
