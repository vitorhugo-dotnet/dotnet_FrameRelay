# Task 3 report: Media Foundation AV1 probing and codecs

**Status: BLOCKED**

## Attempted

Read the Task 3 brief, approved design, implementation plan, H.264 encoder/decoder reference implementations, Media Foundation project dependencies, and current codec capability contracts. Confirmed the AV1 media subtype is the `AV01` FOURCC, represented by the standard Media Foundation subtype GUID `31305641-0000-0010-8000-00AA00389B71`. The pinned Vortice.MediaFoundation 3.8.3 package does not expose a named AV1 subtype constant.

No product code was changed. The H.264 encoder is 651 lines and uses output-before-input setup, an async MFT pump, keyframe control, and retry logic. The 901-line H.264 decoder has a different input-before-output setup, dynamic stream-change handling, output geometry/stride normalization, and sample ownership logic. An AV1 codec implementation cannot safely be produced by a mechanical subtype substitution: AV1 MFTs need their own validated profile/configuration and output behavior, and no AV1-capable Windows hardware/MFT is available here to establish those details.

The brief requires a probe to advertise only a transform that successfully activates and configures, plus usable hardware-only encoder and decoder implementations. Shipping a probe without codec paths would not meet the required outcome; guessing the AV1 transform contract and copying either H.264 pipeline would risk advertising unusable hardware and emitting samples with incorrect framing/metadata.

## Tests

No tests run. There is no implementation to validate, and the focused test project is Windows-targeted. Hardware-dependent integration behavior could not be established on this host.

## Files changed

- `.superpowers/sdd/2026-09-28-av1-codec-negotiation/task-3-report.md` (this report only)

## Self-review

This reports a blocker rather than claiming partial completion. No H.264 behavior, dependencies, RTC/session code, diagnostics, or PR metadata were changed.

## Blocker / needed context

Please provide access to a Windows environment with known AV1 encode and decode hardware transforms, or revise Task 3 into a smaller research/probe task with a specific supported Windows/MFT target and verified media-type/profile contract. Then the implementation can validate activation, media-type setup, encoded sample format, and BGRA decode output against the intended transform before completing the capability probe and codec classes.
