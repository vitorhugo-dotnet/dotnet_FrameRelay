# Borderless Graphics Capture Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remove the Windows Graphics Capture border for monitor and window shares when the OS API is available and the user grants access, while preserving normal capture with a logged reason for every fallback.

**Architecture:** Add a small injectable borderless policy/platform seam in `Media.Windows`. The shared `GraphicsCaptureItemSource` requests access, applies `IsBorderRequired = false` only after a grant, and logs a structured outcome before starting capture. Denial, unsupported APIs, and API exceptions keep the default border and continue sharing.

**Tech Stack:** .NET 10, WinRT `Windows.Graphics.Capture`, Windows API metadata, Microsoft.Extensions.Logging, xUnit.

**Spec:** `docs/superpowers/specs/2026-09-25-borderless-graphics-capture-design.md`

## Global Constraints

- Keep the existing `net10.0-windows10.0.19041.0` target and runtime-gate borderless APIs introduced at Windows build 20348. Set `WindowsSdkPackageVersion` to `10.0.26100.87` centrally so all Windows-targeted projects compile against matching contracts.
- Apply the policy in the shared capture item source so both monitor and window capture use it.
- Do not crop frames, add overlays, or change signaling, audio, encoding, decoding, bitrate, or FPS behavior.
- Treat denied access and unavailable APIs as successful bordered capture; cancellation during access request cancels startup.
- The repository has no MSIX/AppX packaging target. Do not put package capabilities in the Win32 `app.manifest`; any package target added later must declare `graphicsCaptureWithoutBorder` in its package manifest.

## Review Focus

- API metadata missing on Windows 10 build 19041: verify the policy does not invoke newer WinRT members and reports `unsupported`.
- User denies access: verify `IsBorderRequired` is never set false and normal session start proceeds.
- Access request throws because of package identity/capability/runtime state: verify this degrades to bordered capture with an observable reason.
- Setting `IsBorderRequired` throws despite access being granted: verify normal session start still proceeds and reports an apply failure.
- Cancellation while the access prompt is pending: verify startup propagates cancellation and does not call `StartCapture`.

---

### Task 1: Add a testable borderless policy and WinRT adapter

**Files:**
- Modify: `Directory.Build.props`
- Create: `src/SonicDesktopRelay.Media.Windows/BorderlessCapturePolicy.cs`
- Test: `tests/SonicDesktopRelay.Media.Windows.Tests/BorderlessCapturePolicyTests.cs`

**Interfaces:**
- Produces `internal sealed record BorderlessCaptureResult(bool IsEnabled, string Outcome, string? Reason)`.
- Produces `internal interface IBorderlessCapturePlatform` with `bool IsAvailable { get; }`, `Task<AppCapabilityAccessStatus> RequestAccessAsync()`, and `void SetBorderRequired(GraphicsCaptureSession session, bool required)`.
- Produces `internal interface IBorderlessCapturePolicy` with `Task<BorderlessCaptureResult> TryEnableAsync(GraphicsCaptureSession session, CancellationToken ct)` and `internal sealed class BorderlessCapturePolicy(IBorderlessCapturePlatform platform)` implementing it.
- The production platform checks API type/method/property availability with `ApiInformation`, preserves `AppCapabilityAccessStatus` values so user denial, system denial, and a missing package declaration produce distinct reasons, calls `GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless)`, and sets `session.IsBorderRequired` to the passed value.

- [ ] **Step 1: Add failing tests for grant and denial**

Use a fake `IBorderlessCapturePlatform` that returns `AppCapabilityAccessStatus` values and tracks request and setter calls. Pass `null!` for the WinRT session because the fake does not inspect it.

```csharp
[Fact]
public async Task Grant_disables_border()
{
    var platform = new FakeBorderlessCapturePlatform { IsAvailable = true, AccessStatus = AppCapabilityAccessStatus.Allowed };
    var result = await new BorderlessCapturePolicy(platform).TryEnableAsync(null!, CancellationToken.None);
    Assert.True(result.IsEnabled);
    Assert.Equal("granted", result.Outcome);
    Assert.Equal(new[] { false }, platform.RequiredValues);
}

[Fact]
public async Task Denial_keeps_default_border()
{
    var platform = new FakeBorderlessCapturePlatform { IsAvailable = true, AccessStatus = AppCapabilityAccessStatus.DeniedByUser };
    var result = await new BorderlessCapturePolicy(platform).TryEnableAsync(null!, CancellationToken.None);
    Assert.False(result.IsEnabled);
    Assert.Equal("denied", result.Outcome);
    Assert.Empty(platform.RequiredValues);
}
```

- [ ] **Step 2: Run the focused tests and confirm they fail because the policy types do not exist**

Run: `dotnet test tests/SonicDesktopRelay.Media.Windows.Tests/SonicDesktopRelay.Media.Windows.Tests.csproj --filter FullyQualifiedName~BorderlessCapturePolicyTests`
Expected: compile failure for the missing policy/platform types.

- [ ] **Step 3: Add policy/platform types and production adapter**

The policy returns outcomes `granted`, `denied`, `unsupported`, `request_failed`, or `apply_failed`. It rethrows `OperationCanceledException` when the supplied token is cancelled; other access or property exceptions become fallback results with the exception message as `Reason`. Use `WaitAsync(ct)` around the WinRT request so cancellation stops the share startup even though WinRT's operation does not accept a cancellation token.

- [ ] **Step 4: Add tests for unsupported APIs, request/apply failures, and cancellation**

Assert unsupported skips the request; request and setter exceptions yield their matching fallback outcome; and a cancelled pending request throws `OperationCanceledException` without invoking the border setter.

- [ ] **Step 5: Run focused policy tests and confirm they pass**

Run: `dotnet test tests/SonicDesktopRelay.Media.Windows.Tests/SonicDesktopRelay.Media.Windows.Tests.csproj --filter FullyQualifiedName~BorderlessCapturePolicyTests`
Expected: all policy cases pass without creating a WGC session or showing a Windows prompt.

### Task 2: Integrate borderless access into the shared WGC session lifecycle

**Files:**
- Modify: `src/SonicDesktopRelay.Media.Windows/GraphicsCaptureItemSource.cs`
- Modify: `src/SonicDesktopRelay.Media.Windows/GraphicsCaptureScreenSource.cs`
- Modify: `src/SonicDesktopRelay.Media.Windows/GraphicsCaptureWindowSource.cs`
- Modify: `src/SonicDesktopRelay.App/RtcVideoPublishHost.cs`
- Create: `src/SonicDesktopRelay.Media.Windows/BorderlessCaptureStartupCoordinator.cs`
- Create: `src/SonicDesktopRelay.Media.Windows/BorderlessCaptureDiagnostics.cs`
- Create: `tests/SonicDesktopRelay.Media.Windows.Tests/BorderlessCaptureStartupCoordinatorTests.cs`
- Create: `tests/SonicDesktopRelay.Media.Windows.Tests/BorderlessCaptureDiagnosticsTests.cs`
- Modify: `tests/SonicDesktopRelay.Media.Windows.Tests/GraphicsCaptureScreenSourceTests.cs`

**Interfaces:**
- Consumes `IBorderlessCapturePolicy` and `BorderlessCaptureResult` from Task 1; creates the startup coordinator and diagnostics helper in this task.
- `GraphicsCaptureItemSource` accepts `IGraphicsCaptureItemFactory`, `IBorderlessCapturePolicy`, and `ILogger` in an internal constructor; its default constructor uses the production implementations and a `NullLogger`.
- `GraphicsCaptureScreenSource` and `GraphicsCaptureWindowSource` retain parameterless public construction, expose a public logger-only overload for app composition, and add internal policy/logger overloads for tests. `PublisherCaptureSelection` passes the existing logger factory to the source loggers using category `SonicDesktopRelay.Media.Windows.GraphicsCaptureItemSource`.

- [ ] **Step 1: Add failing coordinator tests for fallback continuation and cancellation**

Create `BorderlessCaptureStartupCoordinatorTests` with a fake policy and a counter delegate. For a denied result assert that `ConfigureAndStartAsync` returns `denied` and increments the start counter once. For a fake policy blocked on a `TaskCompletionSource`, cancel the token and assert it throws `OperationCanceledException` and leaves the start counter at zero. These tests need no D3D device or WGC session; pass `null!` for the session and let the fake policy ignore it.

- [ ] **Step 2: Run the focused test and confirm the expected failure**

Run: `dotnet test tests/SonicDesktopRelay.Media.Windows.Tests/SonicDesktopRelay.Media.Windows.Tests.csproj --filter FullyQualifiedName~BorderlessCaptureStartupCoordinatorTests`
Expected: compile failure because `BorderlessCaptureStartupCoordinator` does not exist yet.

- [ ] **Step 3: Make WGC startup asynchronous and apply the outcome before `StartCapture`**

Change `GraphicsCaptureItemSource.StartAsync` to `async Task`. Acquire a new `SemaphoreSlim` lifecycle gate before checking/creating session state; hold it across asynchronous permission configuration and release it in `finally`. Construct the item, device, pool, and session under `_gate`, release `_gate` while awaiting `BorderlessCaptureStartupCoordinator.ConfigureAndStartAsync`, then reacquire `_gate` to mark `_running` and initialize timing immediately before the coordinator's start callback calls `GraphicsCaptureSession.StartCapture`. `StopAsync` must acquire the lifecycle gate before detaching and disposing native objects, preventing a stop from being followed by a late start. On cancellation or setup exceptions, detach/dispose every partially initialized object and rethrow; a returned fallback result is not an exception.

Log one structured event per start through the injected `ILogger` with target kind (`monitor` or `window`), outcome, enabled state, and reason. `PublisherCaptureSelection` creates that logger from the existing `ILoggerFactory` and passes it to the public capture wrappers. Do not log titles, process names, or other user data.

- [ ] **Step 4: Inject a no-prompt policy in the WGC integration tests and assert monitor/window policy parity**

Update the hardware capture tests to construct sources with a fake `unsupported` policy, preventing a system consent dialog from appearing during automated tests. Add deterministic recording-logger tests in `BorderlessCaptureDiagnosticsTests` for `BorderlessCaptureDiagnostics.Log`: assert monitor and window target kinds are included and that a fallback reason is emitted for `denied` and `request_failed` results.

- [ ] **Step 5: Run focused startup and policy tests**

Run: `dotnet test tests/SonicDesktopRelay.Media.Windows.Tests/SonicDesktopRelay.Media.Windows.Tests.csproj --filter FullyQualifiedName~BorderlessCapture`
Expected: policy and startup tests pass; no test requires accepting the system prompt.

### Task 3: Document behavior and verify build/test integration

**Files:**
- Modify: `docs/screen-publishing.md`

- [ ] **Step 1: Document supported behavior and fallback**

Explain that borderless capture is attempted on supported Windows builds, requires user consent, and falls back to the normal border if unsupported or denied. State that packaged builds must declare `graphicsCaptureWithoutBorder` in the package manifest; the current unpackaged executable uses the Win32 manifest and does not gain an AppX capability declaration.

- [ ] **Step 2: Run the Windows Media project test suite**

Run: `dotnet test tests/SonicDesktopRelay.Media.Windows.Tests/SonicDesktopRelay.Media.Windows.Tests.csproj`
Expected: all deterministic tests pass; hardware integration tests either pass on Windows with WGC support or use their existing platform guards.

- [ ] **Step 3: Build the solution**

Run: `dotnet build SonicDesktopRelay.sln --no-restore`
Expected: all projects compile with no new platform compatibility warnings attributable to unguarded build-20348 APIs.

- [ ] **Step 4: Inspect final diff and commit the implementation**

Run `git diff --check`, inspect the full diff, then commit the source, tests, and documentation with `feat: support borderless Windows capture`.

---
