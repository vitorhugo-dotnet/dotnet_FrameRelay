# Windows tray and login startup design

## Goal

Implement issue #36 using Avalonia's native tray support, make the existing General settings functional, and allow an optional per-user Windows login launch. Tray, close, and startup behavior must preserve a single running app and its session state. Explicit Exit must shut down the application and dispose the window's runtime.

## Current architecture

- `App` creates `MainWindow` under Avalonia's classic desktop lifetime.
- `MainWindow` owns `Shell`, registers launch activation, and disposes `Shell` when its window closes.
- `Shell` persists the Discord audio preference through `FileUserPreferencesStore` in the user's application data directory.
- `Program` already uses `LaunchActivationCoordinator` and `LaunchActivationRouter` for per-user single-instance activation forwarding. Startup flags can integrate with this existing mechanism instead of creating another single-instance system.
- The Settings General card is disabled and contains mocked startup/tray checkboxes.

## Design

### Preferences

Extend the existing user-preferences JSON document and store with `startOnSystemStartup` and `minimizeToTray` booleans. Missing properties remain false for backward compatibility. Keep both values in the application configuration file; only the Windows startup registration itself belongs in the registry. Expose both preferences through `Shell` and save changes immediately, surfacing persistence and registration failures through the existing shell error/logging path.

### Windows login registration

Add a small injectable startup-registration abstraction with a Windows implementation using the current user's `HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run` key. Write one stable value name and a quoted current executable path plus startup arguments. Repeated enable operations update the same value; disable removes it. On launch, reconcile the registration with the persisted preference so an upgrade that relocates the executable updates the old path. Log and surface registry failures; do not require elevation or introduce a service/task.

### Tray and window lifecycle

Create one Avalonia `TrayIcon` from the existing application ICO resource and a native menu with Open FrameRelay, Settings, separator, and Exit. Keep its lifetime tied to the desktop application, not to a hidden window. Tray Open restores and activates the existing window; Settings also navigates the existing `Shell` to Settings. Ensure only one tray icon is created.

When `Minimize to tray` is enabled, minimize and user-close hide the window while preserving `Shell` and its runtime. When disabled, preserve ordinary minimize/close behavior. Make close-to-hide distinguishable from explicit application shutdown; the tray Exit command requests desktop lifetime shutdown so the existing window close/disposal path runs. Startup launch with the tray preference enabled starts with the window hidden; otherwise it opens normally. Tray restore reveals the same window/session.

### Startup activation and duplicate launches

Use explicit startup arguments to distinguish a login launch and whether it should begin hidden. Extend the existing launch activation protocol/coordinator so a repeated login activation is forwarded to the active process instead of constructing another window/tray icon. A normal explicit open activation continues to restore and focus the window. Startup with tray disabled follows the normal visible startup path.

### Settings UI

Enable only the General card portions implemented here. Bind the two existing checkboxes to the Shell preferences and remove the General card's blanket disabled/coming-soon treatment. Leave unrelated general controls visibly unavailable. Add concise helper copy explaining that close hides the app when minimize-to-tray is enabled and Exit in the tray menu fully quits.

## Testing

Add focused tests for preference backward-compatible persistence, startup registration add/remove/idempotency and executable quoting, and launch activation's startup/tray flag handling and forwarding. Keep registry access behind an abstraction so behavior tests do not mutate the real user's registry. Verify tray/window routing and close/exit policy through focused app-level seams where Avalonia's UI test harness permits; otherwise cover policy decisions independently and manually review the Avalonia event wiring. Build the app and run only the relevant test projects, not the full suite.

## Scope

Windows only. No duplicate settings, Windows Service, scheduled task, background updater, or Linux/macOS autostart implementation.
