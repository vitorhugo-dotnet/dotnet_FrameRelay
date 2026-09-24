# Issue #26 Share, Watch, and Diagnostics Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement issue #26 so Share, Watch, and Diagnostics expose useful live session state and working preview, audio, and fullscreen controls.

**Architecture:** Keep lifecycle and UI-safe metrics in the existing immutable `SessionSnapshot` flow. Derive measurements from the active media pipeline and RTC host, sample at a bounded cadence, and clear them when the session ends. Give source preview its own disposable capture lifetime; use the existing Watch host, audio sink, `VideoSurface`, and fullscreen shell state for viewer controls.

**Tech Stack:** .NET 10, C#, Avalonia, xUnit, Windows Graphics Capture, Media Foundation, SIPSorcery, NAudio/WASAPI.

**Spec:** `docs/superpowers/specs/2026-09-24-issue-26-share-watch-diagnostics-design.md`

## Global Constraints

- Preserve the existing Avalonia visual language, keep session state consistent across screens, and avoid unrelated architecture changes.
- Missing samples remain explicitly unavailable and render as `---`.
- The shell will sample/update at a UI-friendly cadence; it will not dispatch one UI update per media frame.
- The preview must not publish or create a server session.
- Fullscreen mode changes only the application presentation and must not renegotiate WebRTC.

## Review Focus

- A selected monitor/window changes or closes while preview capture is starting; dispose stale capture and keep errors local to preview.
- The session stops or changes role while a periodic metrics callback is queued; ignore stale values and release host subscriptions/timers.
- The user mutes after setting volume, moves the slider while muted, then unmutes; restore a valid non-zero level and keep mute visibly distinct.
- Fullscreen is exited with Escape, the return control, page navigation, or session stop; restore normal chrome without reconnecting or retaining fullscreen state.
- The app is resized below its normal layout breakpoints while controls and preview are visible; keep actions reachable and video aspect ratio intact.

---

### Task 1: Capture and project live session metrics

**Files:**
- Modify: `src/SonicDesktopRelay.Media/VideoReceiverStats.cs`
- Modify: `src/SonicDesktopRelay.Media/ScreenWatchPipeline.cs`
- Modify: `src/SonicDesktopRelay.Presentation/SessionSnapshot.cs`
- Modify: `src/SonicDesktopRelay.Presentation/SessionRuntime.cs`
- Modify: `src/SonicDesktopRelay.Presentation/MainWindowViewModel.cs`
- Modify: `src/SonicDesktopRelay.App/RtcVideoWatchHost.cs`
- Modify: `src/SonicDesktopRelay.App/RtcVideoPublishHost.cs`
- Modify: `src/SonicDesktopRelay.App/Shell.cs`
- Test: `tests/SonicDesktopRelay.Media.Tests/ScreenWatchPipelineTests.cs`
- Test: `tests/SonicDesktopRelay.Presentation.Tests/MainWindowViewModelTests.cs`
- Test: `tests/SonicDesktopRelay.Presentation.Tests/SessionRuntimeTests.cs`

**Interfaces:**
- Add a UI-safe `SessionMediaMetrics` record in Presentation with nullable width, height, measured bitrate, measured frame rate, latency/RTT, codec, and transport display fields. Do not expose RTC types from Presentation.
- Add nullable `Metrics` to `SessionSnapshot` and a guarded `SessionRuntime.UpdateMetrics(SessionMediaMetrics? metrics)` method that only applies samples to an active Sharing/Watching session and clears them on stop/role change.
- Add `double? VideoBitrateBitsPerSecond` to `VideoReceiverStats`; `ScreenWatchPipeline.TakeStatsSnapshot()` computes bitrate from encoded bytes received during its monotonic sample interval and yields no rate for an empty interval.
- Expose a current metrics snapshot from the watch host; calculate decoded FPS from `TakeStatsSnapshot()` and surface bitrate from encoded byte deltas. Keep RTT nullable unless a valid existing peer/RTCP measurement can be obtained without inventing one-way latency.

- [ ] **Step 1: Add focused failing tests** for empty sample intervals, measured byte rate and frame rate, metric formatting in `MainWindowViewModel`, and ignoring an update after `SessionPhase.Idle`.
- [ ] **Step 2: Run the targeted tests and confirm the new expectations fail** with the current missing metrics/projection.
- [ ] **Step 3: Implement the receiver-rate calculation and snapshot projection.** Track interval bytes with the same monotonic sampling gate used by `TakeStatsSnapshot`; add optional fields at the end of records/constructors to preserve call-site compatibility; clear metrics from every `SessionRuntime` stop and role transition path.
- [ ] **Step 4: Sample active hosts in `Shell` on the existing one-second diagnostics notification.** For the watcher, read frame/byte counters and the already selected transport/decoder; for publishing, use existing effective quality, encoder, video diagnostics, and transport diagnostics. Publish an immutable presentation DTO through `SessionRuntime.UpdateMetrics`; do not post frame-rate UI events.
- [ ] **Step 5: Run `dotnet test tests/SonicDesktopRelay.Media.Tests/SonicDesktopRelay.Media.Tests.csproj` and `dotnet test tests/SonicDesktopRelay.Presentation.Tests/SonicDesktopRelay.Presentation.Tests.csproj`; fix failures and confirm metric reset and stale-update cases pass.**
- [ ] **Step 6: Commit** as `feat: expose live session metrics`.

### Task 2: Show live stats and consolidate Share session controls

**Files:**
- Modify: `src/SonicDesktopRelay.App/Views/WatchView.axaml`
- Modify: `src/SonicDesktopRelay.App/Views/ShareView.axaml`
- Modify: `src/SonicDesktopRelay.App/Views/DiagnosticsView.axaml`
- Modify: `src/SonicDesktopRelay.Presentation/MainWindowViewModel.cs`
- Test: `tests/SonicDesktopRelay.Presentation.Tests/MainWindowViewModelTests.cs`

**Interfaces:**
- Consumes Task 1's `SessionSnapshot.Metrics` and formatted view-model metric properties.
- Adds no second mutable source of metrics in the UI.

- [ ] **Step 1: Add failing projection assertions** for sharing viewer count (`0` included), `---` when metrics are absent, and formatting valid resolution, bitrate, FPS, RTT, codec, and transport values.
- [ ] **Step 2: Run `dotnet test tests/SonicDesktopRelay.Presentation.Tests/SonicDesktopRelay.Presentation.Tests.csproj` and confirm those assertions fail.**
- [ ] **Step 3: Bind Watch connection stats and session details to the view-model values.** Show latency, bitrate, and FPS as individual rows; remove the disabled style from the card; display `---` per missing sample.
- [ ] **Step 4: Bind Diagnostics viewer count to the live snapshot and preserve an unavailable state only when no sharing count exists.**
- [ ] **Step 5: Move Start/Stop controls below the session-code panel in Share; remove the separate Connected viewers card, the duplicate controls under Display/video, and the disabled system-audio card.** Show compact audio mode/degraded status with session information and bind the publisher summary to the same snapshot metrics.
- [ ] **Step 6: Run the Presentation tests and inspect the three XAML views at their existing narrow/wide breakpoints; fix layout and binding failures.**
- [ ] **Step 7: Commit** as `feat: connect live metrics to session screens`.

### Task 3: Add playback mute and volume controls

**Files:**
- Modify: `src/SonicDesktopRelay.Media/IAudioSink.cs`
- Modify: `src/SonicDesktopRelay.Media.Windows/WasapiAudioSink.cs`
- Modify: `src/SonicDesktopRelay.App/RtcVideoWatchHost.cs`
- Modify: `src/SonicDesktopRelay.App/Shell.cs`
- Modify: `src/SonicDesktopRelay.App/Views/WatchView.axaml`
- Modify: `src/SonicDesktopRelay.App/Views/WatchView.axaml.cs`
- Test: `tests/SonicDesktopRelay.Media.Tests/AudioAbstractionsTests.cs`
- Test: `tests/SonicDesktopRelay.Media.Tests/AudioWatchPipelineTests.cs`
- Test: `tests/SonicDesktopRelay.Rtc.Tests/VideoSubscriberTests.cs`
- Test: `tests/SonicDesktopRelay.Media.Windows.Tests/WasapiAudioSinkTests.cs`

**Interfaces:**
- Extend `IAudioSink` with `float Volume { get; set; }` and `bool IsMuted { get; set; }` (or an equivalent atomic control API if the current playback session has no direct volume property); the WASAPI sink owns clamping and playback gain.
- Expose watch-host playback controls and Shell properties `PlaybackVolume`, `IsPlaybackMuted`, and `TogglePlaybackMute()`; defaults are full volume and unmuted.

- [ ] **Step 1: Add failing media tests** proving volume is clamped, mute silences output, and restoring mute returns to the last non-zero level; use an injected playback seam so tests do not require a physical endpoint.
- [ ] **Step 2: Run the focused Media and Media.Windows tests and confirm the control contract is missing.**
- [ ] **Step 3: Implement thread-safe volume/mute in `WasapiAudioSink` and its playback adapter.** Keep the existing PCM queue and playback lifetime; do not stop/recreate WASAPI for mute changes.
- [ ] **Step 4: Forward controls through `RtcVideoWatchHost` and Shell.** Ignore control changes when there is no active sink while retaining the UI defaults for the next watch session.
- [ ] **Step 5: Replace the Watch audio placeholder with a speaker button and 0–100 slider.** Bind slider and mute state; use separate visual states/tooltips for muted and unmuted.
- [ ] **Step 6: Run `dotnet test tests/SonicDesktopRelay.Media.Tests/SonicDesktopRelay.Media.Tests.csproj` and `dotnet test tests/SonicDesktopRelay.Media.Windows.Tests/SonicDesktopRelay.Media.Windows.Tests.csproj`; confirm mute/restore coverage passes.**
- [ ] **Step 7: Commit** as `feat: add watch playback volume controls`.

### Task 4: Complete application-level fullscreen viewing

**Files:**
- Modify: `src/SonicDesktopRelay.App/Views/MainWindow.axaml`
- Modify: `src/SonicDesktopRelay.App/Views/MainWindow.axaml.cs`
- Modify: `src/SonicDesktopRelay.App/Views/WatchView.axaml`
- Modify: `src/SonicDesktopRelay.App/Views/WatchView.axaml.cs`
- Modify: `src/SonicDesktopRelay.App/Shell.cs`
- Test: `tests/SonicDesktopRelay.Presentation.Tests/MainWindowViewModelTests.cs`

**Interfaces:**
- Consumes `Shell.IsVideoFullScreen`, playback controls from Task 3, and the existing `WatchView.Surface`.
- Add Shell commands `EnterVideoFullScreen()`, `ExitVideoFullScreen()`, and `ToggleVideoFullScreen()` with idempotent exit behavior.

- [ ] **Step 1: Add failing tests** for repeated enter/exit, Escape/return state clearing, and resetting fullscreen on session stop or navigation away.
- [ ] **Step 2: Run the focused state tests and confirm current fullscreen state does not satisfy lifecycle requirements.**
- [ ] **Step 3: Implement idempotent Shell state transitions and window key handling.** Escape exits; F11 toggles only while a watch session is active; leaving Watch or stopping the session exits fullscreen.
- [ ] **Step 4: Rework the Watch content grid so the existing video surface fills the content area in fullscreen and a small bottom overlay contains return, mute, and volume controls.** Hide navigation and all regular page/session content while fullscreen; do not replace/recreate the watch host.
- [ ] **Step 5: Run state tests and manually check fullscreen entry/exit during an active session, including Escape, return button, F11, navigation, and stop; verify there is no reconnect.**
- [ ] **Step 6: Commit** as `feat: complete watch fullscreen controls`.

### Task 5: Render a live preview for the selected source

**Files:**
- Create: `src/SonicDesktopRelay.App/SharePreviewController.cs`
- Modify: `src/SonicDesktopRelay.App/Shell.cs`
- Modify: `src/SonicDesktopRelay.App/RtcVideoPublishHost.cs` (reuse/extract the existing `PublisherCaptureSelection` video-source factory)
- Modify: `src/SonicDesktopRelay.App/Views/ShareView.axaml`
- Modify: `src/SonicDesktopRelay.App/Views/ShareView.axaml.cs`
- Test: `tests/SonicDesktopRelay.Media.Windows.Tests/PublisherCaptureSelectionTests.cs`
- Test: `tests/SonicDesktopRelay.Media.Windows.Tests/SharePreviewControllerTests.cs`

**Interfaces:**
- `SharePreviewController` consumes `CaptureTarget?`, a preview capture factory, and a frame callback; it owns at most one `IScreenCaptureSource`, cancels/replaces it on target change, and exposes `PreviewStatus` plus `FrameCaptured`. Keep the controller in App so it can use the existing Windows capture factory while tests use the current Windows/App test project.
- Preview uses an explicitly preview-sized `VideoQuality` and a one-slot/latest-frame UI handoff; it never creates `SessionRuntime` or a publish host.

- [ ] **Step 1: Add failing tests** for monitor/window source selection, target replacement disposing the old source, close/failure reporting, and bounded latest-frame delivery.
- [ ] **Step 2: Run focused capture/controller tests and confirm preview lifecycle behavior is absent.**
- [ ] **Step 3: Implement preview source ownership using the existing `PublisherCaptureSelection` source factory.** Subscribe before start, detach on stop/replace, stop and dispose on cancellation, and marshal only the latest frame to Avalonia.
- [ ] **Step 4: Bind selected target changes to the preview controller; show captured frames in an aspect-preserving preview surface.** Stop preview when the target is null, the selected target closes, or a public sharing session begins; restart when sharing ends if the Share view remains active.
- [ ] **Step 5: Keep preview errors out of `ShellError` and public session lifecycle; show an inline preview status and clean up on view detach/app shutdown.**
- [ ] **Step 6: Run capture/controller tests and verify selecting a monitor/window updates preview without starting a session; check cancel/rapid target change cleanup.**
- [ ] **Step 7: Commit** as `feat: preview selected share source`.

### Task 6: Final integration and regression verification

**Files:**
- Modify only files from Tasks 1–5 where integration fixes are required.
- Test: `SonicDesktopRelay.sln` projects touched by Tasks 1–5.

**Interfaces:**
- Consumes all prior task interfaces; no new cross-layer contracts.

- [ ] **Step 1: Review the full diff against every issue #26 acceptance criterion** and fix any missing binding, duplicated control, stale metric, or cleanup path.
- [ ] **Step 2: Run `dotnet test SonicDesktopRelay.sln` and `dotnet build SonicDesktopRelay.sln`; record exact results.**
- [ ] **Step 3: Launch the Avalonia app where available and inspect Share, Watch, and Diagnostics at minimum, normal, and maximum window widths; inspect active Watch fullscreen and preview aspect ratio.**
- [ ] **Step 4: Review `git diff --check`, ensure no temporary capture artifacts or unrelated files are staged, and commit any integration fixes** as `fix: integrate issue 26 session controls`.
