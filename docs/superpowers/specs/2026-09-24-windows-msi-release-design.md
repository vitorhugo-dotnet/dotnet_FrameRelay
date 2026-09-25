# Windows MSI Release Design

## Goal

Distribute FrameRelay as a conventional Windows x64 application through a signed-off MSI asset in GitHub Releases, while keeping the existing ZIP and single-file EXE and preserving per-user data through upgrades and uninstall.

## Current context

The release workflow publishes a self-contained `win-x64` directory and a single-file executable. The app targets `net10.0-windows10.0.19041.0`. The UI assembly and executable still use legacy `SonicDesktopRelay.*` names, while the public product name and persistent diagnostics path use FrameRelay. Persistent logs live under `%LOCALAPPDATA%\FrameRelay\logs`; app settings/credentials are also user-owned data and must remain outside the install directory.

## Design

- Add a WiX Toolset v5 packaging project under `packaging/`. Pin the WiX major/minor toolchain used by CI and build a per-machine x64 MSI from `artifacts/publish/win-x64`.
- Give the product a stable UpgradeCode and stable component identities. Set the MSI product/display name to `FrameRelay`, publisher to the repository owner/project, and ProductVersion from the already resolved release version. Keep the executable's current filename so the package consumes the existing publish output without renaming application binaries.
- Install application payload under `%ProgramFiles%\FrameRelay`. Register the MSI with Windows Installer/ARP, provide a Start Menu shortcut, and set uninstall metadata. Use WiX major-upgrade handling so a newer release upgrades the same product and older releases cannot replace newer versions.
- Do not author or remove files under `%LOCALAPPDATA%`; uninstall removes only installed application files and installer-owned shortcuts/registration. This preserves logs, credentials, and settings.
- Add an application icon only if a suitable project icon exists; do not create a new visual identity as part of packaging.
- Extend the existing Windows release workflow to build the MSI after publish, validate MSI metadata and payload, name it `FrameRelay-win-x64-<version>.msi`, checksum it alongside the ZIP and EXE, and upload it with those assets. Keep tag releases stable and manual builds prereleases.
- Add workflow/package verification for payload presence, stable product identity, asset/checksum inclusion, and upgrade behavior where Windows Installer testing is practical. CI should install, upgrade to a second version, and uninstall in an isolated runner while confirming user data survives.

## Scope

This change adds Windows MSI packaging and release automation. It does not replace the ZIP or EXE, add auto-update behavior, or package Linux/macOS builds. It does not bundle FFmpeg or other removed codec runtimes.

## Acceptance criteria

1. WiX v5 builds an x64 MSI from the self-contained publish output with the requested stable FrameRelay product identity.
2. Installation creates a Programs and Features/Installed apps entry, Start Menu shortcut, and launchable app under Program Files.
3. An upgrade retains one installed-app entry and replaces the prior installation; uninstall removes installed app files and leaves user data/logs intact.
4. Release workflow uploads `FrameRelay-win-x64-<version>.msi` and includes it in `checksums-sha256.txt` while preserving current ZIP/EXE and stable/prerelease semantics.
5. The MSI payload contains the expected executable/runtime files and no removed FFmpeg artifacts.
