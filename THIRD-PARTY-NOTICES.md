# Third-party notices

FrameRelay/SonicDesktopRelay source is MIT licensed; see [LICENSE](LICENSE). The Windows media
path uses maintained .NET bindings/libraries whose own licenses remain in force.

## Vortice.Windows

- **Components**: `Vortice.MediaFoundation` and `Vortice.Direct3D11` 3.8.3.
- **Purpose**: .NET bindings for Windows Media Foundation and Direct3D 11.
- **License**: MIT.
- **Upstream**: <https://github.com/amerkoleci/Vortice.Windows>.

## NAudio

- **Component**: `NAudio.Wasapi` 3.1.0.
- **Purpose**: WASAPI loopback capture, render-device access and Windows audio interop.
- **License**: MIT.
- **Upstream**: <https://github.com/naudio/NAudio>.

These packages are restored from NuGet as normal managed dependencies. The application does not
download or redistribute a separate third-party H.264 runtime; H.264 video is provided by the
Media Foundation transforms available in supported Windows installations.
