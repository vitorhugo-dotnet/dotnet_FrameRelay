# SDD ledger — plan: docs/superpowers/plans/2026-09-18-stream-performance-quality-controls.md

Spec: `docs/superpowers/specs/2026-09-18-stream-performance-quality-controls-design.md`

Branch: `feat/stream-performance-quality-controls`  
PR: #18 — `feat: improve stream recovery and add quality controls`  
Checkpoint: 2026-09-19  
Checkpoint head before this progress commit: `b24d8acbc11d742412f060ecc308242b68bcd0fe`

## Execution state

This ledger is a versioned checkpoint of the Superpowers inline-execution state. It uses the
`executing-plans` ledger format, but it does **not** mark tasks as `complete` unless the
Superpowers completion contract has fresh passing verification for the current branch head.

The checkpoint head was **RED**:

- workflow: CI #234
- run id: `35387030033`
- Windows job: `105736217725`
- failure stage: `Build solution`
- resolved blocker:
  `EncoderKeyFramePolicy` is missing `ConsumeAfterRequiredReconfigure()`, required by
  `Required_reconfigure_consumes_a_pending_keyframe_without_forcing_a_second_reconfigure`
  in `MediaFoundationKeyFramePolicyTests`.
- compiler error: `CS1061`. The policy operation and encoder integration are now implemented.

A previous branch checkpoint, CI #226, passed restore, Release build, release publishing helper
tests, the .NET test suite, win-x64 runtime restore, and native-media publish-output verification.
That evidence predates later diagnostics/review fixes and therefore is not sufficient to declare
the current head complete.

## Pre-flight / interface ledger

- Tasks 1 -> 2: `VideoPublishProfile` is the shared contract from Media into Presentation/App.
  The selected profile is threaded through `SessionRuntime` and `IVideoPublishHost`.
- Tasks 2 -> 3: effective FPS is consumed by the capture source through the rate-update contract;
  capture is updated without restarting WGC.
- Tasks 2/3 -> 4: effective FPS/sample duration is carried into RTC so RTP cadence is not
  hardcoded to 30 FPS.
- Tasks 1/4 -> 5: encoder reconfiguration remains reserved for actual media configuration
  changes; recovery-only keyframe requests use the codec-control policy.
- Task 6 -> 7: RTC exposes endpoint-free selected-transport metadata; App/Diagnostics consumes
  only the safe classification DTO.
- Tasks 5/6 -> 7: encoder recovery state/timing and ICE path classification are projected into
  the existing Diagnostics surface without logging SDP, candidate bodies, endpoints or
  credentials.

## Task status

Task 1: implemented and verified GREEN
- Added publisher quality/FPS ceiling model.
- Adaptive recovery cannot intentionally rise above the selected profile.
- Added profile-focused quality adaptation coverage.

Task 2: implemented and verified GREEN
- Share UI exposes 1080p / 720p / 540p / 360p.
- Share UI exposes 15 / 30 / 60 FPS.
- Selected profile flows through Presentation -> publish host -> media pipeline.
- Controls are session-start settings rather than UI-only placeholders.

Task 3: implemented and verified GREEN
- Capture FPS can be updated without restarting Windows.Graphics.Capture.
- Effective adaptive FPS changes propagate to the capture throttle.
- The deferred bounded capture -> encode channel was not introduced.

Task 4: implemented and verified GREEN
- `EncodedVideoSample` carries explicit duration.
- RTC video timestamp increments derive from sample cadence instead of `90000 / 30`.
- Coverage exists for 15/30/60 FPS => 6000/3000/1500 RTP clock ticks.

Task 5: implementation present, **GREEN after blocker fix**
- Added narrow Windows `ICodecAPI` COM bridge for `CODECAPI_AVEncVideoForceKeyFrame`.
- Recovery-only keyframe requests use the codec-control fast path when supported.
- Unsupported codec control retains a diagnosed reconfigure fallback.
- Encoder exposes `codec-api` / `reconfigure-fallback` keyframe mode.
- Current regression test additionally requires a pending keyframe request to be consumed by an
  already-required quality/geometry reconfigure so the next input does not trigger a second,
  redundant reconfigure.
- Added `EncoderKeyFramePolicy.ConsumeAfterRequiredReconfigure()`.
- Required encoder reconfiguration now consumes the pending request, preventing a redundant
  codec-control attempt or second rebuild.

Task 6: implemented and verified GREEN
- Added metadata-only nominated ICE-pair diagnostics.
- Classifies Direct vs TURN and UDP vs TCP.
- Publisher and viewer forward selected transport state.
- DTO intentionally contains no candidate address, port, SDP, username or credential fields.

Task 6: Ruling: SIPSorcery 10.0.16 relay candidates report candidate protocol as UDP even when
the local TURN server transport is TCP. For a local relay candidate, use the associated local
`IceServer.Protocol` when available instead of trusting only `RTCIceCandidate.protocol`.
Reason: otherwise Diagnostics can falsely label TURN/TCP as TURN/UDP.
Cost if wrong: transport classification could misdiagnose head-of-line blocking and send future
network tuning in the wrong direction.

Task 7: implemented and verified GREEN
- Existing Diagnostics projection includes effective quality, keyframe mode, recovery latency,
  encode timing, RTC fan-out timing and selected transport classification.
- Viewer stalled wording now says the connection is alive while video is stalled.
- High-frequency timing is read/sampled rather than dispatching per-frame UI events.

Task 8: verification complete locally; external/manual evidence pending
- README updated.
- `docs/screen-publishing.md` updated.
- `docs/native-media-validation.md` updated.
- Scope scan confirmed the deferred capture -> encode `Channel` was not added.
- Scope scan confirmed production remains `ForceRelay=false` / P2P-first.
- Focused Windows media tests: 63 passed.
- Release solution build: 0 warnings, 0 errors.
- Release solution tests: 300 passed, 0 failed, 0 skipped.
- win-x64 runtime restore and portable/single-file native-media publishes passed.
- Publish outputs contain no legacy codec artifacts.
- Manual two-machine validation is not recorded as complete.
- Final whole-branch review is not recorded as complete.
- PR description still needs its implementation/verification evidence refreshed before Ready.

## Scope ledger

Implemented in this PR:
- P0: Media Foundation codec-control recovery keyframe fast path with explicit fallback.
- P0: nominated ICE transport diagnostics.
- P1 partial: recovery latency plus encode/send timing diagnostics.
- P2 partial: correct RTP cadence based on real sample duration.
- Publisher quality selector.
- Publisher FPS selector.
- Adaptive ceiling based on user selection.
- Runtime capture FPS update without restarting WGC.

Explicitly deferred:
- bounded latest-frame-wins capture -> encode `Channel`;
- queue-depth and full independent capture/convert/encode/send stage timings;
- TURN/UDP preference tuning;
- hardware decoder investigation/selection;
- GPU texture -> NV12 zero-copy;
- broader pacing/congestion-controller redesign.

Scope ruling: the bounded capture -> encode channel remains deferred because the WGC source reuses
its BGRA buffer. Queueing the current `VideoFrame` asynchronously without first defining buffer
ownership would allow capture to overwrite pixels while the encoder is still reading them.
Cost if wrong: keeping the synchronous path longer may preserve some avoidable capture-thread
backpressure, but introducing an unsafe queue now risks actual frame corruption.

## Next execution point

The local implementation and CI-equivalent verification are green. Remaining work is the
whole-branch review, manual two-machine validation, and refreshing PR #18's description with the
evidence before removing Draft. No merge is authorized by this ledger.

No merge is authorized by this ledger.

Final review: self-review (no subagent tool). No Critical, Important, or Minor findings.
