# Task 1 report: Separate audio and video RTCP feedback

## Result

Implemented per-track RTCP reception report classification for the publisher peer. Each report now carries the reception block SSRC, media kind (`Audio`, `Video`, or `Unknown`), and fraction lost. The peer associates a report only when both SIPSorcery's media-stream identity and the reception block SSRC match one of its negotiated audio/video sender tracks. Mismatches and unrecognized SSRCs remain `Unknown`.

`VideoPublisher` sends only `Video` reports to the screen quality controller, forwards `Audio` reports through an audio-diagnostics event, and drops `Unknown` reports from both paths. RTCP PLI/FIR keyframe requests are restricted to the video media stream.

## Files changed

- `src/SonicDesktopRelay.Rtc/IPeerConnection.cs`
- `src/SonicDesktopRelay.Rtc/SipSorceryPeerConnection.cs`
- `src/SonicDesktopRelay.Rtc/VideoPublisher.cs`
- `tests/SonicDesktopRelay.Rtc.Tests/SipSorceryPeerConnectionTests.cs`
- `tests/SonicDesktopRelay.Rtc.Tests/VideoPublisherPacketLossTests.cs`
- `tests/SonicDesktopRelay.Rtc.Tests/VideoPublisherTests.cs`

## Verification

Focused command:

`dotnet test tests/SonicDesktopRelay.Rtc.Tests/SonicDesktopRelay.Rtc.Tests.csproj --no-restore --filter 'FullyQualifiedName~SipSorceryPeerConnectionTests|FullyQualifiedName~VideoPublisherPacketLossTests|FullyQualifiedName~VideoPublisherTests'`

Passed: 45; failed: 0; skipped: 0. Includes matching/mismatching media+SSRC classification cases and a publisher test proving audio and unknown reports do not affect video quality while video reports still can.

## Notes

The focused test project required package restore because its `obj/project.assets.json` was absent. No full suite was run. The approved plan/spec files were left untouched.

## Round 1 review follow-up

Strengthened `Only_video_rtcp_reception_reports_reach_the_screen_quality_controller` to send three high-loss audio reports and three high-loss unknown-SSRC reports, spaced across the quality controller's sampling intervals. It now asserts the complete `VideoQuality` record (resolution, FPS, and bitrate) is identical to its initial value before any video report is sent. It also confirms audio diagnostics received all three audio reports. Existing subsequent video reports still prove video feedback can lower bitrate.

Focused verification command:

`dotnet test tests/SonicDesktopRelay.Rtc.Tests/SonicDesktopRelay.Rtc.Tests.csproj --no-restore --filter 'FullyQualifiedName~Only_video_rtcp_reception_reports_reach_the_screen_quality_controller'`

Passed: 1; failed: 0; skipped: 0. Production files were unchanged for this follow-up.
