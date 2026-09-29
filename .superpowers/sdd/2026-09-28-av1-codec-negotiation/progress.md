# SDD ledger — plan: docs/superpowers/plans/2026-09-28-av1-codec-negotiation.md

Setup: workspace created in PowerShell because the provided Bash scripts cannot resolve the Windows .git worktree pointer from WSL. Task briefs will be copied from the approved plan into this plan-scoped workspace.

Pre-flight: Task 1 produces verified SIPSorcery AV1 APIs consumed by Task 4; confirm before implementing RTC paths.
Pre-flight: Task 2 produces VideoCodec, capability, constraints, selection, and sample tag contracts consumed by Tasks 3-6; keep names/signatures identical.
Pre-flight: Task 3 produces AV1 encoder/decoder/probe consumed by Task 5; use existing IVideoEncoder/IVideoDecoder boundaries.
Pre-flight: Task 4 produces codec-aware peer negotiation consumed by Task 5; peer factories and answer results must expose the selected codec.
Pre-flight: Task 5 produces the shared-session selection/fallback state consumed by Task 6; diagnostics must read active instances and stable reasons.
Pre-flight: Task 6 produces sampled codec/timing metrics consumed by Task 7; preserve watchdog cadence.

Ruling: Run only targeted tests named by each task — issue #22 explicitly requires automated coverage, so its implementation includes targeted validation; do not run full suites. Cost if wrong: unnecessary test execution time.
Ruling: Correct the spec's SIPSorcery compatibility statement — NuGet release notes say 10.0.11 added AV1 support and 10.0.16 includes those release notes; retaining the pinned dependency avoids an unneeded update. Cost if wrong: an API limitation could block a later task, in which case stop and revise the plan.

Task 1: complete
Checkpoint: 10.0.16 AV1 capability confirmed from the 10.0.11 release notes; no SIPSorcery update needed. Upstream `AV1Depacketiser` lacks a sequence-gap check, so Task 4 retains our RTP integrity wrapper.
Task 1: complete (commits 5093e6f..HEAD, tests: dotnet test tests/SonicDesktopRelay.Rtc.Tests/SonicDesktopRelay.Rtc.Tests.csproj --filter AV1-negotiation-cases → 3/3 pass).

Task 2 Ruling: keep AV1 encode and decode constraints in separate dictionaries because one peer may expose different encoder and decoder profile/level limits; cost if wrong: slightly more contract surface for a peer using both paths.

Task 2: complete (focused policy tests: 7/7 pass; focused media contract tests: 11/11 pass). Added codec-neutral capability/selection contracts and H.264-default sample tagging. Software-only capability filtering is delegated to Task 3's hardware transform probe because this pure policy consumes already-classified capability sets.

Ruling: Delegate Task 3 as one bounded Windows-media task because its probe and codec classes share Media Foundation activation/configuration mechanics; isolated review follows before Task 4. Cost if wrong: task size may challenge one worker, but the brief explicitly allows DONE_WITH_CONCERNS/BLOCKED instead of speculative code.

Ruling: Retry Task 3 because the prior BLOCKED report assumed no likely hardware validation, but the actual host is Windows 11 build 26200 with an RTX 5060; adapter identity remains context only, not capability evidence. Enumerate/activate/configure actual MFTs and skip hardware integration only if none qualify. Cost if wrong: a retry may still stop at a documented candidate-type ambiguity, but avoids misclassifying the available environment.

Task 4 review finding: Important — AV1 assembler equated RTP N/new-sequence with keyframe and marked every TU as containing a frame. Ruling: parse OBU_FRAME_HEADER/OBU_FRAME and the needed cached reduced-still-picture-header state to determine frame presence and KEY_FRAME conservatively; unknown/missing state means non-key. Cost if wrong: false recovery classification; conservative false negatives can delay delivery until a parseable keyframe, while false positives risk corrupted decode.

Task 4 fix round 1: previous Important finding ADDRESSED; scoped re-review clean (commit 502badf..8a919ff). Parser conservatively reads only AV1 syntax prefix needed for frame presence and KEY_FRAME; downstream decoder handles full bitstream validity. Cost if wrong: malformed syntax beyond parsed header may survive RTP assembly, but complete frame payload reaches codec decoder for validation.
Task 4: complete (commits cc3c882..8a919ff, review clean; focused RTC tests 35/35).

Task 2 review: approved; deferred Minor coverage gaps are publisher level shortfall and viewer profile mismatch tests. Existing selection branches are symmetric; add these if convenient during final task review. Task 2 complete (commits 3fce9a2..4728291, no Critical/Important findings; focused policy 7/7 and contract 11/11 passed).

Task 3 review findings: Important — decoder candidate activation was not paired with configuration selection/fallback, risking an advertised but unusable decoder; Important — lagged MFT output used the current input timestamp rather than matching output/submission metadata; Minor — encoder IMFAttributes COM interface was not disposed. Fix round 1 dispatched. No adjudication yet.

Task 3 review fix round 1: decoder candidate configuration/fallback ADDRESSED; encoder COM attributes disposal ADDRESSED; encoder delayed-output timestamps NOT ADDRESSED because the first fix had added decoder timestamp handling only. Cost if missed: delayed AV1 output could be tagged with a newer frame's timestamp.
Task 3 fix round 2: encoder stores bounded submission timing, uses output MFT timestamp to match submitted frames, and falls back to FIFO only when output lacks a timestamp. Focused tests pass 13, 1 expected hardware-decoder skip; Windows Media build is clean. Scoped re-review pending. Cost if wrong: reordered or timestamp-less output may still be mismatched when the encoder's ordering/timestamps differ from the assumptions; timestamps from the MFT are preserved where present.
Task 3 fix round 2: encoder timing issue ADDRESSED; scoped re-review clean at 846f7f1..HEAD. Encoder output timestamp is matched to submitted sample timing; omitted MFT timestamps use bounded FIFO; tests cover delay, fallback, and unmatched PTS. Task 3 complete (Windows Media build 0 warnings/errors; focused AV1/tracker tests 13 pass, 1 expected skip).

Task 5 implementation: added shared H.264 fallback, same-peer SIPSorcery renegotiation, queued-sample reset/keyframe recovery, viewer AV1 decoder-failure signaling, and encoder-init fallback. API evidence and focused validation are recorded in task-5-report.md. RTC focused tests 63/63; App encoder-failure injection 1/1; App build 0 warnings/errors. Full suites not run.
