# Task 1 Report: Persist and expose Ignore Discord audio

## Outcome

Implemented the local **Ignore Discord audio** preference with a default of `false`. The value is read from and written to `%APPDATA%/SonicDesktopRelay/user-preferences.json` by `FileUserPreferencesStore`. `Shell.IgnoreDiscordAudio` exposes a two-way bindable setting, raises `PropertyChanged` on updates, and emits `IgnoreDiscordAudioChanged` with the new boolean value for the next task to consume. Settings displays a disabled control and an explanation on Windows versions earlier than build 20348.

No capture startup or audio pipeline behavior was changed.

## Files changed

- `src/SonicDesktopRelay.Core/FileUserPreferencesStore.cs` — added the JSON-backed per-user preference store; missing, malformed, or unreadable settings use the default `false`.
- `tests/SonicDesktopRelay.Core.Tests/FileUserPreferencesStoreTests.cs` — covered default, persistence across store instances, and malformed-file fallback.
- `src/SonicDesktopRelay.App/Shell.cs` — loaded and exposed the preference, persistence, notifications, and Windows capability properties.
- `src/SonicDesktopRelay.App/Views/SettingsView.axaml` — added the checkbox and unsupported-Windows explanation.

## Verification

- `dotnet test tests/SonicDesktopRelay.Core.Tests/SonicDesktopRelay.Core.Tests.csproj --filter FullyQualifiedName~FileUserPreferencesStoreTests` — passed, 3 tests.
- `dotnet build src/SonicDesktopRelay.App/SonicDesktopRelay.App.csproj` — succeeded, 0 warnings and 0 errors.

The initial persistence test run failed because the serialized JSON property casing did not match the reader. The property name was corrected, and the focused tests passed afterward.

## Concerns

None identified. Settings write errors are surfaced through the existing `ShellError` property; the in-memory toggle and notification still update when a write fails.

## Round 1 review fix

`ReadIgnoreDiscordAudio` now checks that the parsed JSON root is an object before looking up the preference property. Valid non-object roots such as `[]` and `null` safely return the default `false`. Regression coverage was added for both roots.

- Before the fix, the focused regression run failed for both cases with `InvalidOperationException` from `JsonElement.TryGetProperty`.
- After the fix, `dotnet test tests/SonicDesktopRelay.Core.Tests/SonicDesktopRelay.Core.Tests.csproj --filter FullyQualifiedName~FileUserPreferencesStoreTests` passed: 5 passed, 0 failed.
