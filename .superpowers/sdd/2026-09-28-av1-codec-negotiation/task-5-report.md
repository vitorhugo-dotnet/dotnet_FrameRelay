# Task 5 implementation report

Implemented session-wide AV1 startup with actual Media Foundation probing and H.264 startup fallback when AV1 encoder construction fails. The publisher serializes viewer lifecycle and codec transitions, clears queued old-codec samples, requests a fresh keyframe, keeps audio active, and reoffers H.264 across existing viewer peers. A late H.264 answer and a viewer's `av1_decoder_init_failed` request both trigger the shared transition; stale negotiation IDs and unknown reasons are ignored.

The viewer now probes AV1 decoder capability, advertises AV1 only when the probe succeeds, routes received samples to the matching decoder, and reports AV1 decoder initialization/runtime failure with its current negotiation ID. The subscriber retains its peer for the H.264 reoffer.

Safety verification used pinned SIPSorcery 10.0.16 API XML: `MediaStreamTrack.RestrictCapabilities(VideoFormat)` is documented for retaining one existing format across subsequent negotiations; `createOffer`, `setLocalDescription`, and `setRemoteDescription` are available on the live `RTCPeerConnection`. A focused runtime test renegotiates an AV1 peer to H.264 without replacing either peer. The existing signaling envelope serializes arbitrary JSON payloads and the publisher already handles `webrtc.renegotiate`.

Focused verification:

- RTC tests filtered to `VideoPublisherTests`, `VideoSubscriberTests`, and `SipSorceryPeerConnectionTests`: 63 passed.
- App failure-injection test `RtcVideoPublishHostCodecFallbackTests`: 1 passed.
- App build: 0 warnings, 0 errors.

Remaining review concern: the plan-listed `VideoPublisherCodecNegotiationTests.cs` does not exist in this checkout. Existing selection policy and SDP capability tests are in earlier task test files; this task adds focused transition, late-viewer, decoder-failure, signaling validation, and same-peer renegotiation tests. No full test suite was run.
