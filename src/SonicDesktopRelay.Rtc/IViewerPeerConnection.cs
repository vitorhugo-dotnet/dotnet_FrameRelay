using SonicDesktopRelay.Media;

namespace SonicDesktopRelay.Rtc;

/// <summary>Atomic interval source counters captured at the viewer transport boundary.</summary>
public readonly record struct VideoReceptionSnapshot(
    long RtpPacketsReceived,
    long RtpPacketsLost,
    long AccessUnitsReceived,
    long IncompleteAccessUnits);

/// <summary>
/// The viewer's single WebRTC connection to the one publisher. The mirror of
/// <see cref="IPeerConnection"/>: this side never sends media, it answers and receives.
/// Declared here rather than using SIPSorcery's types directly so negotiation can be tested
/// without a network stack.
/// </summary>
public interface IViewerPeerConnection : IAsyncDisposable
{
    /// <summary>Raised only for connected or terminal failed states.</summary>
    event Action<bool>? ConnectionStateChanged;
    event Action<string, string?, int?>? IceCandidateGathered;

    /// <summary>
    /// One reassembled, still-encoded H.264 access unit. Decoding is this project's job, not
    /// the transport's — the decoder is chosen and named on the Diagnostics page.
    /// </summary>
    event Action<EncodedVideoSample>? VideoSampleReceived;

    /// <summary>
    /// One still-encoded Opus sample. RTC owns transport only; decoding and playback stay in
    /// the independent audio media pipeline.
    /// </summary>
    event Action<EncodedAudioSample>? AudioSampleReceived;

    /// <summary>Metadata-only WebRTC negotiation and peer-state diagnostics.</summary>
    event Action<ViewerNegotiationDiagnosticEntry>? Diagnostic;

    /// <summary>Safe metadata for the nominated ICE pair once one exists.</summary>
    event Action<RtcTransportDiagnostics>? TransportDiagnosticsChanged;

    RtcTransportDiagnostics? TransportDiagnostics { get; }

    VideoReceptionSnapshot ReceptionSnapshot => default;

    /// <summary>Applies the publisher's offer and produces the answer SDP.</summary>
    Task<string> CreateAnswerAsync(string offerSdp, CancellationToken ct);

    Task AddIceCandidateAsync(string candidate, string? sdpMid, int? sdpMLineIndex, CancellationToken ct);

    /// <summary>Sends a PLI. The publisher only emits keyframes on demand, so this is the ask.</summary>
    void RequestKeyFrame();
}

public interface IViewerPeerConnectionFactory
{
    IViewerPeerConnection Create(bool? forceRelay = null);
}
