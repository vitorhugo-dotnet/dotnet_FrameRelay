# Task 4 implementation report: AV1 WebRTC and RTP

Base commit: `cc3c882b7a3d413d11acc9738b0b6a8ff83f48a3`.

## Changes

- Added optional AV1 offer and answer formats gated by caller-supplied AV1 codec capabilities, Main profile (`0`), and a positive supported level. H.264 remains registered and is the default when no capability is supplied.
- Kept audio tracks first in both peers' BUNDLE order.
- Exposed each completed peer's first compatible negotiated video codec. Publisher sends only samples tagged for the negotiated codec; viewer dispatches only the selected payload type to its matching H.264 or AV1 assembler.
- Added an AV1 RTP temporal-unit assembler with packet-count and retained-byte bounds, AV1 aggregation/OBU validation, bounded reorder handling, duplicate and sequence-gap rejection, and SIPSorcery 10.0.16 depacketization only after integrity checks.
- Viewer drops video received before local answer completion and emits codec-tagged `EncodedVideoSample` values after negotiation.
- Added production SDP tests for AV1/H.264 negotiation and H.264 fallback, plus AV1 assembler tests for one-packet and fragmented units, reorder, loss, malformed input, and retained-byte limits.

## Validation

Command:

`dotnet test tests\SonicDesktopRelay.Rtc.Tests\SonicDesktopRelay.Rtc.Tests.csproj --no-restore --filter "FullyQualifiedName~Av1RtpAccessUnitAssemblerTests|FullyQualifiedName~SipSorceryPeerConnectionTests|FullyQualifiedName~SipSorceryViewerPeerConnectionTests" --logger "console;verbosity=minimal"`

Result: Passed, 31/31 tests, 0 failed, 0 skipped. `git diff --check` passed.

## Boundary

AV1 remains disabled in the existing application composition unless a caller supplies validated local capabilities. The Media Foundation capability probe and codecs are Task 3 and are not implemented in this scoped change. No dependency changes were made.

## Review fix round 1

- Replaced the RTP `N` bit keyframe shortcut with AV1 OBU parsing. The assembler tracks `reduced_still_picture_header` from the sequence header's initial syntax bits and clears that state when an RTP coded-video-sequence start is signaled.
- Frame presence now comes only from OBU_FRAME_HEADER (3) or OBU_FRAME (6). Keyframe classification uses the reduced-header mode or, for normal mode, `show_existing_frame` followed by `frame_type`; absent sequence state is conservatively non-key.
- Added focused tests for sequence-header plus frame-header in one temporal unit, a keyframe in a later unit using cached sequence state, and a sequence-header-only temporal unit. Also covered a frame header with absent sequence state.
- Focused validation command:

  `dotnet test tests\SonicDesktopRelay.Rtc.Tests\SonicDesktopRelay.Rtc.Tests.csproj --no-restore --filter "FullyQualifiedName~Av1RtpAccessUnitAssemblerTests|FullyQualifiedName~SipSorceryPeerConnectionTests|FullyQualifiedName~SipSorceryViewerPeerConnectionTests" --logger "console;verbosity=minimal"`

  Result: Passed, 35/35 tests, 0 failed, 0 skipped.
