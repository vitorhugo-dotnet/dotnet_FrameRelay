# FrameRelay

FrameRelay is a Windows desktop screen-sharing client built on .NET 10 and Avalonia. The current
desktop project names still use the legacy `SonicDesktopRelay.*` namespace; this migration does
not rename them.

## Runtime requirements

- Windows 10 build 19041 or later.
- Windows 10 build 20348 or later for audio from a selected application window; older builds
  can still share window video without audio.
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
Monitor: Windows.Graphics.Capture -> BGRA -> NV12 -> Media Foundation H.264 -> WebRTC
         WASAPI system loopback -> PCM 48 kHz stereo -> Opus ---------^
Window:  Windows.Graphics.Capture -> BGRA -> NV12 -> Media Foundation H.264 -> WebRTC
         selected process tree -> PCM 48 kHz stereo -> Opus ----------^
```

The Share page can target a monitor or an eligible application window. Window audio is limited
to the selected process and its child processes. It never falls back to system loopback; when
process capture is unsupported or unavailable, viewers receive window video only. See
[screen publishing and watching](docs/screen-publishing.md) for target and diagnostics details.

Watching mirrors that path:

```text
WebRTC -> Media Foundation H.264 -> NV12 -> BGRA -> Avalonia surface
       -> Opus -> PCM 48 kHz stereo -> WASAPI render endpoint
```

The H.264 encoder and decoder enumerate compatible Media Foundation hardware transforms
without requiring a particular GPU vendor, then fall back to Windows system software transforms
on the CPU. This fallback provides a compatibility path when suitable hardware is unavailable
or fails at runtime; it is not a performance guarantee, and CPU encoding/decoding may reduce
the achievable frame rate on some machines.

Audio and video are stamped from the same `MediaSessionClock`. The RTC layer stays P2P-first
with the backend-provided ICE servers and uses TURN only when direct connectivity cannot be
established. Once ICE nominates a pair, Diagnostics classifies the active path as Direct/TURN and
UDP/TCP using candidate metadata only; addresses, ports, candidate bodies and credentials never
cross that diagnostics boundary.

Before sharing, the publisher can choose a 1080p/720p/540p/360p quality ceiling and 15/30/60 FPS
ceiling. Those values are real media controls: capture cadence, Media Foundation configuration
and RTP video timestamps follow the effective FPS. The publisher sends one shared stream, so
automatic quality control protects the slowest viewer and may lower quality for everyone; it
never exceeds the publisher's selected ceiling. Receiver feedback is sampled every 2 seconds.
Sustained loss of at least 5% or decoded throughput below 85% of target for 5 seconds can step
quality down, with at least 15 seconds between changes. Recovery requires fresh, continuous
healthy feedback for 30 seconds (at most 1% loss and at least 95% of target decoded FPS), then
raises quality one step at a time. The ladder reduces bitrate before resolution and FPS.

Recovery keyframes use Media Foundation `ICodecAPI`/`CODECAPI_AVEncVideoForceKeyFrame` when
the selected encoder supports it, so a PLI/FIR does not normally rebuild the H.264 transform.
Unsupported transforms retain a diagnosed reconfigure fallback for correctness.

The Diagnostics screen reports the live selected native transform, hardware/software path,
formats, effective geometry/FPS/bitrate, keyframe mode and recovery latency, sampled encode/send
timings, selected Direct/TURN transport classification, candidate rejection reasons, WASAPI
endpoint state, Opus codec state, session state, and signaling metadata. SDP, ICE candidate
contents, addresses/ports, credentials, and media payloads are not recorded.

## Persistent diagnostic logs

FrameRelay writes local diagnostic logs automatically. They are intended for reproducing issues
such as a frozen picture, decoder failure, signaling reconnect, or a frame that reaches WebRTC
but never reaches the Avalonia surface.

Logs are stored per Windows user under:

```text
%LOCALAPPDATA%\FrameRelay\logs\
```

The Diagnostics screen also displays the resolved log directory for the current machine.

A new rolling file is used for each day:

```text
FrameRelay-YYYYMMDD.log
```

The file sink keeps the most recent 14 daily log files. Logging is enabled from Trace through
Critical severity. Because Serilog names the .NET Trace level `Verbose`, Trace entries appear as
`[VRB]` in the file.

Each entry includes a timestamp, severity, logger/source context, managed thread id, structured
properties, and exception details when present. Unhandled process exceptions, unobserved Task
exceptions, and unhandled Avalonia UI-thread exceptions are also recorded.

Media diagnostics include enough boundary information to determine where a video session stopped
making progress, including capture/encode counts, received H.264 access units, keyframes,
decoder results and failures, recovery keyframe requests, decoded frames, UI delivery, surface
presentation, and sampled render activity. Media Foundation failures include the decoder stage,
exception type, HRESULT, and stack trace where available. Decoder output diagnostics also record
the Media Foundation output-stream flags, `PROVIDES_SAMPLES` / `CAN_PROVIDE_SAMPLES`, selected
caller-vs-MFT allocation mode, whether FrameRelay supplied a sample, whether `ProcessOutput`
returned a sample, and sampled HRESULT/stream-change results. Per-frame details stay at
Debug/Trace rather than Information.

Signaling and WebRTC logging is metadata-only. FrameRelay does **not** write SDP bodies, ICE
candidate contents, credentials, or audio/video payloads to the diagnostic log.

To open the log directory from PowerShell:

```powershell
explorer.exe "$env:LOCALAPPDATA\FrameRelay\logs"
```

When reporting a media freeze, reproduce the problem first and attach the log file for that day.
The most useful comparison is whether received access-unit counters keep increasing after decoded,
UI-delivered, or rendered-frame counters stop.

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
