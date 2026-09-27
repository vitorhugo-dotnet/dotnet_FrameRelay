# Windows Tray and Startup Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement issue #36's Windows tray, minimize-to-tray, and per-user login startup behaviors using the existing app settings and single-instance infrastructure.

**Architecture:** Extend `FileUserPreferencesStore` and `Shell` for the two persistent settings. Add a testable HKCU Run registration service and startup-argument parsing, integrate one Avalonia tray icon with the existing `MainWindow` lifecycle, and reuse the existing coordinator to avoid duplicate processes.

**Tech Stack:** .NET 10, Avalonia 12.1.1, xUnit, Microsoft.Win32 registry.

**Spec:** `docs/superpowers/specs/2026-09-27-windows-tray-startup-design.md`

## Global Constraints

- Windows only; no Windows Service, scheduled task, background updater, or Linux/macOS autostart.
- Keep user preferences in the existing `user-preferences.json` file and registry startup registration per-user under HKCU.
- Use Avalonia `TrayIcon` / `NativeMenu` and the existing process activation coordinator.
- Reuse the existing Start on system startup and Minimize to tray settings; add no duplicate controls.
- Keep unrelated settings disabled.
- Add no dependencies.

## Review Focus

- Existing preference files without the new properties still load both settings as false; malformed/non-object JSON still follows the store's safe default behavior. Test in `FileUserPreferencesStoreTests`.
- Executable paths with spaces are quoted as one executable in the Run command, with startup arguments preserved. Test command formatting in `WindowsStartupRegistrationTests`.
- Repeated enable updates a single stable Run value and disabling deletes it; registry failures are logged and reach the UI. Test registry behavior with an injected key adapter and exercise failure propagation at the service boundary.
- Duplicate login launches do not create another window/tray icon; protocol activations still forward. Test startup argument parsing and coordinator selection/forwarding without UI startup.
- Closing/minimizing while tray mode is enabled hides the window and preserves shell state; explicit tray Exit requests desktop shutdown. Test the lifecycle policy at a focused seam and manually verify Avalonia event hookup.

---

### Task 1: Persist tray and startup preferences

**Files:**
- Modify: `src/SonicDesktopRelay.Core/FileUserPreferencesStore.cs`
- Test: `tests/SonicDesktopRelay.Core.Tests/FileUserPreferencesStoreTests.cs`

**Interfaces:**
- Add `ReadStartOnSystemStartup()`, `WriteStartOnSystemStartup(bool)`, `ReadMinimizeToTray()`, and `WriteMinimizeToTray(bool)`.
- Preserve existing `ReadIgnoreDiscordAudio()` and `WriteIgnoreDiscordAudio(bool)` behavior while writing all three values together.

- [ ] Add tests for defaults from a missing/legacy JSON file, round-trip of all preferences, and malformed JSON safe defaults.
- [ ] Run `dotnet test tests/SonicDesktopRelay.Core.Tests/SonicDesktopRelay.Core.Tests.csproj --filter FullyQualifiedName~FileUserPreferencesStoreTests`; verify new tests fail for missing methods/fields.
- [ ] Implement the four properties in the existing JSON shape with camel-case names `startOnSystemStartup` and `minimizeToTray`; missing booleans read false.
- [ ] Re-run the filtered Core tests and verify PASS.

### Task 2: Add testable per-user Windows startup registration

**Files:**
- Create: `src/SonicDesktopRelay.App/WindowsStartupRegistration.cs`
- Create: `tests/SonicDesktopRelay.App.Tests/SonicDesktopRelay.App.Tests.csproj`
- Create: `tests/SonicDesktopRelay.App.Tests/WindowsStartupRegistrationTests.cs`
- Modify: `SonicDesktopRelay.sln`

**Interfaces:**
- `IStartupRegistration` exposes `void SetEnabled(bool enabled)`.
- `WindowsStartupRegistration(IStartupRunKey key, Func<string?> executablePath, ILogger logger)` uses stable value name `FrameRelay` and current-user Run key `Software\Microsoft\Windows\CurrentVersion\Run`.
- `IStartupRunKey` exposes `string? Read(string name)`, `void Write(string name, string value)`, and `void Delete(string name)`; production implementation wraps `Registry.CurrentUser`.
- Static `WindowsStartupRegistration.FormatCommand(string executablePath)` returns a quoted executable followed by `--startup`, with correct quoting for paths containing spaces.

- [ ] Add tests for spaced executable paths, replacement/idempotency, disable deletion, missing executable failure, and adapter failure propagation.
- [ ] Run the focused app test project and verify failures against the absent service.
- [ ] Implement the registry adapter and registration service; log registration errors then rethrow so `Shell` can surface them.
- [ ] Re-run the focused app tests and verify PASS.

### Task 3: Parse startup launch mode and preserve single-instance behavior

**Files:**
- Modify: `src/SonicDesktopRelay.Core/LaunchActivation.cs`
- Test: `tests/SonicDesktopRelay.Core.Tests/LaunchActivationTests.cs`
- Modify: `src/SonicDesktopRelay.App/Program.cs`
- Modify: `src/SonicDesktopRelay.App/LaunchActivationCoordinator.cs`
- Create: `src/SonicDesktopRelay.App/LaunchInstancePolicy.cs`
- Test: `tests/SonicDesktopRelay.App.Tests/LaunchInstancePolicyTests.cs`
- Modify: `src/SonicDesktopRelay.App/LaunchActivationRouter.cs`
- Modify: `src/SonicDesktopRelay.App/App.axaml.cs`


**Interfaces:**
- Add `LaunchStartupOptions(bool IsStartupLaunch)` and `LaunchStartupOptionsParser.RemoveArguments(IReadOnlyList<string>, out LaunchStartupOptions)` for `--startup`; Core tests cover hidden-start combinations with `MinimizeToTray`.
- Existing protocol URI handling and activation routing remain intact.
- When the per-user coordinator is already owned, a startup launch without a protocol URI exits immediately; it does not forward a visible activation.
- `App` reads the startup option and persisted `MinimizeToTray` preference, creating the main window hidden only when both are true.

- [ ] Test startup flag parsing/removal and normal launch defaults; test that startup combined with the persisted tray preference determines initial hidden state.
- [ ] Verify Core and focused App tests fail for absent option parsing.
- [ ] Implement parser and route initial options into `App` without changing URI activation semantics.
- [ ] Test `LaunchInstancePolicy.Decide` so a duplicate startup exits without UI, a duplicate protocol launch forwards, and the mutex owner runs.
- [ ] Re-run focused tests and verify PASS.

### Task 4: Bind settings and reconcile startup registration

**Files:**
- Modify: `src/SonicDesktopRelay.App/Shell.cs`
- Modify: `src/SonicDesktopRelay.App/Views/SettingsView.axaml`
- Test: `tests/SonicDesktopRelay.App.Tests/ShellPreferenceTests.cs`

**Interfaces:**
- `Shell` exposes two-way `StartOnSystemStartup` and `MinimizeToTray` properties.
- Add an internal constructor seam that accepts preferences store, startup registration, and logger dependencies; the public constructor retains production defaults.

- [ ] Test each preference persists independently, startup enable/disable invokes registration, and a failed registration sets `ShellError` without reverting the user's stored preference.
- [ ] Verify focused App test failures before implementation.
- [ ] Initialize both preferences from `FileUserPreferencesStore`; reconcile current-user registration at startup to refresh relocated executable paths and remove stale entries when disabled.
- [ ] Bind existing checkboxes; enable only General card sections that are implemented, leave unrelated controls disabled, and add close/exit helper copy.
- [ ] Re-run the focused Core and App tests and verify PASS.

### Task 5: Add tray icon and close/minimize lifecycle

**Files:**
- Create: `src/SonicDesktopRelay.App/Assets/FrameRelay.ico`
- Modify: `src/SonicDesktopRelay.App/SonicDesktopRelay.App.csproj`
- Modify: `src/SonicDesktopRelay.App/App.axaml`
- Modify: `src/SonicDesktopRelay.App/App.axaml.cs`
- Modify: `src/SonicDesktopRelay.App/Views/MainWindow.axaml.cs`
- Test: `tests/SonicDesktopRelay.App.Tests/TrayLifecyclePolicyTests.cs`

**Interfaces:**
- One tray icon is owned by the desktop `App` lifetime, with Open FrameRelay, Settings, separator, and Exit menu items.
- `MainWindow` exposes restore and navigate-to-Settings actions for tray callbacks.
- A pure `TrayLifecyclePolicy` decides whether a minimize or user close is hidden based on `MinimizeToTray` and whether shutdown was explicitly requested.

- [ ] Test tray policy for enabled/disabled preference, minimize, user close, and explicit exit.
- [ ] Verify policy tests fail before implementation.
- [ ] Add a branded ICO resource and configure Avalonia tray icon use; ensure one icon is created and disposed with the desktop lifetime.
- [ ] Wire minimize and user-close to hide only when enabled; tray restore shows/activates the same window, Settings selects the existing Settings page, and Exit requests desktop shutdown.
- [ ] Start hidden when `--startup` is present and `MinimizeToTray` is enabled; otherwise show normally.
- [ ] Build the app and run focused lifecycle tests; inspect the tray menu and window event wiring for duplicate icon and disposal paths.

### Task 6: Final focused verification and PR

**Files:**
- No additional source files unless prior tasks expose a missing issue requirement.

- [ ] Run `dotnet test tests/SonicDesktopRelay.Core.Tests/SonicDesktopRelay.Core.Tests.csproj`.
- [ ] Run `dotnet test tests/SonicDesktopRelay.App.Tests/SonicDesktopRelay.App.Tests.csproj`.
- [ ] Run `dotnet build src/SonicDesktopRelay.App/SonicDesktopRelay.App.csproj --no-restore`.
- [ ] Review `git diff --check`, the diff summary, and the final worktree status.
- [ ] Commit the implementation with a message referencing issue #36, push the feature branch, and open a PR with `Closes #36` in its body.
