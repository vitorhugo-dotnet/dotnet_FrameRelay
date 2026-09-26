# Monitor and Application Window Sharing Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let publishers share one monitor or one application window, with source-matched audio and graceful target lifecycle handling.

**Architecture:** Carry a typed capture target from Share through `SessionRuntime` to the existing publishing host. Reuse one Windows Graphics Capture/D3D11 frame lifecycle for monitor and HWND items, and choose system or process-loopback audio from the same target. Preserve the existing encode, Opus, and WebRTC pipelines.

**Tech Stack:** .NET 10, Avalonia, Windows.Graphics.Capture, Win32 interop, Vortice D3D11, WASAPI, xUnit.

**Spec:** `docs/superpowers/specs/2026-09-23-window-sharing-design.md`

## Global Constraints

- Keep Windows.Graphics.Capture support at Windows 10 build 19041 or later.
- Process-loopback audio requires Windows 10 build 20348 or later.
- Window sharing must never fall back to system loopback audio.
- Monitor sharing keeps its current monitor capture and system-loopback behavior.
- Reuse one video encode/WebRTC pipeline; do not create a parallel media stack.
- Do not log media contents or audio samples.
- Closing the selected window or exiting its process must stop capture and end the session cleanly.
- Resize the capture pool and continue the session.

## Review Focus

- An HWND closes and is quickly reused (including while startup is still preparing): validate it against its owner PID/process identity before start and during capture; prevent a late startup continuation from publishing a stale Sharing state.
- The owner process exits while capture/audio callbacks are in flight: stop both sources once without disposing callback-owned resources early.
- Process-loopback activation is unavailable or fails: continue video-only and prove no system-loopback factory is called.
- A selected process is silent or returns silent WASAPI buffers: emit silence without mixing another endpoint.
- A window changes dimensions repeatedly: recreate pool/staging resources, keep timestamps/FPS throttling valid, and continue encoding.

---

## File Structure

- `src/SonicDesktopRelay.Media/VideoContracts.cs`: add `CaptureTarget` variants and `WindowInfo` metadata (including process creation time) alongside `MonitorInfo`.
- `src/SonicDesktopRelay.Media/IScreenCaptureSource.cs`: generalize the capture source start contract to accept `CaptureTarget`; add `IWindowEnumerator` and a target-closed event.
- `src/SonicDesktopRelay.Media/ScreenPublishPipeline.cs`: start capture with typed targets and derive output diagnostics from current source dimensions.
- `src/SonicDesktopRelay.Presentation/SessionSnapshot.cs`: update `IVideoPublishHost` to accept capture targets and report target closure.
- `src/SonicDesktopRelay.Presentation/SessionRuntime.cs`: pass capture targets into publishing and cleanly end a session on target closure.
- `src/SonicDesktopRelay.Media.Windows/WindowEnumerator.cs`: enumerate valid, visible, titled top-level windows through an injectable Win32 boundary; exclude tool windows and shell/FrameRelay windows.
- `src/SonicDesktopRelay.Media.Windows/CaptureInterop.cs`: add HWND GraphicsCaptureItem creation and window metadata APIs, retaining monitor interop.
- `src/SonicDesktopRelay.Media.Windows/GraphicsCaptureItemSource.cs`: own the shared WGC/D3D11 lifecycle, resize, FPS throttling, frame conversion, diagnostics and native cleanup.
- `src/SonicDesktopRelay.Media.Windows/GraphicsCaptureScreenSource.cs`: preserve monitor-specific adapter behavior while delegating common work to the shared source.
- `src/SonicDesktopRelay.Media.Windows/GraphicsCaptureWindowSource.cs`: validate/start an HWND target and translate item/process closure into target-unavailable notification.
- `src/SonicDesktopRelay.Media.Windows/ProcessLoopbackAudioSource.cs`: implement process-tree WASAPI capture through `IAudioCaptureSource`, with injectable activation/capture seams.
- `src/SonicDesktopRelay.Media.Windows/ProcessLoopbackInterop.cs`: isolate native activation parameters, build support checks and COM callbacks.
- `src/SonicDesktopRelay.App/RtcVideoPublishHost.cs`: select capture/audio implementations from the target and expose audio/source diagnostics.
- `src/SonicDesktopRelay.App/AppComposition.cs`: compose the window enumerator.
- `src/SonicDesktopRelay.App/Shell.cs`: bind source kind, refresh windows, validate selections, start typed targets and surface target/audio state.
- `src/SonicDesktopRelay.Presentation/MainWindowViewModel.cs`: map the `capture_target_closed` failure code to a clear Share status message.
- `src/SonicDesktopRelay.App/Views/ShareView.axaml`: add Monitor/Window choice, window list/refresh, selected-target state and audio-unavailable message.
- `src/SonicDesktopRelay.App/Views/ShareView.axaml.cs`: handle refresh interaction if a command binding is not already used by the view.
- Tests in `tests/SonicDesktopRelay.Media.Windows.Tests` (which already references the App project) and `tests/SonicDesktopRelay.Presentation.Tests`.
- `README.md` and `docs/screen-publishing.md`: document source selection, process audio requirements, unsupported-build behavior and diagnostics.

## Task 1: Define Capture Targets and Update Publishing Contracts

**Files:**
- Modify: `src/SonicDesktopRelay.Media/VideoContracts.cs`
- Modify: `src/SonicDesktopRelay.Media/IScreenCaptureSource.cs`
- Modify: `src/SonicDesktopRelay.Media/ScreenPublishPipeline.cs`
- Modify: `src/SonicDesktopRelay.Presentation/SessionSnapshot.cs`
- Modify: `src/SonicDesktopRelay.Presentation/SessionRuntime.cs`
- Modify: `src/SonicDesktopRelay.App/RtcVideoPublishHost.cs` for the typed host bridge and source-close forwarding
- Modify: `tests/SonicDesktopRelay.Media.Tests/VideoContractsTests.cs`
- Modify: `tests/SonicDesktopRelay.Presentation.Tests/SessionRuntimeTests.cs`
- Modify: `tests/SonicDesktopRelay.Presentation.Tests/SignalingDiagnosticsTests.cs`

**Interfaces:**
- Produce `public abstract record CaptureTarget` with nested `Monitor(MonitorInfo Info)` and `Window(WindowInfo Info)` records.
- Produce `public sealed record WindowInfo(nint Handle, uint ProcessId, DateTime ProcessStartTimeUtc, string Title, string ProcessName, int Width, int Height)`.
- Produce `public interface IWindowEnumerator { IReadOnlyList<WindowInfo> List(); }`.
- Add `event Action<string>? TargetClosed` to the capture-source contract; monitor sources never raise it.
- Change capture start to `Task StartAsync(CaptureTarget target, VideoQuality quality, CancellationToken ct)` and expose current captured width/height for diagnostics and quality scaling.
- Change `IVideoPublishHost.StartAsync` to `Task StartAsync(CaptureTarget target, VideoPublishProfile profile, CancellationToken ct)`.
- Add `event Action<string>? CaptureTargetClosed` to `IVideoPublishHost`; its payload is a user-safe reason, not native handles or content.
- Add `SessionRuntime.StartSharingAsync(CaptureTarget target, VideoPublishProfile profile, int maxViewers, CancellationToken ct)`; keep monitor overloads as forwarding compatibility helpers for current call sites/tests.
- Change `ScreenPublishPipeline.StartAsync` to accept `CaptureTarget`; calculate diagnostics dimensions from the capture source's current dimensions rather than assuming a `MonitorInfo`.

- [ ] **Step 1: Add target contract tests**

Add tests asserting monitor and window target variants preserve their supplied metadata and that window metadata carries title, process name, PID and HWND without adding UI or Win32 behavior to the media abstractions.

```csharp
var target = new CaptureTarget.Window(windowInfo);
Assert.Equal(windowInfo, Assert.IsType<CaptureTarget.Window>(target).Info);
```

- [ ] **Step 2: Run the focused media contract tests**

Run: `dotnet test tests/SonicDesktopRelay.Media.Tests/SonicDesktopRelay.Media.Tests.csproj --filter FullyQualifiedName~VideoContractsTests`
Expected: the new target tests fail to compile until the target records exist.

- [ ] **Step 3: Add target records and enumerator/capture contracts**

Implement the signatures listed above. Keep `MonitorInfo` unchanged; use the target wrapper rather than adding window-only nullable fields to it. Use this closed set of variants so callers cannot construct a target with both or neither source:

```csharp
public abstract record CaptureTarget
{
    public sealed record Monitor(MonitorInfo Info) : CaptureTarget;
    public sealed record Window(WindowInfo Info) : CaptureTarget;
}
```

- [ ] **Step 4: Thread the typed target through the presentation runtime**

Update the publisher interface, runtime overload and fake publisher implementations. On publisher start, pass the exact selected target. Subscribe to `CaptureTargetClosed` before starting media; on notification, coordinate with the in-flight start so a late continuation cannot publish Sharing, stop capture, end the owned backend session, detach signaling, and publish `capture_target_closed` for the Share UI to explain.

```csharp
if (_captureTargetClosedDuringStart)
{
    await EndOwnedSessionAsync(created.SessionId, ct);
    await FailAsync("capture_target_closed");
    return;
}
```

- [ ] **Step 5: Verify and commit the contract slice**

Run: `dotnet test tests/SonicDesktopRelay.Media.Tests/SonicDesktopRelay.Media.Tests.csproj`
Run: `dotnet test tests/SonicDesktopRelay.Presentation.Tests/SonicDesktopRelay.Presentation.Tests.csproj`
Expected: all current and new tests pass; legacy monitor calls still select the monitor target.

Commit: `feat(media): add typed monitor and window capture targets`

## Task 2: Enumerate Eligible Windows and Create HWND Capture Items

**Files:**
- Create: `src/SonicDesktopRelay.Media.Windows/WindowEnumerator.cs`
- Modify: `src/SonicDesktopRelay.Media.Windows/CaptureInterop.cs`
- Modify: `tests/SonicDesktopRelay.Media.Windows.Tests/MonitorEnumeratorTests.cs`
- Create: `tests/SonicDesktopRelay.Media.Windows.Tests/WindowEnumeratorTests.cs`

**Interfaces:**
- Consume `WindowInfo` and `IWindowEnumerator` from Task 1.
- Produce `WindowEnumerator : IWindowEnumerator` with constructor `WindowEnumerator(IWindowApi windowApi, uint excludedProcessId)` and an internal injectable `IWindowApi` for `EnumWindows`, `IsWindow`, `IsWindowVisible`, `GetWindowText`, `GetWindowThreadProcessId`, `GetWindowRect`, tool/shell filtering and process identity lookup.
- Produce `CaptureInterop.CreateItemForWindow(nint hwnd)` using `IGraphicsCaptureItemInterop.CreateForWindow` at vtable slot 3; retain existing monitor slot 4 behavior.
- Produce injectable `IGraphicsCaptureItemFactory.CreateForMonitor(MonitorInfo monitor)` and `CreateForWindow(WindowInfo window)` adapters over those interop methods.

- [ ] **Step 1: Add fake-window API tests**

Test that enumeration includes visible, valid windows with non-empty titles; excludes invisible, invalid, untitled, FrameRelay-owned, and process-name-unavailable windows; captures process creation time; and tolerates a window disappearing between enumeration and metadata lookup.

```csharp
var windows = new WindowEnumerator(fakeApi, currentProcessId: 42).List();
Assert.Equal([visibleWindow], windows);
```

- [ ] **Step 2: Run the focused Windows media tests**

Run: `dotnet test tests/SonicDesktopRelay.Media.Windows.Tests/SonicDesktopRelay.Media.Windows.Tests.csproj --filter FullyQualifiedName~WindowEnumeratorTests`
Expected: FAIL because the enumerator/API seam is not implemented.

- [ ] **Step 3: Implement enumeration and metadata filtering**

Keep Win32 P/Invoke in the Windows project. For every HWND, pair `GetWindowThreadProcessId` with a process handle and its start time; discard the entry if the HWND/PID/process identity changes during lookup. Return a stable snapshot list and do not add polling, icon extraction, shell heuristics, or a persistent window registry.

- [ ] **Step 4: Add HWND item-creation coverage and implement interop**

Use an injectable item factory in tests to assert a window target routes to window creation and a monitor target still resolves `HMONITOR`. Isolate the unsafe COM call in `CaptureInterop` and release the returned ABI pointer on every path:

```csharp
var item = CaptureInterop.CreateItemForWindow(window.Handle);
Assert.Equal(window.Handle, fakeItemFactory.LastWindowHandle);
```

- [ ] **Step 5: Run and commit**

Run: `dotnet test tests/SonicDesktopRelay.Media.Windows.Tests/SonicDesktopRelay.Media.Windows.Tests.csproj --filter FullyQualifiedName~WindowEnumeratorTests`
Expected: PASS, including disappearance and FrameRelay filtering cases.

Commit: `feat(windows): enumerate shareable application windows`

## Task 3: Share the Graphics Capture and D3D11 Lifecycle

**Files:**
- Create: `src/SonicDesktopRelay.Media.Windows/GraphicsCaptureItemSource.cs`
- Modify: `src/SonicDesktopRelay.Media.Windows/GraphicsCaptureScreenSource.cs`
- Create: `src/SonicDesktopRelay.Media.Windows/GraphicsCaptureWindowSource.cs`
- Modify: `src/SonicDesktopRelay.Media.Windows/CaptureInterop.cs`
- Modify: `tests/SonicDesktopRelay.Media.Windows.Tests/GraphicsCaptureScreenSourceTests.cs`
- Create: `tests/SonicDesktopRelay.Media.Windows.Tests/GraphicsCaptureWindowSourceTests.cs`

**Interfaces:**
- Consume `CaptureTarget`, `WindowInfo`, and `IGraphicsCaptureItemFactory` from Tasks 1–2.
- Produce a shared internal lifecycle accepting a `GraphicsCaptureItem`, `VideoQuality`, and target description; expose frames, dimensions, counters, `SetFrameRate`, `StopAsync`, and target-closed notifications.
- Keep monitor/window adapters responsible only for validating/resolving their target and creating the item.

- [ ] **Step 1: Extract testable lifecycle behavior from existing monitor source**

First add tests around existing requirements: FPS throttling, size-change frame drop, counter behavior and idempotent stop. Keep the reused BGRA buffer contract unchanged.

```csharp
fakeItem.RaiseFrame(contentSize: new SizeInt32(1280, 720));
Assert.Equal((1280, 720), source.CurrentDimensions);
Assert.Equal(1, source.FramesDropped); // transition frame is from the old pool size
```

- [ ] **Step 2: Run monitor capture-source tests**

Run: `dotnet test tests/SonicDesktopRelay.Media.Windows.Tests/SonicDesktopRelay.Media.Windows.Tests.csproj --filter FullyQualifiedName~GraphicsCaptureScreenSourceTests`
Expected: new extraction tests fail before the lifecycle is split out.

- [ ] **Step 3: Extract common WGC/D3D11 ownership**

Move device creation, frame-pool/session setup, cursor configuration, callback locking, BGRA readback, resize/recreate, FPS throttling, counters and idempotent native cleanup into `GraphicsCaptureItemSource`. The monitor adapter must retain `MonitorInfo` metadata and existing start semantics. Publish current dimensions after `ContentSize` changes; `MediaFoundationH264Encoder.Encode` already reconfigures when scaled input dimensions change, so add a regression test proving encoding continues at the new window size and produces a keyframe.

```csharp
await capture.StartAsync(new CaptureTarget.Window(window), quality, ct);
Assert.Equal(window.Handle, fakeItemFactory.LastWindowHandle);
```

- [ ] **Step 4: Add window adapter and lifecycle tests**

Assert invalid/stale HWND is rejected before resource creation, close notification occurs once, owner-process exit closes capture, and repeated start/stop releases item, pool, session, textures and devices once. Use fakes for item/interop/process monitoring; do not require an actual desktop session.

- [ ] **Step 5: Run both capture-source suites and commit**

Run: `dotnet test tests/SonicDesktopRelay.Media.Windows.Tests/SonicDesktopRelay.Media.Windows.Tests.csproj --filter FullyQualifiedName~GraphicsCapture`
Expected: monitor compatibility plus window creation, close, resize and cleanup tests pass.

Commit: `feat(windows): capture monitor and window items through shared lifecycle`

## Task 4: Implement Process-Tree WASAPI Loopback

**Files:**
- Create: `src/SonicDesktopRelay.Media.Windows/ProcessLoopbackAudioSource.cs`
- Create: `src/SonicDesktopRelay.Media.Windows/ProcessLoopbackInterop.cs`
- Create: `tests/SonicDesktopRelay.Media.Windows.Tests/ProcessLoopbackAudioSourceTests.cs`

**Interfaces:**
- Consume `IAudioCaptureSource`, `AudioFrame`, and Task 1's `WindowInfo`.
- Produce `IProcessLoopbackClientFactory.Create(uint targetPid, bool includeProcessTree, int sampleRate, int channels, int bitsPerSample, int frameSamples)`, an `IProcessLoopbackClient` exposing `event WasapiPcmDataAvailableHandler? DataAvailable`, `event Action<Exception?>? Stopped`, `Start()`, `Stop()`, and `IAsyncDisposable`, and `ProcessLoopbackAudioSource` that adapts that client to `IAudioCaptureSource`.
- Expose `IsSupported`, `TargetProcessId`, `TargetProcessName`, `IncludesProcessTree` and `DegradedReason` diagnostics.

- [ ] **Step 1: Write process-loopback source tests against an injected client**

Cover target PID/tree parameters, 48 kHz stereo 20 ms frame output, silent buffers, callback detachment, idempotent stop, process exit, unsupported OS/build and activation failure. Assert unsupported/error states do not construct `WasapiLoopbackAudioSource` or any system-loopback factory.

```csharp
await source.StartAsync(CancellationToken.None);
Assert.Equal(target.ProcessId, fakeClient.TargetProcessId);
Assert.True(fakeClient.IncludeProcessTree);
```

- [ ] **Step 2: Run focused process-audio tests**

Run: `dotnet test tests/SonicDesktopRelay.Media.Windows.Tests/SonicDesktopRelay.Media.Windows.Tests.csproj --filter FullyQualifiedName~ProcessLoopbackAudioSourceTests`
Expected: FAIL until the source and injected-client contracts exist.

- [ ] **Step 3: Implement the source and isolate native activation**

Use `ActivateAudioInterfaceAsync` with `AUDIOCLIENT_ACTIVATION_PARAMS`, `AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS`, and `PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE`. Keep COM activation, completion callbacks and native buffer ownership in `ProcessLoopbackInterop`; keep normalization, frame accumulation, event forwarding and stop/dispose state in `ProcessLoopbackAudioSource`. Follow Microsoft's [process loopback parameter contract](https://learn.microsoft.com/en-us/windows/win32/api/audioclientactivationparams/ns-audioclientactivationparams-audioclient_process_loopback_params) and [application loopback sample](https://learn.microsoft.com/en-us/samples/microsoft/windows-classic-samples/applicationloopbackaudio-sample/) for activation and buffer lifetime.

```csharp
var client = factory.Create(window.ProcessId, includeProcessTree: true,
    sampleRate: 48_000, channels: 2, bitsPerSample: 16, frameSamples: 960);
```

- [ ] **Step 4: Add explicit Windows build support checks**

Check Windows build 20348 before native activation. Unsupported builds return a typed unavailable/degraded result and never try system loopback. Preserve monitor loopback behavior untouched.

- [ ] **Step 5: Run source tests and existing monitor audio tests; commit**

Run: `dotnet test tests/SonicDesktopRelay.Media.Windows.Tests/SonicDesktopRelay.Media.Windows.Tests.csproj --filter "FullyQualifiedName~ProcessLoopbackAudioSourceTests|FullyQualifiedName~WasapiLoopbackAudioSourceTests"`
Expected: process source edge cases and existing system-loopback behavior pass.

Commit: `feat(windows): capture audio from selected process tree`

## Task 5: Route Target Selection Through Runtime and Publisher Host

**Files:**
- Modify: `src/SonicDesktopRelay.App/RtcVideoPublishHost.cs`
- Modify: `src/SonicDesktopRelay.App/AppComposition.cs`
- Modify: `src/SonicDesktopRelay.Media/ScreenPublishPipeline.cs`
- Modify: `tests/SonicDesktopRelay.Media.Tests/ScreenPublishPipelineTests.cs`
- Modify: `tests/SonicDesktopRelay.Media.Tests/AudioPublishPipelineTests.cs`
- Create: `tests/SonicDesktopRelay.Media.Windows.Tests/PublisherCaptureSelectionTests.cs` (the existing Windows media test project already references `SonicDesktopRelay.App`)
- Modify: `tests/SonicDesktopRelay.Presentation.Tests/SessionRuntimeTests.cs`

**Interfaces:**
- Consume typed targets and target-closed event from Tasks 1–3, plus process audio from Task 4.
- `RtcVideoPublishHost.StartAsync` chooses `GraphicsCaptureScreenSource` + `WasapiLoopbackAudioSource` for monitor targets and `GraphicsCaptureWindowSource` + `ProcessLoopbackAudioSource` for window targets.
- Publish one target-closed notification from the host, subscribed by `SessionRuntime`; make repeated close/process-exit notifications idempotent.
- On process-loopback unsupported/activation failure, keep video publishing and audio absent/silent with a user-readable degraded reason. Never select system loopback in this branch.

- [ ] **Step 1: Add host/runtime selection tests**

Add constructor-injected capture/audio factories to `RtcVideoPublishHost` and use fakes to assert each target selects exactly one matching video and audio source. Assert a window target never requests a system-loopback instance even when process activation is unsupported or throws.

```csharp
await host.StartAsync(new CaptureTarget.Window(window), profile, ct);
Assert.IsType<GraphicsCaptureWindowSource>(factory.VideoCreated);
Assert.IsType<ProcessLoopbackAudioSource>(factory.AudioCreated);
Assert.Null(factory.SystemLoopbackCreated);
```

- [ ] **Step 2: Add target-close runtime tests**

Raise `CaptureTargetClosed` from a fake publisher while sharing; assert capture stop, backend `EndAsync`, signaling detach, a useful final UI state and safe handling of duplicate notifications.

- [ ] **Step 3: Implement factory selection and keep one media pipeline**

Construct the existing `ScreenPublishPipeline`, `AudioPublishPipeline`, encoder and `VideoPublisher` once per session. Replace only the capture/audio source selected at the host boundary. Preserve audio failure as nonfatal to video; if process-loopback initialization fails, omit the audio pipeline so the negotiated viewer receives no application audio and never receives system audio.

- [ ] **Step 4: Coordinate process/window shutdown and diagnostics**

Ensure selected HWND close or owner exit stops process loopback and video together. Log source kind, title/process identity, PID, process-tree mode, dimensions and close/resize/init/shutdown reason as structured metadata; do not include frame/audio contents.

- [ ] **Step 5: Run focused media and presentation tests; commit**

Run: `dotnet test tests/SonicDesktopRelay.Media.Tests/SonicDesktopRelay.Media.Tests.csproj`
Run: `dotnet test tests/SonicDesktopRelay.Presentation.Tests/SonicDesktopRelay.Presentation.Tests.csproj`
Expected: existing monitor publisher/runtime tests and new target/audio selection/lifecycle tests pass.

Commit: `feat(media): select capture and audio sources by target`

## Task 6: Add Share Screen Source and Target Selection

**Files:**
- Modify: `src/SonicDesktopRelay.App/Shell.cs`
- Modify: `src/SonicDesktopRelay.App/AppComposition.cs`
- Modify: `src/SonicDesktopRelay.App/Views/ShareView.axaml`
- Modify: `src/SonicDesktopRelay.App/Views/ShareView.axaml.cs` if event handlers are needed
- Create: `tests/SonicDesktopRelay.Media.Windows.Tests/ShellShareSelectionTests.cs` (the existing Windows media test project already references `SonicDesktopRelay.App`)

**Interfaces:**
- Consume `IWindowEnumerator`, `CaptureTarget`, and typed runtime start from earlier tasks.
- Define `ShareSourceKind` in the App project. Expose `ShareSourceKind`, `SelectedWindow`, `Windows`, `RefreshWindows()`, `SelectedCaptureTarget`, and target/audio status on `Shell` with change notifications; inject enumerators through a testable constructor while retaining parameterless Avalonia construction.
- Preserve `SelectedMonitor`, monitor refresh behavior, and current quality/FPS controls.

- [ ] **Step 1: Add view-model/shell selection tests**

Inject monitor and window enumerators. Cover monitor default, switching source, window refresh, retaining a still-valid HWND only while PID and process creation time match, choosing a replacement when the target disappears, empty lists, and Share command rejection without a valid target.

- [ ] **Step 2: Run the focused Share selection tests**

Run: `dotnet test tests/SonicDesktopRelay.Media.Windows.Tests/SonicDesktopRelay.Media.Windows.Tests.csproj --filter FullyQualifiedName~ShellShareSelectionTests`
Expected: FAIL until the testable Shell selection state exists.

- [ ] **Step 3: Implement source selection and target validation**

Keep selection logic in Shell and window enumeration in the injected service. `ShareAsync` maps the active selection to `CaptureTarget.Monitor` or `.Window`, checks quality/FPS, then invokes the typed runtime method.

```csharp
CaptureTarget? target = ShareSourceKind switch
{
    ShareSourceKind.Monitor when SelectedMonitor is { } m => new CaptureTarget.Monitor(m),
    ShareSourceKind.Window when SelectedWindow is { } w => new CaptureTarget.Window(w),
    _ => null
};
```

- [ ] **Step 4: Update Share XAML**

Add explicit Monitor/Window controls. Show the matching monitor picker or selectable window list with title/process name and a refresh action. Disable controls while sharing, clearly mark the selected target, and show process-audio unavailable/degraded text for window mode.

- [ ] **Step 5: Verify bindings and commit**

Run: `dotnet test tests/SonicDesktopRelay.Media.Windows.Tests/SonicDesktopRelay.Media.Windows.Tests.csproj --filter FullyQualifiedName~ShellShareSelectionTests`
Run: `dotnet build src/SonicDesktopRelay.App/SonicDesktopRelay.App.csproj`
Expected: selection tests pass and Avalonia compiled bindings resolve for both source modes.

Commit: `feat(ui): select a monitor or application window to share`

## Task 7: Close the Acceptance Matrix and Document Behavior

**Files:**
- Modify: `src/SonicDesktopRelay.App/Shell.cs` and `src/SonicDesktopRelay.App/RtcVideoPublishHost.cs` only for surfaced status/diagnostics gaps
- Modify: `tests/SonicDesktopRelay.Media.Windows.Tests/FrameRelayLoggingTests.cs`
- Modify: `tests/SonicDesktopRelay.Presentation.Tests/SessionRuntimeTests.cs`
- Modify: relevant capture/audio test files from Tasks 2–5
- Modify: `README.md`
- Modify: `docs/screen-publishing.md`

- [ ] **Step 1: Add remaining acceptance tests**

Pin the five Review Focus cases: reused HWND/PID mismatch, owner exit racing callbacks, unsupported/failed audio with no system fallback, silent process output, and repeated resize while publishing. Also run a window share followed by a monitor share on the same runtime and assert monitor capture plus system loopback are recreated after all window resources are disposed.

- [ ] **Step 2: Add diagnostics assertions**

Assert structured diagnostics include `capture.source_type`, target title/process, width/height, resize and close reason, `audio.capture_mode`, target PID/process, process-tree inclusion and activation result, and exclude pixel/sample payloads.

- [ ] **Step 3: Document source and audio behavior**

Document how to switch Monitor/Window, what window metadata is shown, process-tree audio selection, the Windows build 20348 audio requirement, video-only unsupported behavior, target-close behavior, and diagnostic fields. State explicitly that window sharing never sends system audio.

- [ ] **Step 4: Run changed projects and full solution tests**

Run focused changed projects:
`dotnet test tests/SonicDesktopRelay.Media.Windows.Tests/SonicDesktopRelay.Media.Windows.Tests.csproj`
`dotnet test tests/SonicDesktopRelay.Presentation.Tests/SonicDesktopRelay.Presentation.Tests.csproj`
`dotnet test tests/SonicDesktopRelay.Media.Tests/SonicDesktopRelay.Media.Tests.csproj`
Run full solution:
`dotnet test SonicDesktopRelay.sln`
Expected: all suites pass; document any Windows-only native capture checks that cannot run in the current environment.

- [ ] **Step 5: Review final diff and commit acceptance/documentation slice**

Run: `git diff --check`
Run: `git status --short`
Expected: no whitespace errors, no generated artifacts, no unrelated files.

Commit: `docs: document monitor and application window sharing`

## Final Delivery

- Confirm the current feature branch contains only the issue spec, implementation plan, feature changes and focused tests.
- Commit all remaining implementation changes on `codex/issue-16-window-audio`.
- Push the feature commit to `main` only after implementation and review gates have completed, as requested.
