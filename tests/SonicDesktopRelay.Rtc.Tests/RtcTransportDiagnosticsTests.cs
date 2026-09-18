using System.Net.Sockets;
using SIPSorcery.Net;
using Xunit;

namespace SonicDesktopRelay.Rtc.Tests;

public sealed class RtcTransportDiagnosticsTests
{
    [Theory]
    [InlineData(RTCIceCandidateType.host, RTCIceCandidateType.host, RTCIceProtocol.udp, RTCIceProtocol.udp, "Direct", "UDP")]
    [InlineData(RTCIceCandidateType.host, RTCIceCandidateType.srflx, RTCIceProtocol.udp, RTCIceProtocol.udp, "Direct", "UDP")]
    [InlineData(RTCIceCandidateType.relay, RTCIceCandidateType.host, RTCIceProtocol.udp, RTCIceProtocol.udp, "TURN", "UDP")]
    [InlineData(RTCIceCandidateType.host, RTCIceCandidateType.relay, RTCIceProtocol.udp, RTCIceProtocol.udp, "TURN", "UDP")]
    [InlineData(RTCIceCandidateType.relay, RTCIceCandidateType.host, RTCIceProtocol.tcp, RTCIceProtocol.tcp, "TURN", "TCP")]
    public void Candidate_pair_is_classified_without_exposing_endpoints(
        RTCIceCandidateType localType,
        RTCIceCandidateType remoteType,
        RTCIceProtocol localProtocol,
        RTCIceProtocol remoteProtocol,
        string expectedPath,
        string expectedProtocol)
    {
        var result = RtcTransportClassifier.Classify(
            localType,
            remoteType,
            localProtocol,
            remoteProtocol);

        Assert.Equal(expectedPath, result.Path);
        Assert.Equal(expectedProtocol, result.Protocol);
        Assert.Equal(localType.ToString(), result.LocalCandidateType);
        Assert.Equal(remoteType.ToString(), result.RemoteCandidateType);
    }

    [Fact]
    public void Local_turn_server_transport_overrides_the_relay_candidate_protocol()
    {
        // SIPSorcery 10.0.16 advertises relay candidates as UDP even when the TURN server
        // control transport is TCP. The selected local relay retains its IceServer.Protocol.
        var result = RtcTransportClassifier.Classify(
            RTCIceCandidateType.relay,
            RTCIceCandidateType.host,
            RTCIceProtocol.udp,
            RTCIceProtocol.udp,
            ProtocolType.Tcp);

        Assert.Equal("TURN", result.Path);
        Assert.Equal("TCP", result.Protocol);
    }

    [Fact]
    public void Transport_diagnostics_has_no_endpoint_or_credential_fields()
    {
        var names = typeof(RtcTransportDiagnostics)
            .GetProperties()
            .Select(x => x.Name)
            .ToArray();

        Assert.DoesNotContain(names, x =>
            x.Contains("Address", StringComparison.OrdinalIgnoreCase)
            || x.Contains("Port", StringComparison.OrdinalIgnoreCase)
            || x.Contains("Credential", StringComparison.OrdinalIgnoreCase)
            || x.Contains("Username", StringComparison.OrdinalIgnoreCase)
            || x.Contains("Candidate", StringComparison.OrdinalIgnoreCase)
               && !x.EndsWith("CandidateType", StringComparison.Ordinal));
    }
}
