# FrameRelay Windows MSI

The WiX Toolset SDK is pinned to 5.0.1 in `FrameRelay.wixproj`. Build the MSI from the self-contained Windows x64 publish directory by setting the `PublishDirectory` and numeric `ProductVersion` MSBuild properties. CI assigns the final release filename after the package builds.

The MSI installs the application under `%ProgramFiles%\FrameRelay` and creates a FrameRelay Start Menu shortcut. Windows Installer owns the installed application files, shortcut, and uninstall registration. It does not own or remove files under `%LOCALAPPDATA%\FrameRelay`, which keeps logs and per-user configuration across upgrade and uninstall.

The shortcut is placed under the all-users Programs menu in a FrameRelay folder. Its component uses a per-user registry key as its Windows Installer key path, as required by ICE validation for a non-advertised Start Menu shortcut. The package itself and application files are installed per-machine.

The UpgradeCode is `{8A8D6B71-9AF1-4CB1-AC70-88BA688C734B}`. This identifier must remain unchanged across releases, including any future product display-name change. The Start Menu component GUID is also stable across releases. Publish payload components are generated deterministically from their install paths by WiX's `Files` authoring.
