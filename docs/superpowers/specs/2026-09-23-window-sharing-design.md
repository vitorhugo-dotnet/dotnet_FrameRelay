# Monitor and Application Window Sharing Design

## Goal

Let a publisher select either a whole monitor or one top-level application window and send
only that visual target through FrameRelay's existing video publishing pipeline. Monitor shares
retain system loopback audio. Window shares capture audio from the selected window's owning
process tree, including child processes where Windows supports it. An unsupported process
loopback path must never fall back to system audio.

Success means the selected source is clear before the session starts, window resize does not
terminate capture, closing the selected window or exiting its process ends the share cleanly,
repeated sessions release native resources, and diagnostics identify source and audio modes
without recording window contents or audio data.

## Decisions

- Represent monitor and window as explicit capture targets and carry the selected target through
  the existing `SessionRuntime` and publisher host. Keep one publish, encode, and WebRTC pipeline.
- Refactor the Windows Graphics Capture lifecycle around a `GraphicsCaptureItem`. Retain monitor
  creation from `HMONITOR` and add HWND creation; both use the same D3D11 device, frame pool,
  BGRA readback, frame-rate throttling, cursor policy where supported, and cleanup path.
- Enumerate visible, valid top-level windows with non-empty titles and process names. Exclude
  FrameRelay's own window where its HWND is available. Refresh on demand; do not build a
  continuously running window-management service or attempt exhaustive shell/tool filtering.
- Show Monitor and Window source choices in Share. Show an identifiable selectable target list
  for the chosen source, preserve valid selection across refreshes, and make the active choice
  apparent before starting.
- Select system loopback for monitor sharing and process-tree loopback for window sharing. Use
  `ActivateAudioInterfaceAsync` with process-loopback activation parameters for the PID that owns
  the selected HWND. Include child processes using the Windows process-loopback mode.
- On systems where process-loopback is unsupported or cannot be initialized, allow the window's
  video share to continue without audio, report a clear audio-unavailable state in the Share UI
  and diagnostics, and send silence through the normal audio track if needed by the existing
  WebRTC contract. Never start system loopback for a window share.
- Bind window lifetime to its owner process and Graphics Capture item. If the HWND closes or its
  owner exits, stop video and process audio, end the session, and expose a useful closed-target
  status. A resize recreates the frame pool at the new content size and continues publishing.
- Log structured source kind, target title/process identity, dimensions, resize/stop reason, audio
  mode, PID/process identity, process-tree inclusion, and initialization/shutdown reason. Do not
  log captured content or private audio data.
- Keep window enumeration, target selection, window-to-item creation, and process audio
  activation behind testable contracts so lifecycle and selection tests do not need a real
  desktop capture session.

## Components and data flow

The Windows presentation layer enumerates monitors and eligible windows. The Share screen binds
to an explicit source kind and selected target. Starting a share passes a target value into
`SessionRuntime`, then the publisher host chooses the capture factory and audio factory from the
same target. The video capture source feeds `ScreenPublishPipeline`; its frames continue through
the existing encoder and WebRTC publisher. The selected audio capture source feeds the existing
audio publish pipeline and Opus encoder.

The media contract must no longer assume a monitor-only target. A target contains the stable
monitor identifier and metadata for monitor shares, or HWND, PID, title, and process name for
window shares. HWND values are process-local identifiers: validate them when starting, and use
the PID/process handle to guard against a destroyed/reused handle. Capture code creates the
appropriate `GraphicsCaptureItem` and reports target closure and dimensions without exposing
Win32 details to the session runtime.

The common capture lifecycle owns the D3D11 device/context, runtime Direct3D device, capture
item, frame pool, session, staging texture, reusable BGRA buffer, throttling state, and diagnostic
counters. A content-size change recreates the pool and staging resources as required, updates
dimensions, and drops only the incompatible transition frame. `GraphicsCaptureItem.Closed` or
owner-process exit raises a single terminal target-unavailable event. Stop is idempotent, clears
running state before disposal, detaches event handlers, and releases all native objects.

Process loopback is a separate `IAudioCaptureSource` implementation with an injectable activation
and capture boundary. It normalizes audio to the same 48 kHz stereo PCM frames as system loopback.
The source records the target PID, process identity, process-tree mode, and support/activation
status. Target exit stops it; normal silence remains silence. Monitor sessions continue to use
the existing WASAPI endpoint loopback implementation.

## Failure handling and compatibility

- Reject a stale or invalid HWND before session creation with a useful selection error.
- If a selected window closes or its process exits during a share, stop both media sources and
  close the session; do not leave the viewer on a frozen last frame.
- If process-loopback is unsupported (Windows build earlier than 20348) or activation fails,
  keep the video session alive with audio disabled/silent and clearly report why. Never substitute
  system loopback.
- If the process emits no audio, the process source produces silence; it must not inspect or mix
  unrelated render endpoints.
- On resize, update capture dimensions and let the existing encoder handle the next frame size
  according to its current behavior; if encoder reconfiguration is required, perform it without
  ending the publishing session.
- Preserve current monitor behavior, including monitor enumeration, cursor capture, FPS control,
  and system audio.
- Stop/Dispose can race capture callbacks and audio callbacks. Use idempotent teardown, detach
  callbacks before native disposal, and do not dispose a device while a callback uses it.

## Verification

- Presentation tests cover source-kind selection, selected monitor/window validation, visible
  selected-target state, refresh behavior, and closed-target messaging.
- Windows media tests cover eligible-window enumeration with fake Win32 enumeration inputs,
  invalid HWND rejection, window item creation routing, close notification, size-change pool
  recreation, frame throttling, and repeated start/stop cleanup.
- Audio tests cover process-loopback activation parameters, target PID and process-tree mode,
  output normalization, silence, process exit, unsupported-build handling, and the guarantee that
  window mode never creates a system-loopback source. Existing monitor loopback tests continue to
  cover monitor audio.
- Publisher/runtime tests cover source and audio selection, graceful target closure, clean startup
  failure, and switching from a window session to a later monitor session.
- Diagnostics tests cover monitor/window and system/process-loopback fields and ensure no media
  contents are logged.
- Run the focused tests for changed projects and the full solution test suite after implementation.
