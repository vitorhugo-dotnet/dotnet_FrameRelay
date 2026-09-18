# H.264 Loss Recovery and Quality Adaptation Implementation Plan

**Goal:** Prevent known-incomplete H.264 RTP access units from reaching Media Foundation, bound recovery/keyframe feedback, and stop transient RTCP loss from cascading shared desktop quality from 1080p to unreadable resolutions.

**Base:** `main` @ `d1d957b8954f64f78dd276438590adadb8f2615a`

## Proven root causes

1. **SIPSorcery 10.0.16 H.264 depacketization does not validate RTP sequence continuity.**
   The exact `v10.0.16` `H264Depacketiser` accumulates payloads for one timestamp, sorts them by sequence number, then concatenates FU-A fragments without checking that adjacent sequence numbers are contiguous. A missing middle packet can therefore produce a truncated Annex-B access unit instead of a drop.

2. **Shared quality degrades one rung per poor RTCP report.**
   `VideoPublisher` forwards every loss report >= 5% to `ScreenPublishPipeline.ReportPoorReception()`, and that method immediately calls `Quality.Reduced()`. Rapid reports therefore deterministically cascade 1080p -> 720p -> 540p.

3. **Decoder null output is incorrectly treated as decode loss.**
   `ScreenWatchPipeline` currently turns the first null decoder result after receiving into a recovery keyframe request. Media Foundation can legitimately return no decoded frame while requesting more input, so this creates false recovery episodes and contributes to PLI/keyframe churn.

## Design

### RTP / H.264 integrity

- Enable SIPSorcery's supported video `RTPReorderBuffer` with a small named reorder window.
- Consume the low-level received video RTP packets for the trusted media path.
- Add a narrowly scoped `H264RtpAccessUnitAssembler` that:
  - groups packets by RTP timestamp,
  - handles 16-bit sequence wrap,
  - reorders defensively,
  - verifies contiguous sequence numbers,
  - validates FU-A start/middle/end structure,
  - validates packetization-mode=1 payload shapes,
  - drops incomplete/corrupt access units,
  - delegates byte-level Annex-B reconstruction to SIPSorcery's public `H264Depacketiser` only after integrity is proven.
- Feed only validated access units into `ScreenWatchPipeline` / Media Foundation.
- After an integrity drop, keep the last rendered frame, enter one recovery episode, send one PLI, suppress dependent access units, and resume on a clean IDR access unit.

### Recovery feedback

- Give viewer keyframe requests explicit reasons: initial connection, RTP loss, stall.
- Remove decode-null as a recovery trigger; retain null-decode diagnostics.
- Coalesce publisher-side encoder keyframe requests while one is pending and clear the pending state when a produced keyframe proves completion.
- Distinguish initial connection, received PLI/FIR, packet-loss recovery, and quality-change keyframe reasons in diagnostics.

### Shared quality adaptation

- Separate recovery feedback from congestion adaptation.
- Replace one-report-one-rung degradation with a deterministic stateful policy:
  - transient reports only accumulate evidence,
  - require consecutive poor reports over a minimum duration,
  - enforce a cooldown between quality changes,
  - reduce bitrate at the current resolution before reducing resolution,
  - require a sustained stable period before one-step recovery,
  - recover gradually toward the default quality.
- Keep one shared encode/capture pipeline; per-viewer simulcast/SVC remains out of scope.

### Diagnostics

Expose/log metadata-only counters/events for:
- RTP packets received, sequence gaps/missing packets, reordered packets,
- incomplete/corrupt AUs dropped and last drop metadata,
- recovery episodes, recovery keyframes, PLI sent/received,
- publisher keyframe requests/coalescing,
- quality degradation/recovery considered and applied with loss, counters, old/new resolution+bitrate, stable/cooldown timing.

No SDP, ICE/TURN credentials, encoded contents, or raw frames are logged.

## TDD / verification

1. Prove RED before production changes for RTP gap/FU-A validation and transient quality-loss behavior.
2. Implement smallest production changes to turn focused tests GREEN.
3. Run focused RTC/media tests.
4. Run fresh `dotnet restore`, Release build, full Release tests, repository CI/static checks.
5. Inspect complete diff and perform requesting-code-review; fix Critical/Important findings.
6. Open PR against `main`, inspect PR CI, and do not merge.

## Manual two-machine verification still required

Automated tests prove deterministic RTP integrity/recovery/adaptation state transitions. A real Windows publisher/viewer run is still required to validate native Media Foundation behavior, controlled loss/jitter, long-running readability, and whether the historical terminal `SEHException 0x80004005` persists independently once known-corrupt AUs are blocked.
