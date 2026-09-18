using SonicDesktopRelay.Media;

namespace SonicDesktopRelay.Rtc;

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

    /// <summary>Inbound-loss ratio this viewer reported over RTCP, 0..1.</summary>
    event Action<double>? PacketLossReported;

    Task<string> CreateOfferAsync(CancellationToken ct);

    Task ApplyAnswerAsync(string sdp, CancellationToken ct);

    Task AddIceCandidateAsync(string candidate, string? sdpMid, int? sdpMLineIndex, CancellationToken ct);

    void SendVideo(EncodedVideoSample sample);

    void SendAudio(EncodedAudioSample sample);
}

public interface IPeerConnectionFactory
{
    IPeerConnection Create(Guid participantId);
}
