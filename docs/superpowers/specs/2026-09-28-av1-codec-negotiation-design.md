# Optional AV1 codec negotiation design

## Goal

Add hardware-accelerated AV1 as an optional screen-sharing codec while preserving H.264 as the mandatory fallback. AV1 is selected only when the publisher and every active viewer can use a mutually compatible AV1 configuration. Codec selection, implementation, acceleration path, and fallback reason are observable in diagnostics.

This design assumes one shared encoded video stream per publishing session. If any active viewer cannot receive AV1, the session uses H.264 for all viewers. A viewer joining an AV1 session can therefore cause the session to return to H.264. Separate per-viewer encoding is out of scope for this change.

## Current architecture and constraints

- `RtcVideoPublishHost` owns one `MediaFoundationH264Encoder`; `ScreenPublishPipeline` encodes once and `VideoPublisher` fans the same access units out to one `IPeerConnection` per viewer.
- The publisher offer and viewer answer each register H.264 only. `SipSorceryViewerPeerConnection` consumes RTP packets through `H264RtpAccessUnitAssembler` before forwarding encoded samples to the decoder pipeline.
- `RtcVideoWatchHost` creates `MediaFoundationH264Decoder` directly. Encoder and decoder contracts are codec-neutral, but the native implementations, diagnostics, and RTP receive path are H.264-specific.
- The RTC project pins SIPSorcery 10.0.16. SIPSorcery 10.0.11 added AV1 video codec support, so the pinned version includes that baseline. Verify the exact AV1 offer, answer, packetization, and depacketization APIs used by this project before implementation; do not infer AV1 WebRTC support from Media Foundation codec presence alone.
- The shared-stream architecture means a single offer/answer cannot independently select AV1 for one viewer and H.264 for another without adding per-codec encode pipelines. This design chooses the common codec for all active viewers to preserve bounded queues and one capture/encode path.

## Capability model

Introduce a codec-neutral capability result for local and remote peers. AV1 capability is true only when all required checks pass:

1. The OS and Media Foundation expose a usable AV1 transform.
2. The transform can be initialized for the selected profile, dimensions, and frame rate, and is hardware accelerated. Software AV1 is excluded by policy.
3. The local WebRTC stack can advertise, negotiate, packetize/depacketize, and carry the compatible AV1 RTP format.
4. The peer advertises the compatible AV1 codec/profile through SDP.

Probe by enumerating and initializing the actual Media Foundation transforms; do not infer support from GPU identity. Keep encoder and decoder capability independent. H.264 remains available as the required fallback. Capability probing must dispose temporary native resources and must not block the capture callback.

## Negotiation and fallback

1. Detect local publisher AV1 hardware encode capability before publishing.
2. Advertise AV1 alongside H.264 only when local encode capability and the WebRTC AV1 path are available. H.264 remains present in every offer.
3. Each viewer detects AV1 hardware decode capability and registers only the formats it can actually decode. SDP answer intersection identifies remote support; a signaling self-report is not authoritative.
4. Select AV1 only when publisher encode, every active viewer decode, WebRTC support, and AV1 profile constraints intersect. Otherwise select H.264 and record a stable fallback reason.
5. Because all viewers consume the same encoded stream, adding an AV1-incompatible viewer while AV1 is active transitions the session back to H.264. Renegotiation and pipeline replacement must be bounded and serialized; queued old-codec samples are discarded before H.264 resumes.
6. If AV1 encoder or decoder initialization fails, close the failed candidate, record the failure, and retry with H.264 without terminating the session. Never label AV1 as active until the selected native transform and SDP negotiation both succeed.

The policy must define behavior when a viewer changes capabilities during a session and when a downgrade/restart fails. H.264 remains the terminal safe path; if H.264 initialization itself fails, preserve existing session-start failure behavior.

## Media and transport changes

- Generalize codec selection/factory boundaries while keeping the existing `IVideoEncoder` and `IVideoDecoder` data-flow contracts unless implementation proves an extension is necessary.
- Add Media Foundation AV1 encoder and decoder implementations only for configurations validated on supported Windows systems. Use the same bounded latest-frame-wins encode queue and existing media timing/quality policy.
- Add AV1 RTP access-unit reconstruction and integrity checks compatible with the selected SIPSorcery version. Do not pass AV1 packets through the H.264 assembler or depacketizer.
- Preserve one session encoder stream and existing per-viewer transport queues. Keep audio negotiation and audio-first BUNDLE ordering unchanged.
- Verify whether a SIPSorcery upgrade is required for AV1 SDP/RTP support. If needed, upgrade only that dependency to the smallest compatible release and pin it; do not add a software codec dependency.

## Diagnostics

Expose structured local supported codecs, remote/common supported codecs, negotiated codec, encoder and decoder names, hardware/software path, and AV1 fallback reason. Include initialization failure details without logging SDP, ICE credentials, or fingerprints. Update both publisher and viewer diagnostics surfaces; show H.264 as the active codec when a fallback completes.

Record AV1 encode/decode duration alongside existing dimensions, frame rate, bitrate, queue depth, and dropped-frame metrics. Avoid per-frame UI events; reuse the existing sampled diagnostics/watchdog update cadence.

## Failure handling and concurrency

- AV1 probe or initialization failure degrades to H.264 and is visible to the user/operator.
- The capture callback never performs probing, initialization, or codec switching.
- Codec transitions serialize with viewer add/remove and peer renegotiation. Use bounded queues, drain or discard samples from the previous codec, request a clean keyframe after switching, and keep the existing audio path running.
- Any switch that cannot establish H.264 leaves the session in its existing failed state with a clear diagnostic rather than sending a mismatched payload.

## Validation

Automated coverage should include pure negotiation policy cases (AV1 on both sides, either peer H.264-only, mixed viewers, hardware unavailable, software-only transforms, and transform initialization failure), SDP codec intersection, AV1 RTP assembly including loss/reordering, AV1-to-H.264 fallback, and existing H.264 send/receive behavior.

On Windows hardware that exposes AV1 transforms, run encoder/decoder integration coverage. Hardware-dependent tests must report a skip reason when transforms are unavailable. Compare AV1 and H.264 using identical capture dimensions, frame rate, target visual quality, and duration; report bitrate, encode/decode time, dropped frames, and end-to-end latency where measurable. Do not claim AV1 is preferable until those measurements exist.

## Out of scope

- Removing or weakening H.264 support.
- Software AV1 encode/decode.
- HEVC/H.265, Linux support, or capture-pipeline redesign.
- Simultaneous per-viewer codec streams or multiple concurrent encoder pipelines.
- Unbounded frame buffering or changes to existing congestion-control and quality adaptation policy.
