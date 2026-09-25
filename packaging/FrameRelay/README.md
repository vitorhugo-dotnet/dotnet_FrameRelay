# FrameRelay Windows MSI

The WiX Toolset SDK is pinned to 5.0.1 in `FrameRelay.wixproj`. Build the MSI from the self-contained Windows x64 publish directory by setting the `PublishDirectory` and numeric `ProductVersion` MSBuild properties. The release workflow uses `Build-FrameRelayMsi.ps1` and names the artifact `FrameRelay-win-x64-<full-release-version>.msi`.

Windows Installer accepts a numeric three-part `ProductVersion`. Stable release versions map directly; prerelease metadata is dropped from the numeric MSI version while the full version remains in the download name. For example, release `0.0.0-alpha.pr42.110` produces an MSI with ProductVersion `0.0.0`.

The package allows major upgrades at an equal numeric version because successive prereleases can share the same three-part core version. The WiX project suppresses ICE61, which rejects this inclusive version boundary; other MSI validation checks remain enabled. Windows Installer cannot tell which same-core prerelease is newer, so either package can replace the other.

The MSI installs the application under `%ProgramFiles%\FrameRelay` and creates a FrameRelay Start Menu shortcut. Windows Installer owns the installed application files, shortcut, and uninstall registration. It does not own or remove files under `%LOCALAPPDATA%\FrameRelay`, which keeps logs and per-user configuration across upgrade and uninstall.

The shortcut is placed under the all-users Programs menu in a FrameRelay folder. Its component uses a per-user registry key as its Windows Installer key path, as required by ICE validation for a non-advertised Start Menu shortcut. The package itself and application files are installed per-machine.

The UpgradeCode is `{8A8D6B71-9AF1-4CB1-AC70-88BA688C734B}`. This identifier must remain unchanged across releases, including any future product display-name change. The Start Menu component GUID is also stable across releases. Publish payload components are generated deterministically from their install paths by WiX's `Files` authoring.
