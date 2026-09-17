# FrameRelay

FrameRelay shares a Windows screen and its system audio with other Windows machines over the shared [RelayControl](https://github.com/vitorhugo-dotnet/dotnet_SonicRelay) control plane. It is an Avalonia desktop app: pick a monitor, get a six-character code, and everyone who enters it watches the same encoded stream over WebRTC.

FrameRelay is a separate product from **SonicRelay**. SonicRelay focuses on low-latency system-audio streaming to mobile viewers; FrameRelay focuses on desktop screen sharing while reusing the same device identity, session, signaling and TURN infrastructure.

## Requirements

To run a release:

- Windows 10 build 19041 or later. Nothing else — **FFmpeg is bundled**, so there is no separate
  download and no `winget install` step.

To build from source:

- .NET 10 SDK.
- Network access on the first build: it fetches the pinned FFmpeg 8.1.1 shared build once
  (about 100 MB, cached under `artifacts/ffmpeg/`) and copies the libraries into every build
  output. `-p:EmbedFFmpegRuntime=false` skips that and uses a system install instead. See
  [docs/screen-publishing.md](docs/screen-publishing.md#ffmpeg-requirement).

The bundled FFmpeg is GPL v3; see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for what that
means for redistribution.

## Build and test

```bash
dotnet build SonicDesktopRelay.sln
dotnet test SonicDesktopRelay.sln
dotnet run --project src/SonicDesktopRelay.App
```

> The solution, project and namespace identifiers still use `SonicDesktopRelay.*` internally. The public product name is **FrameRelay**; renaming internal identifiers is intentionally outside this branding-only change.

Tests that need a display skip themselves when there is none; everything else runs on fakes. The
FFmpeg tests do not skip — the build hands them the same libraries the app ships.

## Projects

| Project | TFM | Responsibility |
|---|---|---|
| `SonicDesktopRelay.Core` | `net10.0` | Device identity, credential storage, settings |
| `SonicDesktopRelay.ApiClient` | `net10.0` | Typed HTTP: devices, sessions, ICE servers |
| `SonicDesktopRelay.Signaling` | `net10.0` | WebSocket signaling, envelope, reconnection |
| `SonicDesktopRelay.Media` | `net10.0` | Platform-neutral media contracts, the publish and watch pipelines |
| `SonicDesktopRelay.Media.Windows` | `net10.0-windows10.0.19041.0` | Windows.Graphics.Capture and the FFmpeg H.264 encoder and decoder |
| `SonicDesktopRelay.Rtc` | `net10.0` | Peer connections, both halves of negotiation, fan-out to N viewers |
| `SonicDesktopRelay.Presentation` | `net10.0` | Session state machine and view models |
| `SonicDesktopRelay.App` | `net10.0-windows10.0.19041.0` | Avalonia shell and composition root |

The App carries a Windows TFM because MSBuild cannot reference a `net10.0-windows` project from
a `net10.0` one, and the App is the only assembly that composes `Media.Windows`. Every library
below it stays platform-neutral, which is what lets the whole presentation layer be tested
without a GPU.

## Related projects

- [RelayControl](https://github.com/vitorhugo-dotnet/dotnet_SonicRelay) — shared device identity, pairing, sessions, signaling and TURN credentials.
- [SonicRelay Desktop](https://github.com/vitorhugo-dotnet/desktop_dotnet_SonicRelay) — system-audio publisher for SonicRelay.
- [SonicRelay Mobile](https://github.com/vitorhugo-dotnet/flutter_mobile-web_SonicRelay) — mobile SonicRelay audio viewer.

## Documentation

- [Screen publishing and watching](docs/screen-publishing.md) — capture, encoder and decoder
  selection, the FFmpeg requirement, the quality ladder, and why a stall is not a
  disconnection.
- [Design spec](docs/superpowers/specs/2026-08-23-sonicdesktoprelay-design.md).
- [Third-party notices](THIRD-PARTY-NOTICES.md) — the bundled FFmpeg and its licence.