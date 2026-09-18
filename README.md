# FrameRelay

FrameRelay is a Windows desktop screen-sharing client built on .NET 10 and Avalonia. The current
desktop project names still use the legacy `SonicDesktopRelay.*` namespace; this migration does
not rename them.

## Runtime requirements

- Windows 10 build 19041 or later.
- Access to the FrameRelay backend/signaling service.
- No separate video codec runtime installation. Screen video uses the H.264 transforms shipped
  with Windows.

For source builds, install the .NET 10 SDK.

## Build and test

```powershell
dotnet restore SonicDesktopRelay.sln
dotnet build SonicDesktopRelay.sln --configuration Release --no-restore
dotnet test SonicDesktopRelay.sln --configuration Release --no-build --no-restore
```

## Native media path

Publishing uses one media session for every viewer:

```text
Windows.Graphics.Capture -> BGRA -> NV12 -> Media Foundation H.264 -> WebRTC
WASAPI loopback -> PCM 48 kHz stereo -> Opus ----------------------^
```

Watching mirrors that path:

```text
WebRTC -> Media Foundation H.264 -> NV12 -> BGRA -> Avalonia surface
       -> Opus -> PCM 48 kHz stereo -> WASAPI render endpoint
```

The H.264 encoder enumerates hardware Media Foundation transforms first and falls back to a
system software transform when necessary. The decoder also enumerates native transforms and
normalizes output to NV12 before the reusable BGRA render buffer.

Audio and video are stamped from the same `MediaSessionClock`. The RTC layer stays P2P-first
with the backend-provided ICE servers and uses TURN only when direct connectivity cannot be
established.

The Diagnostics screen reports the live selected native transform, hardware/software path,
formats, geometry/bitrate, candidate rejection reasons, WASAPI endpoint state, Opus codec state,
session state, and signaling metadata. SDP, ICE candidate contents, credentials, and media
payloads are not recorded.

## Projects

| Project | Target | Responsibility |
|---|---|---|
| `SonicDesktopRelay.Media` | `net10.0` | Platform-neutral audio/video contracts and pipelines |
| `SonicDesktopRelay.Media.Windows` | `net10.0-windows10.0.19041.0` | Windows.Graphics.Capture, Media Foundation, WASAPI |
| `SonicDesktopRelay.Rtc` | `net10.0` | SIPSorcery peer connections, H.264/Opus transport |
| `SonicDesktopRelay.Presentation` | `net10.0` | Session/view-model state |
| `SonicDesktopRelay.App` | Windows | Avalonia composition root and UI |

## Documentation

- [Screen publishing and watching](docs/screen-publishing.md)
- [Native media validation](docs/native-media-validation.md)
- [Third-party notices](THIRD-PARTY-NOTICES.md)
