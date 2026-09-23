# Issue #8 Responsive Avalonia UI Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:subagent-driven-development` (recommended) or `superpowers:executing-plans` to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Refactor the FrameRelay desktop shell and four pages to match the supplied prototypes, reflow with live window resizing, and preserve existing behavior.

**Architecture:** Keep `Shell`, `MainWindowViewModel`, and the current runtime bindings as the source of truth. Update the Avalonia shell, page compositions, shared tokens, and shared card/unavailable styles; use responsive layouts and live container queries in XAML instead of page-specific width code.

**Tech Stack:** .NET 10, Avalonia 12.1.1, compiled XAML bindings, xUnit presentation tests.

**Spec:** `docs/superpowers/specs/2026-09-23-issue-8-responsive-avalonia-ui-design.md`

## Global Constraints

- Keep the App target at `net10.0-windows10.0.19041.0` and its current Avalonia 12.1.1 dependencies.
- Keep backend, signaling, WebRTC, and media architecture unchanged.
- Keep `Shell`, `MainWindowViewModel`, existing runtime snapshots, diagnostics collections, and currently working actions as the source of truth.
- Show unsupported values as `---`; do not invent telemetry or simulate viewers.
- Keep unsupported controls visibly dark/desaturated and noninteractive, including keyboard focus.
- Preserve diagnostics privacy: do not capture raw SDP, ICE candidate contents, credentials, or signaling payloads.
- Preserve Watch F11/Esc fullscreen behavior and video aspect ratio.
- Do not rename legacy project, namespace, assembly, or storage identifiers.

## Review Focus

- A small live window can reflow all pages without hiding actions or requiring horizontal page scrolling; manually resize across each breakpoint and inspect all four pages.
- Missing/invalid configuration or a machine with no enumerated monitor still shows the existing actionable validation/error states; manually exercise Settings and Share with those conditions.
- Unknown latency, bitrate, codec, or other unprovided values remain `---` through idle and active session states; inspect all summary cards during manual page verification.
- A prototype-only action cannot receive focus, activate by keyboard/mouse, or mutate state; test every disabled card while tabbing and clicking during manual page verification.
- Diagnostics continues to display real metadata while excluding raw signaling material; run the existing signaling diagnostics tests and inspect Diagnostics during manual page verification.

---

## File map

- `src/SonicDesktopRelay.App/App.axaml`: merge shared component styles into application resources.
- `src/SonicDesktopRelay.App/Styles/Tokens.axaml`: shared dark palette, semantic state colors, spacing, radii, and type sizes.
- `src/SonicDesktopRelay.App/Styles/Components.axaml` (new): reusable card, status, navigation, and unavailable-state classes.
- `src/SonicDesktopRelay.App/Views/MainWindow.axaml` and `.axaml.cs`: responsive shell, active navigation, FrameRelay title, global status/footer, and page host.
- `src/SonicDesktopRelay.App/Views/ShareView.axaml` and `.axaml.cs`: share-page composition and existing monitor/session actions.
- `src/SonicDesktopRelay.App/Views/WatchView.axaml` and `.axaml.cs`: watch-page composition around the existing `VideoSurface` and fullscreen handlers.
- `src/SonicDesktopRelay.App/Views/DiagnosticsView.axaml` and `.axaml.cs`: real diagnostic groups/logs and responsive summary layout.
- `src/SonicDesktopRelay.App/Views/SettingsView.axaml` and `.axaml.cs`: responsive settings layout and existing editable fields.
- `src/SonicDesktopRelay.Presentation/MainWindowViewModel.cs`: default landing page, if still required after moving Home out of the main product flow.
- `tests/SonicDesktopRelay.Presentation.Tests/MainWindowViewModelTests.cs`: default landing-page regression test.

## Task 1: Shared visual tokens and component states

**Files:**
- Modify: `src/SonicDesktopRelay.App/Styles/Tokens.axaml`
- Create: `src/SonicDesktopRelay.App/Styles/Components.axaml`
- Modify: `src/SonicDesktopRelay.App/App.axaml`
- Verify: `src/SonicDesktopRelay.App/SonicDesktopRelay.App.csproj`

**Interfaces:**
- Produces shared resource keys for page/rail/card surfaces, border and text roles, accent/success/warning/danger states, spacing, radii, and typography.
- Produces style classes `.card`, `.statusConnected`, `.statusWarning`, `.statusDisconnected`, and `.unavailable`. `.unavailable` darkens/desaturates the containing card; card roots using it must set `IsEnabled="False"` so descendants cannot receive input or focus.
- App resources expose `Components.axaml` after `Tokens.axaml` so all referenced resources are available.

- [ ] **Step 1: Add the failing landing-page regression test**

Add this test to `MainWindowViewModelTests.cs` before changing the view model:

```csharp
[Fact]
public void A_new_window_opens_on_the_share_page()
{
    var viewModel = new MainWindowViewModel();

    Assert.Equal(Page.Share, viewModel.CurrentPage);
}
```

- [ ] **Step 2: Run the focused presentation test and confirm it fails**

Run: `dotnet test tests/SonicDesktopRelay.Presentation.Tests --filter FullyQualifiedName~A_new_window_opens_on_the_share_page`
Expected: FAIL because a new view model currently selects `Page.Home`.

- [ ] **Step 3: Set Share as the default page and implement shared styles**

Change `_currentPage` in `MainWindowViewModel` from `Page.Home` to `Page.Share`. Add the semantic resources to `Tokens.axaml`, define the shared classes in `Components.axaml`, and merge that dictionary after `Tokens.axaml` in `App.axaml`. Keep each shared value centralized; page files must reference these keys/classes rather than reintroducing literal palette/spacing values.

Example unavailable-card use in later tasks:

```xml
<Border Classes="card unavailable" IsEnabled="False">
    <!-- prototype-only content; descendants inherit disabled input state -->
</Border>
```

- [ ] **Step 4: Run focused tests and build the App to validate resource/XAML compilation**

Run: `dotnet test tests/SonicDesktopRelay.Presentation.Tests --filter FullyQualifiedName~A_new_window_opens_on_the_share_page`
Run: `dotnet build src/SonicDesktopRelay.App/SonicDesktopRelay.App.csproj`
Expected: test passes and the application XAML compiles without missing-resource warnings/errors.

- [ ] **Step 5: Commit the shared visual foundation**

```powershell
git add src/SonicDesktopRelay.App/App.axaml src/SonicDesktopRelay.App/Styles/Tokens.axaml src/SonicDesktopRelay.App/Styles/Components.axaml src/SonicDesktopRelay.Presentation/MainWindowViewModel.cs tests/SonicDesktopRelay.Presentation.Tests/MainWindowViewModelTests.cs
git commit -m "feat(ui): add shared FrameRelay visual tokens"
```

## Task 2: Responsive shell and navigation

**Files:**
- Modify: `src/SonicDesktopRelay.App/Views/MainWindow.axaml`
- Modify: `src/SonicDesktopRelay.App/Views/MainWindow.axaml.cs`
- Use: `src/SonicDesktopRelay.App/Shell.cs`
- Use: `src/SonicDesktopRelay.Presentation/MainWindowViewModel.cs`

**Interfaces:**
- Consumes `Shell.ViewModel.CurrentPage`, `Shell.ViewModel.StatusText`, `Shell.DeviceName`, and `Shell.IsVideoFullScreen` as they exist today.
- Produces navigation to exactly `Share`, `Watch`, `Diagnostics`, and `Settings`; the selected entry carries one shared active visual state. Home is no longer the default or a primary navigation entry.
- The window content becomes a live size container. Its compact/wide styles update on resize; they do not use `OnFormFactor`, which resolves only at startup.

- [ ] **Step 1: Record the existing shell behavior before editing**

Confirm `MainWindow.axaml.cs` routes a navigation button's `Tag` through `Enum.TryParse<Page>` to `ViewModel.CurrentPage`, and that the sidebar is hidden while `IsVideoFullScreen` is true. Keep these behaviors as the acceptance baseline.

- [ ] **Step 2: Implement the shell layout in XAML**

Use an auto-sized navigation column and a star-sized content column. Set the normal window size to `1180x760`, the supported minimum to `640x480`, and keep the window resizable. Place FrameRelay branding, the four required entries, a compact connection/session status, and the device name/version footer in the rail. Bind navigation to the existing page enum. Remove the Home view from the visible page host; retain its source files if they have no other consumers. Preserve the existing fullscreen visibility binding.

- [ ] **Step 3: Add live wide/compact navigation styles**

Declare the Window as a width container and add its `ContainerQuery` in `Window.Styles`. At the compact breakpoint, set the rail width to `64`, hide text labels while keeping accessible tooltips, and retain the icons and active state. The `Auto,*` grid then reallocates the remaining width to page content. Do not add a `SizeChanged` width switch to code-behind. Use the same live container-query/reflow techniques for page layouts, following [Avalonia's responsive layout guidance](https://docs.avaloniaui.net/docs/styling/container-queries).

```xml
<Window Container.Name="window" Container.Sizing="Width">
    <Window.Styles>
        <ContainerQuery Name="window" Query="max-width:700">
            <Style Selector="Border#navigationRail">
                <Setter Property="Width" Value="64" />
            </Style>
            <Style Selector="TextBlock.navLabel">
                <Setter Property="IsVisible" Value="False" />
            </Style>
        </ContainerQuery>
    </Window.Styles>
    <Grid ColumnDefinitions="Auto,*">
        <Border x:Name="navigationRail" Width="180" />
    </Grid>
</Window>
```

Keep the production grid's navigation rail and page host in these locations; verify by resizing the running window because the threshold transition is live behavior.

- [ ] **Step 4: Build and run presentation tests**

Run: `dotnet test tests/SonicDesktopRelay.Presentation.Tests`
Run: `dotnet build src/SonicDesktopRelay.App/SonicDesktopRelay.App.csproj`
Expected: the view model page state remains valid and compiled bindings/styles resolve.

- [ ] **Step 5: Commit the responsive shell**

```powershell
git add src/SonicDesktopRelay.App/Views/MainWindow.axaml src/SonicDesktopRelay.App/Views/MainWindow.axaml.cs
git commit -m "feat(ui): refactor responsive FrameRelay shell"
```

## Task 3: Share page

**Files:**
- Modify: `src/SonicDesktopRelay.App/Views/ShareView.axaml`
- Modify: `src/SonicDesktopRelay.App/Views/ShareView.axaml.cs`
- Use: `src/SonicDesktopRelay.App/Shell.cs`
- Use: `src/SonicDesktopRelay.App/Styles/Components.axaml`

**Interfaces:**
- Keep existing bindings to `Monitors`, `SelectedMonitor`, `ShareQualities`, `SelectedShareQuality`, `ShareFrameRates`, `SelectedShareFrameRate`, `ViewModel.Code`, `ViewModel.StatusText`, `ViewModel.CanShare`, `ViewModel.CanStop`, and `Shell.ShareAsync`/`StopAsync`.
- Display only real supported session/viewer state; unknown preview telemetry reads `---` and unsupported audio controls are unavailable cards.

- [ ] **Step 1: Restructure the Share page into reflowing cards**

Use a `ScrollViewer` around the page content, a page heading, and reflowing card rows. Include display preview, monitor selector, quality/frame-rate controls, session code/copy, session status, viewer count sourced from the runtime snapshot, connection summary, system-audio affordance, and primary start/stop action. Bind the primary action to the existing handlers and keep `Copy code` enabled only when `ViewModel.Code` exists.

- [ ] **Step 2: Mark unsupported Share features and values explicitly**

Apply the shared `.unavailable` state and `IsEnabled="False"` to prototype-only actions/cards. Display literal `---` for latency, bitrate, codec, or preview metrics that have no existing source. Do not bind made-up defaults such as `0 ms` or a simulated viewer list.

- [ ] **Step 3: Build and run existing presentation tests**

Run: `dotnet build src/SonicDesktopRelay.App/SonicDesktopRelay.App.csproj`
Run: `dotnet test tests/SonicDesktopRelay.Presentation.Tests`
Expected: Share compiled bindings resolve to existing Shell/ViewModel members and the existing view model state tests pass.

- [ ] **Step 4: Commit the Share page**

```powershell
git add src/SonicDesktopRelay.App/Views/ShareView.axaml src/SonicDesktopRelay.App/Views/ShareView.axaml.cs
git commit -m "feat(ui): redesign responsive Share page"
```

## Task 4: Watch page

**Files:**
- Modify: `src/SonicDesktopRelay.App/Views/WatchView.axaml`
- Modify: `src/SonicDesktopRelay.App/Views/WatchView.axaml.cs` only if layout integration requires it
- Use: `src/SonicDesktopRelay.App/Controls/VideoSurface.cs`

**Interfaces:**
- Preserve `CodeBox`, `WatchButton`, `Surface`, `OnCodeChanged`, `OnWatch`, `OnStop`, `OnFrame`, and existing window `KeyDown` handlers.
- Preserve `Shell.WatchAsync`, `Shell.StopAsync`, `Shell.ViewModel.StatusText`, and `Shell.IsVideoFullScreen`.

- [ ] **Step 1: Lay out Watch controls and video surface responsively**

Create a scrollable page header/session-code/action area and a flexible video region. The video region must have a bounded width, fill available height/width, preserve its content aspect ratio, and allow vertical scrolling when the window is very small. Arrange connection/session details beside the preview on wide content and below it when narrow using container queries or reflowing panels.

- [ ] **Step 2: Preserve fullscreen and unsupported controls**

Keep the existing F11/Esc event path and restore-state behavior unchanged. Keep the shell rail and page controls hidden in fullscreen as they are today. Retain supported join/leave actions; render unavailable fit/audio/telemetry affordances disabled with `---` for unknown values.

- [ ] **Step 3: Build and run geometry/presentation tests**

Run: `dotnet build src/SonicDesktopRelay.App/SonicDesktopRelay.App.csproj`
Run: `dotnet test tests/SonicDesktopRelay.Presentation.Tests`
Expected: XAML compiles and existing `VideoSurfaceGeometryTests` continue to pass.

- [ ] **Step 4: Commit the Watch page**

```powershell
git add src/SonicDesktopRelay.App/Views/WatchView.axaml src/SonicDesktopRelay.App/Views/WatchView.axaml.cs
git commit -m "feat(ui): redesign responsive Watch page"
```

## Task 5: Diagnostics page

**Files:**
- Modify: `src/SonicDesktopRelay.App/Views/DiagnosticsView.axaml`
- Modify: `src/SonicDesktopRelay.App/Views/DiagnosticsView.axaml.cs` only if the view needs a presentation-only event handler
- Use: `src/SonicDesktopRelay.App/Shell.cs`
- Use: `tests/SonicDesktopRelay.Presentation.Tests/SignalingDiagnosticsTests.cs`

**Interfaces:**
- Keep `MediaStatusText`, `LogDirectory`, `Diagnostics`, `SignalingDiagnostics`, and `WebRtcDiagnostics` as current real sources.
- Keep event content metadata-only; do not change how diagnostic entries are collected or what payload they store.

- [ ] **Step 1: Organize diagnostics into responsive summaries and grouped logs**

Add a heading and compact subsystem/status cards, then present the existing session, signaling, and WebRTC diagnostic collections in labeled log areas. Use reflowing summary cards and a one-column log stack at narrow sizes. Keep event logs selectable and vertically scrollable.

- [ ] **Step 2: Distinguish real diagnostics from unavailable prototype metrics**

Bind existing media/session/status content directly. Show `---` where the prototype requests a metric without a current source. Render filters, exports, or copy actions as unavailable only when no existing behavior supports them; do not add a handler that simulates success.

- [ ] **Step 3: Run diagnostics tests and App build**

Run: `dotnet test tests/SonicDesktopRelay.Presentation.Tests --filter FullyQualifiedName~SignalingDiagnosticsTests`
Run: `dotnet build src/SonicDesktopRelay.App/SonicDesktopRelay.App.csproj`
Expected: diagnostics metadata tests pass and the grouped view compiles.

- [ ] **Step 4: Commit Diagnostics**

```powershell
git add src/SonicDesktopRelay.App/Views/DiagnosticsView.axaml src/SonicDesktopRelay.App/Views/DiagnosticsView.axaml.cs
git commit -m "feat(ui): redesign responsive Diagnostics page"
```

## Task 6: Settings page

**Files:**
- Modify: `src/SonicDesktopRelay.App/Views/SettingsView.axaml`
- Modify: `src/SonicDesktopRelay.App/Views/SettingsView.axaml.cs` only if necessary for presentation behavior
- Use: `src/SonicDesktopRelay.App/Shell.cs`

**Interfaces:**
- Keep `BackendAddress`, `IsBackendAddressValid`, `DeviceName`, and current two-way text bindings unchanged.
- Preserve the existing save-on-valid-backend and device-registration semantics in `Shell`.

- [ ] **Step 1: Recompose Settings as responsive cards/sections**

Use shared card/field styles and width-filling text boxes for backend address and device name. Allow labels/help text to wrap. Use a reflowing layout for prototype sections so narrow widths become one column without clipping the editable fields.

- [ ] **Step 2: Represent unimplemented prototype settings as disabled**

Place each unsupported settings group in the shared unavailable-card style, set the entire card disabled, and add a subtle “Coming soon” label only where needed to explain why it cannot be edited. Do not add configuration properties or persistence for these mock settings.

- [ ] **Step 3: Build and run existing backend/settings presentation tests**

Run: `dotnet build src/SonicDesktopRelay.App/SonicDesktopRelay.App.csproj`
Run: `dotnet test tests/SonicDesktopRelay.Core.Tests --filter FullyQualifiedName~BackendSettingsTests`
Run: `dotnet test tests/SonicDesktopRelay.Presentation.Tests`
Expected: current backend address validation/persistence tests and page-state tests pass.

- [ ] **Step 4: Commit Settings**

```powershell
git add src/SonicDesktopRelay.App/Views/SettingsView.axaml src/SonicDesktopRelay.App/Views/SettingsView.axaml.cs
git commit -m "feat(ui): redesign responsive Settings page"
```

## Task 7: Integrated verification and PR

**Files:**
- Verify: all App views and styles in Tasks 1–6
- Verify: `SonicDesktopRelay.sln`
- Update: focused tests in `tests/SonicDesktopRelay.Presentation.Tests` only where a presentation regression has a practical unit-test seam

**Interfaces:**
- No new runtime interfaces. Confirm the UI still consumes existing sources and commands only.

- [ ] **Step 1: Run full solution tests and build**

Run: `dotnet test SonicDesktopRelay.sln`
Run: `dotnet build SonicDesktopRelay.sln`
Expected: all tests pass and the solution builds with warnings treated as errors.

- [ ] **Step 2: Launch the App and inspect the wide layout**

Run: `dotnet run --project src/SonicDesktopRelay.App/SonicDesktopRelay.App.csproj`
Inspect Share, Watch, Diagnostics, and Settings against the supplied issue images and Stitch export. Confirm shell branding/status, active navigation, card hierarchy, and available data.

- [ ] **Step 3: Resize through compact and very small desktop widths**

While the App is running, resize from wide to narrow and back. Confirm live rail compaction, card/column reflow, visible actions/values, no main-page horizontal scrolling, and preserved video aspect ratio. Verify that vertical scrolling remains available at the smallest supported size.

- [ ] **Step 4: Exercise behavior and disabled affordances**

Start/stop sharing where a monitor/backend is available; copy a real session code; join/leave a session where available; test F11 and Esc; edit/save a valid backend address and device name; inspect real diagnostics. Tab through unavailable cards and attempt pointer/keyboard activation; confirm they stay inert and unknown values remain `---`.

- [ ] **Step 5: Review final diff and whitespace**

Run: `git diff --check`
Run: `git status --short`
Review that no media, WebRTC, signaling, backend, storage, or namespace changes entered the branch.

- [ ] **Step 6: Commit any final verification fixes and publish the PR**

Commit focused fixes with descriptive messages, push `feature/8-avalonia-ui-refactor`, then open a PR titled `Refactor Avalonia UI to match FrameRelay prototypes` that references `Closes #8`. Include build/test results and the manual resize/behavior checks in the PR description.
