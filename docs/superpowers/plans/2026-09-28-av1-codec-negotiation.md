# Optional AV1 Codec Negotiation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add hardware-only AV1 screen-sharing with explicit capability negotiation and session-wide H.264 fallback.

**Architecture:** Keep one capture and encoded stream per session. Add codec capability/selection contracts, Media Foundation AV1 implementations, WebRTC AV1 format and RTP handling, session-wide selection across all viewers, fallback to H.264 on incompatibility or initialization failure, and sampled diagnostics. No capture callback or per-viewer send queue performs codec work.

**Tech Stack:** .NET 10, C#, Media Foundation via Vortice 3.8.3, SIPSorcery, Avalonia diagnostics UI, xUnit.

**Spec:** `docs/superpowers/specs/2026-09-28-av1-codec-negotiation-design.md`

## Global Constraints

- H.264 remains mandatory and is retained in every supported negotiation path.
- AV1 is selected only when the publisher and all active viewers have compatible hardware encode/decode and WebRTC support.
- Initial AV1 scope is 8-bit 4:2:0 AV1 Main profile; negotiate a level supported by the publisher and every viewer for the selected dimensions and frame rate.
- Software AV1 encode/decode is excluded.
- Capability is established by Media Foundation transform enumeration and successful configuration, never GPU model inference.
- Keep one bounded capture/encode path per session and existing latest-frame-wins behavior.
- Keep audio first in BUNDLE order and leave audio negotiation unchanged.
- Keep SIPSorcery 10.0.16 unless API inspection proves a specific required AV1 SDP/RTP feature is missing; do not update dependencies without that evidence.
- Do not log SDP, ICE credentials, or fingerprints.
- Do not run test commands during implementation unless the user explicitly authorizes test execution; the linked issue does require adding automated coverage.

## Review Focus

- AV1-capable publisher with any H.264-only viewer must use H.264 for the shared stream; test mixed viewer capabilities and verify all peers agree on the selected payload.
- AV1 peers with incompatible profile/level limits must use H.264; test Main-profile selection and reject a level that cannot carry the selected dimensions and frame rate.
- A viewer whose AV1 decoder fails after capability probing must trigger H.264 fallback without ending the session; test the failure transition and active codec state.
- A software-only AV1 transform or an MFT unable to initialize for the selected size/profile must not be advertised; test hardware classification, H.264 reason, and resource cleanup.
- AV1 RTP packet loss/reordering must not route data through H.264 reconstruction or create unbounded buffering; test access-unit integrity and recovery behavior.

---

### Task 1: Confirm AV1 support in the RTC dependency and settle the wire-format boundary

**Files:**
- Inspect: `src/SonicDesktopRelay.Rtc/SonicDesktopRelay.Rtc.csproj`
- Inspect: `src/SonicDesktopRelay.Rtc/SipSorceryPeerConnection.cs`
- Inspect: `src/SonicDesktopRelay.Rtc/SipSorceryViewerPeerConnection.cs`
- Inspect: `src/SonicDesktopRelay.Rtc/H264RtpAccessUnitAssembler.cs`
- Modify only if required: `src/SonicDesktopRelay.Rtc/SonicDesktopRelay.Rtc.csproj`
- Test: `tests/SonicDesktopRelay.Rtc.Tests/SipSorceryPeerConnectionTests.cs`

**Interfaces:**
- Produces: verified SIPSorcery 10.0.16 AV1 `VideoFormat`/RTP send and receive API details for later tasks. Keep version 10.0.16 unless a required API is demonstrably missing. Do not add a separate software codec package.

- [x] Inspect the resolved SIPSorcery package metadata/source for AV1 SDP registration, RTP packetization, and low-level RTP receive support.
- [x] Confirm no dependency update is needed for AV1 in version 10.0.16, which includes AV1 support introduced in 10.0.11.
- [x] Add a focused offer characterization test that proves AV1 and H.264 are both advertised when AV1 is enabled and H.264 remains present when it is disabled.
- [x] Add a viewer-answer characterization test for AV1 intersection and H.264-only fallback.

### Task 2: Add codec-neutral capability and shared-session selection policy

**Files:**
- Create: `src/SonicDesktopRelay.Media/VideoCodec.cs`
- Create: `src/SonicDesktopRelay.Media/VideoCodecCapabilities.cs`
- Create: `src/SonicDesktopRelay.Media/VideoCodecNegotiator.cs`
- Modify: `src/SonicDesktopRelay.Media/VideoContracts.cs`
- Test: `tests/SonicDesktopRelay.Media.Tests/VideoCodecNegotiatorTests.cs`

**Interfaces:**
- Produces: `public enum VideoCodec { H264, Av1 }`.
- Produces: `public sealed record VideoCodecConstraints(string Profile, int MaxLevel)`; AV1 uses Main profile (`"0"`) and the maximum level supported by the advertised transform/peer.
- Produces: `public sealed record VideoCodecCapabilities(IReadOnlySet<VideoCodec> Encoders, IReadOnlySet<VideoCodec> Decoders, IReadOnlyDictionary<VideoCodec, VideoCodecConstraints> Constraints, IReadOnlyDictionary<VideoCodec, string> RejectionReasons)`.
- Produces: `public sealed record VideoCodecSelection(VideoCodec Codec, string? FallbackReason)`.
- Produces: `public static class VideoCodecNegotiator` with `Select(VideoCodecCapabilities publisher, IReadOnlyCollection<VideoCodecCapabilities> viewers, VideoCodecConstraints requiredAv1) -> VideoCodecSelection`.
- Produces: `EncodedVideoSample.Codec { get; init; }`, defaulting to `VideoCodec.H264` so existing six-argument construction remains source-compatible; AV1 encoder/receive paths set `Codec = VideoCodec.Av1`.

- [ ] Add tests for AV1 on publisher and all viewers, either-side H.264-only, no active viewers (select H.264 with `no-active-viewers`), mixed viewers, and stable fallback reasons.
- [ ] Add tests for AV1 Main-profile compatibility and rejection when any peer's supported level is below the level required by the selected dimensions and frame rate.
- [ ] Add tests that software-only AV1 capabilities are absent from the advertised capability set.
- [ ] Implement selection so AV1 is returned only when publisher encode and every viewer decode contain AV1 Main and their maximum levels meet `requiredAv1`; otherwise return H.264 with a stable reason.
- [ ] Keep codec values and reasons independent of Media Foundation and SIPSorcery types.

### Task 3: Implement Media Foundation AV1 capability probing and codecs

**Files:**
- Create: `src/SonicDesktopRelay.Media.Windows/MediaFoundationAv1CapabilityProbe.cs`
- Create: `src/SonicDesktopRelay.Media.Windows/MediaFoundationAv1Encoder.cs`
- Create: `src/SonicDesktopRelay.Media.Windows/MediaFoundationAv1Decoder.cs`
- Modify as required to share transform enumeration/configuration: `src/SonicDesktopRelay.Media.Windows/MediaFoundationH264Encoder.cs`
- Modify as required to share transform enumeration/configuration: `src/SonicDesktopRelay.Media.Windows/MediaFoundationH264Decoder.cs`
- Test: `tests/SonicDesktopRelay.Media.Windows.Tests/MediaFoundationAv1CapabilityProbeTests.cs`
- Test: `tests/SonicDesktopRelay.Media.Windows.Tests/MediaFoundationAv1CodecTests.cs`

**Interfaces:**
- Consumes: `VideoCodecCapabilities`, `IVideoEncoder`, and `IVideoDecoder` from Task 2/current media contracts.
- Produces: `MediaFoundationAv1CapabilityProbe.Detect()` returning AV1 encode/decode capability, hardware classification, and rejection reasons; `MediaFoundationAv1Encoder : IVideoEncoder`; `MediaFoundationAv1Decoder : IVideoDecoder`.

- [ ] Add injectable transform-enumeration tests for absent AV1, software-only AV1, hardware AV1, and transform activation/configuration failure.
- [ ] Implement probe using Media Foundation encoder and decoder categories independently; release every rejected/temporary COM transform and activation object.
- [ ] Implement AV1 encoder output using the same `EncodedVideoSample` dimensions, keyframe, and duration contract used by H.264.
- [ ] Implement AV1 decoder input using the same `VideoFrame` contract used by H.264.
- [ ] Reuse the existing asynchronous MFT pump and bounded frame handling where compatible; keep blocking initialization outside the capture callback.
- [ ] Add hardware integration coverage that skips with a concrete reason when the host exposes no usable hardware AV1 transforms.

### Task 4: Add AV1 WebRTC negotiation and RTP access-unit handling

**Files:**
- Create: `src/SonicDesktopRelay.Rtc/Av1RtpAccessUnitAssembler.cs`
- Modify: `src/SonicDesktopRelay.Rtc/SipSorceryPeerConnection.cs`
- Modify: `src/SonicDesktopRelay.Rtc/SipSorceryViewerPeerConnection.cs`
- Modify: `src/SonicDesktopRelay.Rtc/IPeerConnection.cs`
- Modify: `src/SonicDesktopRelay.Rtc/IViewerPeerConnection.cs`
- Test: `tests/SonicDesktopRelay.Rtc.Tests/Av1RtpAccessUnitAssemblerTests.cs`
- Test: `tests/SonicDesktopRelay.Rtc.Tests/SipSorceryPeerConnectionTests.cs`
- Test: `tests/SonicDesktopRelay.Rtc.Tests/SipSorceryViewerPeerConnectionTests.cs`

**Interfaces:**
- Consumes: the verified AV1 SIPSorcery format and packet APIs from Task 1 and codec values/capabilities from Task 2.
- Produces: codec-aware peer construction and a viewer receive path that emits validated `EncodedVideoSample` values tagged with the negotiated codec.

- [ ] Add RTP assembly tests for single-packet frames, fragmented frames, sequence gaps, reordering, malformed payloads, and bounded retained bytes.
- [ ] Add SDP tests for AV1+H.264 offers, AV1-only-capable viewer intersection, and H.264-only viewer intersection.
- [ ] Register AV1 only when the local peer's hardware capability and the verified SIPSorcery AV1 path are both available; keep H.264 in the offer.
- [ ] Route AV1 packets through an AV1-specific assembler and H.264 packets through the existing H.264 assembler, selected from the negotiated payload format.
- [ ] Emit the negotiated codec and avoid forwarding access units until negotiation is complete.

### Task 5: Coordinate one codec across viewers and fall back without ending the session

**Files:**
- Modify: `src/SonicDesktopRelay.Rtc/VideoPublisher.cs`
- Modify: `src/SonicDesktopRelay.Rtc/VideoSubscriber.cs`
- Modify: `src/SonicDesktopRelay.App/RtcVideoPublishHost.cs`
- Modify: `src/SonicDesktopRelay.App/RtcVideoWatchHost.cs`
- Modify: `src/SonicDesktopRelay.Media/ScreenPublishPipeline.cs` only if a safe encoder swap boundary requires it
- Modify: `src/SonicDesktopRelay.Media/ScreenWatchPipeline.cs` only if a safe decoder swap boundary requires it
- Test: `tests/SonicDesktopRelay.Rtc.Tests/VideoPublisherCodecNegotiationTests.cs`
- Test: `tests/SonicDesktopRelay.Rtc.Tests/VideoSubscriberTests.cs`
- Test: `tests/SonicDesktopRelay.Rtc.Tests/VideoPublisherTests.cs`
- Test: `tests/SonicDesktopRelay.Presentation.Tests/SessionRuntimeTests.cs`

**Interfaces:**
- Consumes: `VideoCodecNegotiator.Select`, codec-aware peer results, and the AV1 Media Foundation implementations.
- Produces: a publisher session whose encoder codec matches every active peer. A viewer reporting AV1 decoder initialization failure sends the existing `webrtc.renegotiate` message with `reason: "av1_decoder_init_failed"` and its current `negotiationId`; the publisher validates it and downgrades the whole session to H.264. A late viewer that changes the common codec to H.264 follows the same transition.

- [ ] Add policy integration tests for AV1/AV1, AV1 publisher with H.264-only viewer, H.264-only publisher with AV1 viewer, mixed viewers, profile/level mismatch, and late incompatible viewer arrival.
- [ ] Add a failure-injection test proving AV1 encoder initialization failure starts the H.264 session and retains the session.
- [ ] Add a failure-injection test proving viewer AV1 decoder initialization failure reports incompatibility and causes shared-session H.264 fallback.
- [ ] Add a signaling test proving stale negotiation IDs and unrecognized codec-fallback reasons cannot trigger a session downgrade.
- [ ] Implement serialized viewer add/remove and codec transition coordination. Discard old-codec queued samples, keep queue limits unchanged, and request a clean keyframe after a transition.
- [ ] Re-offer H.264 to every current viewer using the verified SIPSorcery renegotiation API; keep each viewer's `VideoSubscriber` peer alive when renegotiation succeeds.
- [ ] If existing signaling rejects the codec-fallback reason or SIPSorcery cannot renegotiate to H.264 safely, stop and revise the design/protocol before implementing fallback.
- [ ] Keep audio pipelines alive throughout a video codec transition.

### Task 6: Surface codec decision and failure reason in diagnostics

**Files:**
- Modify: `src/SonicDesktopRelay.Presentation/SessionSnapshot.cs`
- Modify: `src/SonicDesktopRelay.App/RtcVideoPublishHost.cs`
- Modify: `src/SonicDesktopRelay.App/RtcVideoWatchHost.cs`
- Modify: `src/SonicDesktopRelay.App/Shell.cs`
- Modify: `src/SonicDesktopRelay.App/Views/DiagnosticsView.axaml`
- Test: `tests/SonicDesktopRelay.Presentation.Tests/SignalingDiagnosticsTests.cs`
- Test: `tests/SonicDesktopRelay.Media.Windows.Tests/NativeVideoDiagnosticsTests.cs`

**Interfaces:**
- Produces: immutable diagnostics fields for local codecs, each remote viewer's codecs, the common codec set, negotiated codec/profile/level, encoder/decoder implementation, acceleration path, and fallback reason, projected through the existing sampled diagnostics flow.

- [ ] Add presentation tests for AV1 selected, H.264 fallback with reason, and no active stream.
- [ ] Add host diagnostics tests confirming the active encoder/decoder name and acceleration come from the instantiated codec, not a fresh probe.
- [ ] Project local/per-viewer remote codecs, common codecs, selected profile/level, acceleration, and fallback reason into the diagnostics page without exposing SDP/ICE secrets.
- [ ] Include encode/decode duration in sampled metrics and preserve the current low-frequency UI update cadence.

### Task 7: Record equivalent-workload performance comparison and complete focused validation

**Files:**
- Create: `docs/performance/av1-h264-screen-sharing.md`
- Test: all new targeted test files from Tasks 2–6, plus existing H.264 media and RTC tests listed below

**Interfaces:**
- Consumes: diagnostics and timing counters produced by Task 6.
- Produces: a reproducible benchmark procedure and measured table comparing AV1 and H.264 under identical screen-sharing settings, including bitrate, encode/decode time, dropped frames, and latency when available.

- [ ] Record Windows OS/build, hardware, codec transform, profile, dimensions, frame rate, duration, and visual-quality target for each run.
- [ ] Compare AV1 and H.264 under identical workloads; report missing metrics as unavailable rather than inferring them.
- [ ] Add regression coverage for existing H.264 offer/answer, encode/decode, and RTP integrity behavior alongside the AV1 cases.
- [ ] Provide the exact targeted test commands in the PR description; execute them only if the user authorizes test execution.

## Execution Notes

- Preserve one reviewable commit per completed task and keep each commit limited to that task's files.
- Do not claim AV1 support on a machine until the hardware transform, WebRTC codec path, and selected peer configuration have all succeeded.
- If safe shared-codec transitions are not supported by the verified SIPSorcery version, stop before implementing an unsafe mixed-codec path and update the design for review.
