using SIPSorcery.Net;

namespace SonicDesktopRelay.Rtc;

/// <summary>
/// Safe, endpoint-free classification of the ICE candidate pair selected for media.
/// Raw candidates, addresses, ports, usernames and credentials deliberately do not cross
/// this boundary.
/// </summary>
public sealed record RtcTransportDiagnostics(
    string Path,
    string Protocol,
    string LocalCandidateType,
    string RemoteCandidateType)
{
    public override string ToString() => $"{Path}/{Protocol}";
}

/// <summary>
/// Converts nominated ICE candidate metadata into the small diagnostics vocabulary used by
/// FrameRelay. This is intentionally pure so classification can be tested without a live peer.
/// </summary>
public static class RtcTransportClassifier
{
    public static RtcTransportDiagnostics Classify(
        RTCIceCandidateType localType,
        RTCIceCandidateType remoteType,
        RTCIceProtocol localProtocol,
        RTCIceProtocol remoteProtocol)
    {
        var path = localType == RTCIceCandidateType.relay || remoteType == RTCIceCandidateType.relay
            ? "TURN"
            : "Direct";

        // A valid nominated pair normally uses the same protocol on both sides. If a stack
        // reports mixed metadata, TCP is the conservative classification because it is the
        // path where head-of-line blocking matters for our diagnostics.
        var protocol = localProtocol == RTCIceProtocol.tcp || remoteProtocol == RTCIceProtocol.tcp
            ? "TCP"
            : "UDP";

        return new RtcTransportDiagnostics(
            path,
            protocol,
            localType.ToString(),
            remoteType.ToString());
    }
}
