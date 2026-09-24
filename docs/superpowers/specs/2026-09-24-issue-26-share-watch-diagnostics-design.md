# Issue #26: Share, Watch, and Diagnostics UX Design

## Goal

Implement the requirements in GitHub issue #26: make the existing Share, Watch, and Diagnostics screens useful during real sessions by exposing current session/media information and controls. Preserve the existing Avalonia visual language, keep session state consistent across screens, and avoid unrelated architecture changes.

## Existing structure

- `SessionRuntime` owns lifecycle and publishes immutable `SessionSnapshot` values. Viewer count and signaling state already flow through this snapshot.
- `MainWindowViewModel` projects that snapshot to all pages.
- `Shell` composes the runtime and media hosts, marshals UI notifications, and already owns a partial viewer fullscreen flag.
- `RtcVideoPublishHost` and `RtcVideoWatchHost` expose media diagnostics, codec names, transport information, and frame events. The watch host already runs a one-second diagnostics/watchdog timer.
- `VideoSurface` presents a decoded stream while preserving aspect ratio.
- Share currently has source selection, a preview placeholder, separate sharing buttons, an audio placeholder, and unused session-information placeholders. Watch has a video surface, disabled audio/fullscreen placeholders, and disabled stats. Diagnostics already displays the shared session snapshot and host diagnostics.

## Design

### One source of session truth

Continue to use `SessionSnapshot` for lifecycle, signaling, viewer count, and session-level media values. Add only the compact, UI-safe fields needed to represent useful live metrics (for example resolution, codec, measured frame rate/bitrate, and RTT/latency when the relevant host has a valid sample). Keep transport-specific types inside App/RTC; project them to presentation-friendly values rather than introducing a Presentation dependency on RTC. Missing samples remain explicitly unavailable and render as `---`.

Hosts will expose or update the measurements they can already derive from their active pipeline and peer connection. The shell will sample/update at a UI-friendly cadence, reusing the watch host's diagnostics tick where practical; it will not dispatch one UI update per media frame. Clear samples when a session ends or changes role. Guard stale timer/event callbacks against a stopped or replaced host.

### Diagnostics and Watch

Diagnostics will show the snapshot viewer count while sharing and retain an unavailable marker only when no valid count exists. Watch's connection-stats card will bind to live latency/RTT, received bitrate, and decoded frame rate. Stats remain unavailable until there is a valid sample and reset when watching stops.

Expose volume and mute controls backed by the existing WASAPI audio sink. The slider updates playback volume; the speaker button toggles mute; retain the last non-zero level so unmuting restores it. Represent mute visually and provide the same control in normal and fullscreen viewing.

Complete the existing application-level fullscreen shell behavior: the remote video fills the FrameRelay client area, normal navigation and page content are hidden, and a small bottom overlay contains return, mute, and volume controls. Keep the same active watch host and `VideoSurface` session when toggling modes so returning does not reconnect. Escape and the return button leave fullscreen; F11 may continue to toggle it.

### Share

Make the preview follow the selected `CaptureTarget` before publishing starts. Use a preview capture lifetime separate from the publisher lifetime, stop/dispose the old preview when selection changes or the view/session no longer needs it, and present frames through a bounded UI handoff so slow rendering cannot accumulate unbounded work. Keep source aspect ratio and do not stretch. Preview errors should be local to the preview and must not start or disrupt a public session.

Move Start/Stop sharing directly below the session-code panel, show sharing state and viewer count there, and remove duplicate controls and the standalone Connected viewers card. Remove the non-actionable system-audio placeholder; show the already available audio mode/degraded state compactly in session/publisher information.

Expand the publisher connection summary using the same snapshot and existing publish-host diagnostics: session and signaling state, viewer count, resolution, frame rate, bitrate, codec, transport/selected ICE path, and RTT/latency when available. Avoid maintaining a separate UI-owned copy of session state.

## Error and lifecycle behavior

- Per-field unavailable values use `---`; a missing metric does not disable its whole card.
- Runtime/host stop, role changes, target changes, and view disposal release timers, preview capture, and event subscriptions.
- Event callbacks from retired sessions are ignored. UI-bound updates are marshalled through Avalonia's dispatcher.
- Preview or optional audio failures do not tear down an otherwise valid share/watch session; report actionable status in the relevant screen/Diagnostics.

## Verification

Add or update focused Presentation tests for snapshot projection, sample availability/reset, and rapid session transitions; add App-level tests where the current test seams allow reliable control behavior. Build the solution and run relevant automated tests after implementation. Manually inspect the Share, Watch, and Diagnostics layouts at narrow and wide window sizes if the environment supports launching the Avalonia app.

## Scope limits

No unrelated media, signaling, or backend refactors. No independent metrics pipeline. The preview must not publish or create a server session. Fullscreen mode changes only the application presentation and must not renegotiate WebRTC.
