using SonicDesktopRelay.Media;

namespace SonicDesktopRelay.Rtc;

public enum RtcMediaKind
{
    Unknown,
    Audio,
    Video
}

/// <summary>A reception report tied to its source SSRC and confidently associated media kind.</summary>
public sealed record RtcpReceptionReport(RtcMediaKind MediaKind, uint Ssrc, double FractionLost);

/// <summary>
/// One WebRTC connection to one viewer. Declared here rather than using SIPSorcery's types
/// directly so the fan-out and negotiation logic can be tested without a network stack.
/// </summary>
public interface IPeerConnection : IAsyncDisposable
{
    Guid ParticipantId { get; }

    event Action<string, string?, int?>? IceCandidateGathered;

    /// <summary>A clean encoder recovery point was requested, with its transport/recovery cause.</summary>
    event Action<KeyFrameRequestReason>? KeyFrameRequested;

    /// <summary>RTCP reception reports associated with a negotiated media track when possible.</summary>
    event Action<RtcpReceptionReport>? ReceptionReportReceived;

    /// <summary>Safe metadata for the nominated ICE pair once one exists.</summary>
    event Action<RtcTransportDiagnostics>? TransportDiagnosticsChanged;

    RtcTransportDiagnostics? TransportDiagnostics { get; }

    Task<string> CreateOfferAsync(CancellationToken ct);

    Task ApplyAnswerAsync(string sdp, CancellationToken ct);

    Task AddIceCandidateAsync(string candidate, string? sdpMid, int? sdpMLineIndex, CancellationToken ct);

    void SendVideo(EncodedVideoSample sample);

    void SendAudio(EncodedAudioSample sample);
}

public interface IPeerConnectionFactory
{
    IPeerConnection Create(Guid participantId, bool? forceRelay = null);
}
