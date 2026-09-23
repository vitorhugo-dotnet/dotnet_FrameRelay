# Issue #8: Responsive FrameRelay Avalonia UI

## Goal

Refactor the existing Avalonia desktop UI to follow the Share, Watch, Diagnostics, and Settings FrameRelay prototypes supplied with GitHub issue #8, while retaining current working behavior. The result must adapt to window resizing and must not make unsupported prototype features appear functional.

## Scope

- Rework the main window shell and navigation, including FrameRelay branding and globally useful connection/device status.
- Rework Share, Watch, Diagnostics, and Settings page composition to match the supplied visual hierarchy and information density.
- Extend shared visual tokens and styles for the dark palette, status states, spacing, typography, cards, and unavailable features.
- Use responsive Avalonia layouts so cards and columns reflow at narrow widths, main content avoids horizontal scrolling, and preview/video surfaces preserve aspect ratio.
- Keep the existing Home page out of the primary product flow unless a useful role is identified during implementation.
- Keep legacy project, namespace, and storage identifiers unchanged.

The scope is one cohesive UI project because the shell, shared styles, and four pages jointly establish the same visual language and responsive behavior. It does not include media, signaling, backend, or runtime redesign.

## Design choices

The recommended approach is a presentation-only refactor over the existing `Shell` and `MainWindowViewModel` bindings. Shared styles and small reusable controls should carry repeated card/status/unavailable treatments. Avalonia layout primitives should do the reflow work; code-behind should not accumulate per-page width checks.

Two alternatives were considered:

1. Rebuild the navigation/runtime state model while replacing the UI. This adds behavioral risk and is unnecessary for the visual requirements.
2. Match the screenshots with fixed dimensions. This cannot satisfy resize/reflow requirements and would clip content at smaller sizes.

## Structure and behavior

### Shell and navigation

`MainWindow` remains the page host and uses the current page binding. The shell presents FrameRelay branding, Share/Watch/Diagnostics/Settings navigation, an active-page accent, and relevant connection/device status. Navigation can compact at narrower widths. The old Home entry should not compete with the four primary pages. The existing `IsVideoFullScreen` behavior must continue to hide shell chrome and restore it on exit.

### Page composition

- **Share:** provide a display preview area, monitor selection, existing quality/frame-rate controls, session code and copy action, session state, viewer/connection summary, audio control affordance, and primary sharing actions. Existing supported actions remain bound to the current shell/runtime state.
- **Watch:** provide session-code entry, join/leave actions, the existing `VideoSurface`, session/connection details, and supported fit/fullscreen/audio controls. F11/Esc behavior and video aspect ratio remain intact.
- **Diagnostics:** keep real media, session, signaling, and WebRTC diagnostic data available in a scannable layout. Preserve the existing metadata-only privacy boundary; do not add raw SDP, ICE candidate contents, credentials, or signaling payloads.
- **Settings:** keep backend address validation/persistence and device-name behavior functional. Any additional prototype-only settings remain visibly unavailable.

### Responsive layout and unavailable features

Wide layouts may use multiple columns and a persistent navigation rail. At narrower sizes, panels reflow and navigation may compact. At very small supported desktop sizes, vertical scrolling is acceptable, but the main page should not need horizontal scrolling. Resizing in place must update the layout.

Unsupported actions/cards remain in the prototype's intended location but are visibly disabled, darker and desaturated, non-focusable/non-clickable, and do not alter state. A shared style/state provides this treatment. Unsupported telemetry displays `---`; no values are fabricated.

### Data flow and errors

Existing controls continue to bind to `Shell`, `MainWindowViewModel`, runtime snapshots, monitor enumeration, and current diagnostics collections. UI-only composition may add presentation projections when required, but must not introduce fake backend/runtime state. Current validation and actionable errors remain visible near their controls or in the shell.

## Likely files

- `src/SonicDesktopRelay.App/Views/MainWindow.axaml` and `.axaml.cs`
- `src/SonicDesktopRelay.App/Views/ShareView.axaml` and `.axaml.cs`
- `src/SonicDesktopRelay.App/Views/WatchView.axaml` and `.axaml.cs`
- `src/SonicDesktopRelay.App/Views/DiagnosticsView.axaml` and `.axaml.cs`
- `src/SonicDesktopRelay.App/Views/SettingsView.axaml` and `.axaml.cs`
- `src/SonicDesktopRelay.App/Styles/Tokens.axaml` and any focused shared styles/controls
- Presentation/UI tests where existing test seams support them

The implementation may adjust this list to follow existing project patterns, but must stay within the UI/presentation boundary described above.

## Verification and acceptance

- Build and run relevant test projects, including presentation tests and solution-level checks available on the host.
- Add or update tests for reusable unavailable-state behavior and presentation state where practical.
- Manually inspect all four pages at wide and narrow window sizes and resize while running; verify no important action/value is clipped and no normal layout requires horizontal scrolling.
- Verify Watch fullscreen (F11/Esc), video sizing/aspect ratio, Share actions, Settings persistence/validation, and Diagnostics content/privacy.
- Compare layout hierarchy, spacing, cards, active navigation, statuses, and information density against the supplied issue screenshots and Stitch export.

## Out of scope

- WebRTC, signaling, media pipeline, or backend architecture changes.
- New APIs or product functionality solely to populate prototype cards.
- Invented metrics or simulated connected viewers.
- Unrelated project/namespace/storage renames.
