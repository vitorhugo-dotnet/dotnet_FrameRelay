# Adaptive Video Quality and Codec Fallback Design

## Goal

Reduce visible freezes in FrameRelay sessions across machines with different GPU vendors and
capabilities. Hardware H.264 transforms should be used when a compatible Media Foundation
transform is available; otherwise the session must work through the Windows software transform.
The publisher should automatically adapt the shared stream to sustained receiver/network pressure
and gradually recover toward the publisher-selected profile ceiling.

Success means the publisher responds to measured viewer packet/access-unit loss and decoder
throughput, changes bitrate/resolution/FPS without exceeding the selected profile, and can recover
the stream when conditions improve. The README and signaling protocol must explain the resulting
behavior and compatibility requirements.

## Decisions

- Keep one capture, one encoder and one shared quality target per publishing session. Any sustained
  problem reported by a viewer protects the slowest viewer by lowering quality for all viewers.
- Keep the current bitrate-first quality ladder. Reduce one rung at a time; bitrate drops before
  resolution, and the low rungs also reduce FPS. The selected height and FPS are ceilings. FPS may
  temporarily fall below the selection and must recover gradually.
- Evaluate rolling viewer feedback sent every 2 seconds. A viewer is degraded when its reported
  missing-packet/incomplete-access-unit ratio is at least 5%, or decoded FPS is below 85% of its
  effective target, sustained for at least 5 seconds. Require the existing 15-second minimum
  interval between quality changes.
- Consider a viewer stable when loss is at most 1% and decoded FPS is at least 95% of its effective
  target continuously for 30 seconds. Improve only one rung per recovery interval and never exceed
  the selected profile ceiling. Missing or stale viewer telemetry is unknown, not evidence of a
  healthy decoder; RTCP and publisher-local queue/send evidence remain usable independently.
- Send viewer telemetry over a new routed signaling message, `video.receiver_stats`, addressed to
  the authenticated publisher participant. The backend routes it only within the same session;
  the publisher associates it with the authenticated sender and the peer connection already
  assigned to that viewer. The payload contains bounded aggregate counters/ratios and decoded FPS,
  never media, SDP, ICE candidates, addresses or credentials.
- Keep generic Windows Media Foundation transform enumeration, hardware-first and software
  fallback, without vendor-specific NVIDIA assumptions. Record the selected transform and
  acceleration path. If the active transform fails at runtime, attempt the compatible alternative;
  reinitialize the stream at the current effective quality and request a clean keyframe. If neither
  path can be initialized, surface the existing media failure state rather than silently claiming
  hardware/software acceleration is active.
- Keep congestion adaptation separate from decoder recovery. RTCP loss alone updates adaptation
  evidence but does not request a keyframe. Actual PLI/FIR, incomplete access-unit recovery, or a
  quality reconfiguration may request one through existing coalescing/rate-limiting behavior.
- Document selection/fallback, feedback cadence, adaptation thresholds, profile ceilings and
  diagnostics in the FrameRelay README; document message shape, routing and validation in the
  backend protocol documentation.

## Interfaces and data flow

The viewer samples transport reception and decoded-frame progress over monotonic 2-second
intervals, computes bounded aggregate telemetry, and sends `video.receiver_stats` to the publisher
over the existing authenticated signaling connection. The backend adds this client message to its
routed-message allowlist and forwards it only to the requested participant in the same session.
The FrameRelay signaling client recognizes the message; `VideoPublisher` accepts it only from a
currently connected viewer and feeds its evidence to the session-scoped quality controller.

The payload should be versioned and small, with interval packet count/loss count, received and
incomplete access-unit counts, decoded-frame count, and effective FPS target. Reject malformed,
negative, non-finite, implausible, or oversized values. The server treats the body as opaque but
continues to enforce message-size, participant/session membership, and routed-message constraints.
Do not persist or log per-interval payloads. Existing RTCP reception reports and publisher-local
capture/encode/send queue diagnostics remain complementary signals.

## Failure handling and compatibility

- Ignore telemetry from unknown participants, stale peer connections, or invalid payloads; do not
  let one viewer report on another viewer's behalf.
- Missing telemetry must not be interpreted as zero loss or healthy decode. Existing RTCP-based
  adaptation remains available for clients that do not implement the new message.
- Receiver-side decode underperformance may lower the shared quality for all viewers by design.
- Keep diagnostic data privacy constraints: no media payload, SDP, raw ICE candidate, network
  endpoint, TURN credential, or user content is added to logs.
- Software transforms may be slower than hardware; fallback guarantees compatibility, not a fixed
  frame rate or quality on machines whose CPU cannot sustain the selected profile.

## Verification

- Unit tests cover generic hardware/software codec selection, no-hardware operation, candidate
  rejection, and runtime fallback/reinitialization behavior.
- Media tests cover 2-second telemetry aggregation, malformed/stale evidence, sustained-downshift
  thresholds, short-loss hysteresis, one-rung recovery, worst-viewer protection and profile caps.
- RTC/signaling tests cover viewer-to-publisher routing, sender/peer association, unauthorized or
  unassociated telemetry rejection, and no keyframe request from RTCP loss alone.
- Backend tests cover routed allowlisting, same-session authorization, opaque payload forwarding,
  and invalid/oversized envelope handling.
- Update the FrameRelay README and backend protocol documentation, then run the focused test
  projects and full solutions for both repositories where supported.
