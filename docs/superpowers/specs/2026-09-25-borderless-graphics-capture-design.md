# Borderless Windows Graphics Capture Design

## Goal

Remove the Windows Graphics Capture colored border from monitor and window shares when the operating system supports the official borderless flow and the user grants access. Unsupported systems, denied access, unavailable package capability, or API errors must continue sharing with the normal border and report why borderless capture was skipped.

## Current context

- `GraphicsCaptureScreenSource` creates `GraphicsCaptureSession` for monitor capture.
- `GraphicsCaptureItemSource` is the common WGC session owner used by both monitor and window capture.
- `GraphicsCaptureWindowSource` wraps the common source and is already selected for window sharing.
- `RtcVideoPublishHost` is composed with the application logger factory; the diagnostics screen already reports runtime diagnostics, while logs are persisted by `FrameRelayLogging`.
- The app currently builds as an unpackaged Windows `WinExe`. Its `app.manifest` is a Win32 application manifest; the repository has no MSIX/AppX project or package manifest.

## Chosen approach

Request borderless access at the shared WGC session boundary, before starting the capture session, so monitor and window capture follow the same policy. Isolate runtime API detection and access requests behind a small injectable policy that reports a stable outcome (`granted`, `denied`, `unsupported`, or `request_failed`) and a diagnostic reason. On grant, set `GraphicsCaptureSession.IsBorderRequired` to `false`; on any non-granted result or failure while applying the property, leave the default border requirement in effect and continue normal capture.

Keep the Windows API details out of capture-selection, WebRTC, audio, and encoding layers. Log the selected border mode and fallback reason through the existing Microsoft.Extensions.Logging pipeline. Do not crop frames or draw an overlay.

## Runtime behavior

1. Start the normal WGC item, device, and frame pool setup.
2. Detect the borderless APIs at runtime using Windows API metadata, rather than assuming availability from the app's minimum target framework.
3. If available, call `GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless)` and await the result before `StartCapture`.
4. If access is granted, attempt to set `IsBorderRequired = false` before `StartCapture`.
5. If access is denied, the API is absent, access request throws, or setting the property is unavailable/fails, continue by starting the session with its default border. Emit a structured log event with the outcome and reason.
6. Cancellation remains cancellation: if the start token is cancelled while awaiting the permission request, do not start a capture session.

The system can still show a border if another application requires one for the same target; this is an operating-system constraint and does not fail sharing.

## Capability and packaging

The borderless access request requires `graphicsCaptureWithoutBorder` in an app package manifest. Microsoft documents that requirement for `RequestAccessAsync` and notes that `IsBorderRequired` is introduced at Windows build 20348. The current repository has no packaged build or package manifest, so there is no existing packaged target to update. Keep the current unpackaged build working; when a package manifest/build target exists, that package manifest must declare `graphicsCaptureWithoutBorder`. Do not put an AppX capability element into the existing Win32 `app.manifest`.

## Testability and diagnostics

- Unit-test the policy decision for granted, denied, unsupported API, request exception, and property-application failure without requiring Windows capture hardware or a consent prompt.
- Verify that all fallback outcomes permit the regular capture-start path and that diagnostics identify the outcome/reason.
- Keep existing WGC hardware tests as integration coverage; do not make automated tests depend on accepting the Windows consent dialog.
- Log one structured outcome per capture start, including monitor/window target kind, border mode, and fallback reason where applicable.

## Scope boundaries

- Apply policy to full-display and existing single-window WGC capture.
- Preserve frame dimensions and pixels exactly as returned by WGC.
- Do not change signaling, audio capture, WebRTC, encoder/decoder behavior, or bitrate/FPS adaptation.
- Do not add packaging infrastructure solely for this change; declare the capability in a package manifest when packaged builds are introduced.

## Acceptance criteria

- Supported Windows versions request `GraphicsCaptureAccessKind.Borderless` before starting a WGC session.
- Granted access results in `IsBorderRequired = false` for monitor and window capture.
- Unsupported APIs, denied permission, and runtime errors preserve normal bordered capture and produce a useful log reason.
- Cancellation during permission request does not start sharing.
- The current unpackaged app builds without requiring an AppX manifest; any future packaged build declares `graphicsCaptureWithoutBorder` in its package manifest.
- Existing streaming behavior, frame geometry, audio, and adaptive quality behavior remain unchanged.

## References

- [GraphicsCaptureSession.IsBorderRequired](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.graphicscapturesession.isborderrequired): documents the border behavior, access prerequisite, package capability, and build 20348 introduction.
- [GraphicsCaptureAccess.RequestAccessAsync](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.graphicscaptureaccess.requestaccessasync?view=winrt-28000): documents the asynchronous access request and package capability prerequisite.
- [App capability declarations](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/app-capability-declarations): explains capabilities are declared in the app package manifest.
