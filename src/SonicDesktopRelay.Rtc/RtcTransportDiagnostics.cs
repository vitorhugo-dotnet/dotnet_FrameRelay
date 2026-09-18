using System.Net.Sockets;
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
        RTCIceProtocol remoteProtocol,
        ProtocolType? localRelayServerProtocol = null)
    {
        var path = localType == RTCIceCandidateType.relay || remoteType == RTCIceCandidateType.relay
            ? "TURN"
            : "Direct";

        // SIPSorcery 10.0.16 deliberately advertises relay candidates as UDP even when the
        // local TURN server was reached over TCP/TLS. The local relay keeps its IceServer,
        // whose Protocol is therefore the authoritative local client->TURN transport.
        var protocol = localType == RTCIceCandidateType.relay && localRelayServerProtocol is { } turnProtocol
            ? turnProtocol == ProtocolType.Tcp ? "TCP" : "UDP"
            : localProtocol == RTCIceProtocol.tcp || remoteProtocol == RTCIceProtocol.tcp
                ? "TCP"
                : "UDP";

        return new RtcTransportDiagnostics(
            path,
            protocol,
            localType.ToString(),
            remoteType.ToString());
    }
}
