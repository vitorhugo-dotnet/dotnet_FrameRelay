using SonicDesktopRelay.Media;

namespace SonicDesktopRelay.Rtc;

/// <summary>
/// The viewer's single WebRTC connection to the one publisher. The mirror of
/// <see cref="IPeerConnection"/>: this side never sends media, it answers and receives.
/// Declared here rather than using SIPSorcery's types directly so negotiation can be tested
/// without a network stack.
/// </summary>
public interface IViewerPeerConnection : IAsyncDisposable
{
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

    /// <summary>Applies the publisher's offer and produces the answer SDP.</summary>
    Task<string> CreateAnswerAsync(string offerSdp, CancellationToken ct);

    Task AddIceCandidateAsync(string candidate, string? sdpMid, int? sdpMLineIndex, CancellationToken ct);

    /// <summary>Sends a PLI. The publisher only emits keyframes on demand, so this is the ask.</summary>
    void RequestKeyFrame();
}

public interface IViewerPeerConnectionFactory
{
    IViewerPeerConnection Create();
}
