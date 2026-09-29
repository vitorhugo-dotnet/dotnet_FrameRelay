# Task 5 implementation report

Implemented session-wide AV1 startup with actual Media Foundation probing and H.264 startup fallback when AV1 encoder construction fails. The publisher serializes viewer lifecycle and codec transitions, clears queued old-codec samples, requests a fresh keyframe, keeps audio active, and reoffers H.264 across existing viewer peers. A late H.264 answer and a viewer's `av1_decoder_init_failed` request both trigger the shared transition; stale negotiation IDs and unknown reasons are ignored.

The viewer now probes AV1 decoder capability, advertises AV1 only when the probe succeeds, routes received samples to the matching decoder, and reports AV1 decoder initialization/runtime failure with its current negotiation ID. The subscriber retains its peer for the H.264 reoffer. Publisher codec selection now calls `VideoCodecNegotiator.Select` with the publisher's actual encoder probe and each active viewer's selected AV1 answer format parameters (`profile`, `level-idx`, and `tier`). Missing or malformed parameters cannot enable AV1. Relay retries use the same session gate and create an H.264-only offer after downgrade.

Safety verification used pinned SIPSorcery 10.0.16 API XML: `MediaStreamTrack.RestrictCapabilities(VideoFormat)` is documented for retaining one existing format across subsequent negotiations; `createOffer`, `setLocalDescription`, and `setRemoteDescription` are available on the live `RTCPeerConnection`. A focused runtime test renegotiates an AV1 peer to H.264 without replacing either peer. The existing signaling envelope serializes arbitrary JSON payloads and the publisher already handles `webrtc.renegotiate`.

Focused verification:

- RTC tests filtered to `VideoPublisherTests`, `VideoSubscriberTests`, and `SipSorceryPeerConnectionTests`: 63 passed.
- App failure-injection test `RtcVideoPublishHostCodecFallbackTests`: 1 passed.
- App build: 0 warnings, 0 errors.

Fix round 1 focused verification: RTC publisher/subscriber/SIPSorcery tests 67 passed; App build 0 warnings/errors. Coverage includes profile and level mismatch, a valid current-ID AV1 decoder failure causing session-wide H.264 fallback, and a relay retry that keeps H.264.

Fix round 2 derives the required AV1 level from scaled output dimensions and selected frame rate using the [AOM AV1 Annex A](https://github.com/AOMediaCodec/av1-spec/blob/master/annex.a.levels.md) MaxPicSize, MaxHSize, MaxVSize, and MaxDisplayRate limits. Unknown or unsupported workloads fail closed. The fixed 640×360@30 probe capability (level index 4) therefore cannot qualify a larger workload that requires a higher level. Capture resize updates the requirement under the session gate.

Answer processing now takes locks in session-then-peer order, applies the answer while protected, releases the peer gate before any transition reoffers, and retains the session gate through selection/switch. A deterministic concurrency test verifies answer application completes before a concurrent decoder-failure request can create the H.264 reoffer.

Fix round 2 focused verification: workload/policy tests 16 passed; RTC publisher/SIPSorcery tests 41 passed; App build 0 warnings/errors. A parallel test/build attempt first hit MSBuild shared-output file locks; sequential reruns passed. No full suites run.

Remaining review concern: the plan-listed `VideoPublisherCodecNegotiationTests.cs` does not exist in this checkout. Existing selection policy and SDP capability tests are in earlier task test files; this task adds focused transition, late-viewer, decoder-failure, signaling validation, and same-peer renegotiation tests. No full test suite was run.
