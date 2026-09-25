# Windows MSI Release Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Produce a conventional FrameRelay Windows x64 MSI and publish it beside the existing ZIP and single-file EXE.

**Architecture:** Add an SDK-style WiX v5 project that packages the already self-contained publish directory with a fixed UpgradeCode and explicit install/uninstall ownership. Extend Windows release CI to build and inspect the MSI, perform an install/upgrade/uninstall smoke test, then include the MSI in release upload and checksums without changing stable versus prerelease behavior.

**Tech Stack:** .NET 10 SDK, WiX Toolset SDK 5.0.1, Windows Installer (`msiexec`), PowerShell, GitHub Actions.

**Spec:** `docs/superpowers/specs/2026-09-24-windows-msi-release-design.md`

## Global Constraints

- Build a per-machine x64 MSI from the self-contained `win-x64` output.
- Keep product name `FrameRelay`, stable UpgradeCode/component identities, and derive ProductVersion from the resolved release version.
- Treat equal MSI ProductVersions as upgradeable so successive prereleases that share one numeric core replace the installed copy; MSI cannot order same-core prerelease labels.
- Install under `%ProgramFiles%\FrameRelay`; never author or remove `%LOCALAPPDATA%` data.
- Keep existing ZIP and EXE artifacts and existing stable/prerelease release semantics.
- Do not add FFmpeg or removed codec runtime artifacts.
- Pin WiX Toolset v5 for unattended builds; use SDK version `5.0.1`.

## Review Focus

- Prerelease strings such as `0.0.0-alpha.pr42.110` are not valid Windows Installer ProductVersion values; normalize deterministically to the three numeric MSI fields while retaining the original full release version in the asset filename and release notes.
- MSI major upgrades require the same UpgradeCode. CI must exercise replacement by an equal numeric ProductVersion because successive prereleases share a core; lower numeric versions must still be rejected.
- Uninstall must leave `%LOCALAPPDATA%\FrameRelay` logs, credentials, and settings intact; create this data during the integration test and assert it survives.
- Dynamic publish payloads can gain or lose files; verify all publish files, including the executable and .NET runtime payload, are represented in the MSI.
- Installation must not create duplicate ARP entries, and Start Menu shortcut removal must not remove unrelated user files.

---

### Task 1: Add the WiX v5 MSI project

**Files:**
- Create: `packaging/FrameRelay/FrameRelay.wixproj`
- Create: `packaging/FrameRelay/Package.wxs`
- Create: `packaging/FrameRelay/README.md`

**Interfaces:**
- Consumes: `PublishDirectory` (absolute path to the existing self-contained publish output) and `ProductVersion` (numeric MSI version) MSBuild properties.
- Produces: `artifacts/release/FrameRelay-win-x64-<full-release-version>.msi` when invoked by the build script in Task 2.

- [ ] **Step 1: Define the project and package authoring.** Pin `WixToolset.Sdk/5.0.1`. Author a per-machine x64 `Package` with name `FrameRelay`, project publisher metadata, a fixed UpgradeCode, major-upgrade handling that includes equal versions, and `MediaTemplate`. Suppress ICE61 only because it rejects the inclusive equal-version boundary. Install the publish directory contents under `ProgramFiles64Folder\FrameRelay`. Add a Start Menu shortcut whose component is removed by Windows Installer. Keep the shortcut icon reference omitted because the app repository has no `.ico` asset. Do not author LocalAppData directories or files.
- [ ] **Step 2: Confirm all authored paths and identities.** Check in an explanatory README containing the stable UpgradeCode, build properties, install location, and the rule that a future product rename must retain the UpgradeCode. Confirm each authored component has a stable identity and uses a key path.
- [ ] **Step 3: Build a local MSI from a representative publish directory.** Run `dotnet build packaging/FrameRelay/FrameRelay.wixproj -p:PublishDirectory=<absolute-publish-dir> -p:ProductVersion=1.2.3 -p:OutputPath=<absolute-temp-dir>`. Expected: WiX compiles the package and emits one `.msi` without missing-file or duplicate-component warnings.
- [ ] **Step 4: Inspect generated MSI metadata.** Use Windows Installer database inspection in Task 3's verification script; confirm ProductName, Manufacturer, ProductVersion, UpgradeCode, x64 platform, install directory, shortcut, and application executable.

### Task 2: Add a deterministic MSI build script and version tests

**Files:**
- Create: `.github/scripts/Build-FrameRelayMsi.ps1`
- Create: `tests/Build-FrameRelayMsi.Tests.ps1`
- Modify: `packaging/FrameRelay/FrameRelay.wixproj`

**Interfaces:**
- Consumes: `-PublishDirectory`, `-ReleaseVersion`, and `-OutputDirectory` parameters.
- Produces: the named MSI path and a validated numeric `ProductVersion` passed to the WiX project.

- [ ] **Step 1: Write a failing version/build-contract test.** Add standalone PowerShell assertions for `1.2.3 -> 1.2.3`, `0.0.0-alpha.pr42.110 -> 0.0.0`, invalid/non-semver input rejection, and the resulting file name retaining the unmodified full release version. Also assert the script fails clearly when the publish directory lacks `SonicDesktopRelay.App.exe`.
- [ ] **Step 2: Run the contract test and observe expected failures.** Run `pwsh -NoProfile -File tests/Build-FrameRelayMsi.Tests.ps1`. Expected: FAIL because the builder/version conversion does not exist yet.
- [ ] **Step 3: Implement the minimal build script.** Parse the leading three numeric semantic-version components, reject malformed/out-of-range MSI fields, retain the original version for the MSI filename, verify the publish directory and expected executable exist, call `dotnet build` with the exact project and MSBuild properties, and fail on a nonzero exit code or missing output MSI.
- [ ] **Step 4: Run the contract test.** Run `pwsh -NoProfile -File tests/Build-FrameRelayMsi.Tests.ps1`. Expected: PASS for stable version, prerelease normalization, invalid version, missing executable, and artifact naming.
- [ ] **Step 5: Build and open a real MSI.** On Windows, invoke the script against a published x64 app and inspect properties with the Windows Installer COM database API. Expected: numeric product version and the exact `FrameRelay-win-x64-<full-release-version>.msi` output.

### Task 3: Verify MSI metadata and payload

**Files:**
- Create: `.github/scripts/Test-FrameRelayMsi.ps1`
- Create: `tests/Test-FrameRelayMsi.Tests.ps1`

**Interfaces:**
- Consumes: `-MsiPath`, `-PublishDirectory`, and expected name/version metadata.
- Produces: nonzero exit on any missing payload, identity mismatch, unexpected codec artifact, or checksum omission; zero on success.

- [ ] **Step 1: Write failing verification tests using a fixture MSI or mocked MSI database reader.** Cover wrong ProductName, changed UpgradeCode, missing app EXE, missing payload file, unexpected `avcodec-`, `avutil-`, `swscale-`, `swresample-`, or `FFmpeg.AutoGen` member, and absent MSI checksum entry.
- [ ] **Step 2: Run the verification test and observe expected failures.** Run `pwsh -NoProfile -File tests/Test-FrameRelayMsi.Tests.ps1`. Expected: FAIL because the verifier is not implemented.
- [ ] **Step 3: Implement Windows Installer database inspection.** Open the MSI read-only using `WindowsInstaller.Installer` COM, query the Property table for `ProductName`, `Manufacturer`, `ProductVersion`, `UpgradeCode`, and `ARPNOREPAIR`; query the File/Component/Directory tables to verify expected installed names and architecture; compare every file under `PublishDirectory` against the packaged file table and reject removed codec tokens. Always close COM handles in `finally`.
- [ ] **Step 4: Run verifier tests on Windows.** Run `pwsh -NoProfile -File tests/Test-FrameRelayMsi.Tests.ps1`. Expected: all positive and negative cases pass and COM handles are released.

### Task 4: Extend release asset creation and checksums

**Files:**
- Modify: `.github/workflows/release.yml`
- Modify: `.github/scripts/Build-FrameRelayMsi.ps1`
- Modify: `.github/scripts/Test-FrameRelayMsi.ps1`
- Modify: `tests/Build-FrameRelayMsi.Tests.ps1`

**Interfaces:**
- Consumes: resolved `$version`, self-contained `artifacts/publish/win-x64`, and release tag/prerelease outputs.
- Produces: ZIP, EXE, MSI, and a checksum file covering all three package artifacts.

- [ ] **Step 1: Add workflow contract assertions before changing the workflow.** Extend the release workflow test to assert WiX SDK 5.0.1 restore/build, MSI build after publish, MSI verifier invocation, MSI checksum inclusion, MSI upload inclusion, and that existing `--verify-tag`/`--latest` and `--prerelease` paths remain present.
- [ ] **Step 2: Run the workflow contract test and observe expected failures.** Run `pwsh -NoProfile -File tests/Build-FrameRelayMsi.Tests.ps1`. Expected: the newly added workflow assertions fail because the release workflow has not been updated.
- [ ] **Step 3: Update the Windows release job.** Build the MSI from `artifacts/publish/win-x64` after publish, then run metadata/payload validation. Generate the MSI checksum along with existing `.zip` and `.exe` checksums. Add the MSI path to the `assets` outputs and GitHub Release asset array. Leave old ZIP/EXE names and release flags unchanged.
- [ ] **Step 4: Run release and script contract tests.** Run `pwsh -NoProfile -File tests/Build-FrameRelayMsi.Tests.ps1` and the existing `pwsh -NoProfile -File tests/Publish-GitHubRelease.Tests.ps1` on a POSIX-capable host (it intentionally skips on Windows). Expected: MSI workflow assertions pass and the publish helper behavior remains unchanged.

### Task 5: Add install, upgrade, launch, and uninstall CI smoke test

**Files:**
- Create: `.github/scripts/Test-FrameRelayMsiLifecycle.ps1`
- Create: `tests/Test-FrameRelayMsiLifecycle.Tests.ps1`
- Modify: `.github/workflows/release.yml`

**Interfaces:**
- Consumes: first and second MSI paths, isolated install directory, and temporary user data root.
- Produces: successful install/launch/upgrade/uninstall proof or a failing CI step with captured MSI log paths.

- [ ] **Step 1: Write lifecycle test assertions.** Exercise script orchestration with disposable fixtures and assert the sequence is first install, app launch, second-version major upgrade, single ARP entry, app launch after upgrade, uninstall, app files absent, and seeded `%LOCALAPPDATA%\FrameRelay` files still present.
- [ ] **Step 2: Run the lifecycle test and observe expected failures.** Run `pwsh -NoProfile -File tests/Test-FrameRelayMsiLifecycle.Tests.ps1`. Expected: FAIL because the lifecycle script does not exist.
- [ ] **Step 3: Implement isolated MSI lifecycle validation.** Build a second package with the same numeric ProductVersion but a distinct full release version and the same UpgradeCode. Install via quiet `msiexec` with verbose logs, start `SonicDesktopRelay.App.exe` and verify process startup, seed user data before the install, upgrade via the second MSI, query uninstall registration for exactly one FrameRelay product, uninstall via `msiexec`, and assert only MSI-owned install/shortcut files were removed while seeded user data remains. Also reject a lower numeric version in preflight. Use a unique runner-local test root and always uninstall/cleanup in `finally`.
- [ ] **Step 4: Run the lifecycle test locally on Windows.** Run `pwsh -NoProfile -File tests/Test-FrameRelayMsiLifecycle.Tests.ps1`. Expected: install, launch, upgrade, and uninstall all pass; user data survives.
- [ ] **Step 5: Wire lifecycle validation into release CI.** Build a second MSI from the same publish output with a distinct prerelease filename but equal numeric ProductVersion, then run the lifecycle script before release asset creation. Do not upload the second test MSI.
- [ ] **Step 6: Verify CI workflow parsing and the complete Windows job.** Run the complete existing .NET release tests plus all three MSI PowerShell scripts locally; CI must finish the install/upgrade/uninstall sequence before publishing assets.

### Task 6: Document MSI installation and artifact versioning

**Files:**
- Modify: `README.md`
- Modify: `packaging/FrameRelay/README.md`

**Interfaces:**
- Documents: stable release MSI naming, installation/uninstall path behavior, persistent user data location, and numeric MSI-version mapping for prerelease builds.

- [ ] **Step 1: Document the end-user path.** Add a release download note identifying `FrameRelay-win-x64-<version>.msi`, explain that Windows Installed apps handles uninstall/upgrade, and state `%LOCALAPPDATA%\FrameRelay` user data persists through removal.
- [ ] **Step 2: Document prerelease version mapping.** Explain that Windows Installer accepts numeric three-field ProductVersion, so MSI metadata uses the first three numeric release components (for example `0.0.0-alpha.pr42.110` maps to `0.0.0`) while the downloadable MSI filename retains the full release version.
- [ ] **Step 3: Review docs and final diffs.** Confirm the README does not imply MSI replaces the portable ZIP or single-file EXE, and no FFmpeg/runtime artifacts were added.

## Final verification

- Run `dotnet build SonicDesktopRelay.sln --configuration Release --no-restore` and `dotnet test SonicDesktopRelay.sln --configuration Release --no-build --no-restore` on Windows.
- Run `pwsh -NoProfile -File tests/Build-FrameRelayMsi.Tests.ps1`, `pwsh -NoProfile -File tests/Test-FrameRelayMsi.Tests.ps1`, and `pwsh -NoProfile -File tests/Test-FrameRelayMsiLifecycle.Tests.ps1` on Windows.
- Run the existing release publishing helper test on a POSIX-capable runner; it skips by design on Windows.
- Inspect the final ZIP, EXE, MSI, and checksum manifest; ensure each checksum points to an existing file and the MSI includes the full publish payload.
