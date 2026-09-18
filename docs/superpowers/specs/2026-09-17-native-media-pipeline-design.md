# FrameRelay native Windows media pipeline — design

Status: approved in chat on 2026-09-17 for issue #6.

Issue: https://github.com/vitorhugo-dotnet/dotnet_FrameRelay/issues/6

## Goal

Replace the Windows FFmpeg video runtime with a Windows-native media pipeline and add the system-audio path that the product already describes but does not currently implement.

The resulting Windows stack is:

```text
Publisher
Windows.Graphics.Capture
  -> BGRA frame (initial bridge; D3D11 surface path remains an optimization boundary)
  -> Media Foundation H.264 encoder
  -> H.264 RTP

WASAPI loopback
  -> PCM 48 kHz
  -> Opus
  -> Opus RTP

H.264 + Opus
  -> SIPSorcery RTCPeerConnection
  -> ICE/STUN direct P2P when possible
  -> existing coturn/TURN only when ICE requires relay

Viewer
SIPSorcery RTCPeerConnection
  -> H.264 -> Media Foundation H.264 decoder -> VideoFrame -> existing UI
  -> Opus -> PCM -> WASAPI playback
```

The signaling protocol, session routing and RelayControl API are not redesigned by this work.

## Scope

In scope:

- `Windows.Graphics.Capture` remains the screen-capture backend.
- Microsoft Media Foundation replaces FFmpeg for H.264 encode and decode.
- Hardware Media Foundation transforms are preferred, with Microsoft software fallback.
- System audio is captured through WASAPI loopback.
- Opus audio is carried over the same SIPSorcery peer connection as H.264 video.
- Publisher audio and video tracks are send-only; viewer tracks are receive-only.
- Existing PLI/FIR keyframe behavior and global video quality ladder remain functional.
- Existing `/api/webrtc/ice-servers` mapping remains the source of STUN/TURN configuration.
- `ForceRelay=false` remains production default; `ForceRelay=true` remains available to tests/diagnostics.
- Diagnostics expose selected media backends and the selected ICE route without credentials.
- FFmpeg packages, runtime acquisition, probing, code and notices are removed only after native parity is covered.

Out of scope:

- Linux media backend.
- Replacing SIPSorcery or implementing a custom WebRTC/TURN stack.
- Simulcast, SVC or per-viewer video encodes.
- Microphone capture.
- A broad rename of legacy `SonicDesktopRelay.*` namespaces/projects/storage paths.
- Unrelated signaling or UI refactors.

## Design principles

### Preserve current boundaries

The repository already has useful seams:

- `IVideoEncoder` / `IVideoDecoder`
- `ScreenPublishPipeline` / `ScreenWatchPipeline`
- `IPeerConnection` / `IViewerPeerConnection`
- one capture + one encode per publishing session
- one decoder per watching session

Those boundaries remain. Native Windows APIs stay in `SonicDesktopRelay.Media.Windows`; SIPSorcery stays in `SonicDesktopRelay.Rtc`; UI/composition stays in `SonicDesktopRelay.App`.

No Media Foundation COM type, WASAPI device type or SIPSorcery peer type leaks into Presentation or the platform-neutral media contracts.

### Audio and video fail independently

Audio is added as a parallel pipeline, not folded into a giant `WindowsMediaSession` class.

A recoverable audio capture/playback failure degrades the session to video-only and is exposed in Diagnostics. A video encoder/decoder failure remains fatal to the screen-share media path because video is the primary product function. Neither path may hold a lock or wait on the other path in a way that can deadlock the session.

### One session timeline

Publisher audio and video timestamps are normalized through a single monotonic `MediaSessionClock` created when publishing starts. The viewer preserves incoming RTP timing per track and maps decoded samples to the same session-relative timeline.

The clock is not reset by renegotiation or temporary track recovery. Video frame drops do not stop audio time, and audio underruns do not stop video time.

## Project and dependency changes

`SonicDesktopRelay.Media` remains `net10.0` and gains only platform-neutral contracts and pipeline types.

`SonicDesktopRelay.Media.Windows` remains `net10.0-windows10.0.19041.0` and owns:

- Media Foundation startup/shutdown and MFT selection.
- Media Foundation H.264 encoder/decoder.
- Windows Graphics Capture adapter.
- WASAPI loopback capture and playback.
- Windows-only media diagnostics.

Pinned Windows media dependencies for this implementation:

- `Vortice.MediaFoundation` `3.8.3`;
- upgrade the existing `Vortice.Direct3D11` reference from `3.6.2` to `3.8.3` so the Vortice family is aligned;
- `NAudio.Wasapi` `3.1.0` for endpoint enumeration, loopback capture and playback;
- use the modern NAudio 3 APIs (`WasapiRecorder` / `WasapiRecorderBuilder` and `WasapiPlayer` / `WasapiPlayerBuilder`) rather than the obsolete `WasapiLoopbackCapture` / `WasapiOut` APIs;
- keep `SIPSorcery` at the repository's current `10.0.16` unless compilation proves a documented upstream API is unavailable.

For Opus, prefer SIPSorcery's existing managed audio codec support/Concentus path. Do not add a native Opus runtime dependency. If direct compile-time use of Concentus is required, add an explicit managed package reference rather than depending silently on a transitive package.

## Video capture and frame bridge

`GraphicsCaptureScreenSource` continues to own monitor selection, cursor capture, throttling, resolution-change handling and one capture per session.

The current public media contract delivers a CPU-backed BGRA `VideoFrame`. Issue #6 permits an isolated CPU-copy fallback when full zero-copy is not safe in the first native implementation, so the initial native encoder consumes that contract rather than spreading D3D11 handles through platform-neutral code.

The CPU/system-memory bridge is isolated behind the Windows encoder implementation. It must not become a requirement of `IVideoEncoder` or `ScreenPublishPipeline`.

A future D3D11 zero-copy optimization may replace the bridge internally by sharing the capture device through `IMFDXGIDeviceManager` and feeding NV12 GPU surfaces to a compatible MFT. That optimization must not require changing RTC, signaling, Presentation, or the public media contracts.

## Media Foundation lifecycle

Introduce a small Windows-only runtime owner, e.g. `MediaFoundationRuntime`, responsible for process-safe Media Foundation initialization and shutdown.

Requirements:

- initialization is idempotent;
- encoder and decoder instances can coexist;
- shutdown occurs only after the last native media user is disposed;
- Media Foundation startup failure is reported as a media start failure with an actionable message.

Do not let each frame or each peer connection call Media Foundation startup/shutdown.

## H.264 encoder

Introduce `MediaFoundationH264Encoder : IVideoEncoder`.

### MFT selection

Enumerate H.264 encoder MFTs and attempt candidates in this order:

1. hardware-backed transforms;
2. Microsoft/system software H.264 encoder.

A candidate is accepted only after its input/output types and required real-time settings are successfully configured. Diagnostics preserve a rejection list containing transform name/CLSID and a credential-free reason.

### Format

Input is the existing BGRA frame contract. The Windows implementation converts to an encoder-supported format, preferably NV12, inside the encoder boundary.

Output is normalized to the H.264 access-unit format expected by the current SIPSorcery RTP sender. The normalizer handles Annex-B/length-prefixed differences so `IPeerConnection` never depends on a specific MFT output packaging mode.

WebRTC negotiation remains H.264 with `packetization-mode=1`.

### Real-time configuration

Where supported:

- enable Media Foundation low-latency mode;
- enable codec low-latency mode through `ICodecAPI`;
- configure the existing quality ladder's bitrate, frame rate and resolution;
- avoid B-frame/reordering settings that add avoidable screen-share latency.

Unsupported optional settings are diagnostic information, not automatically fatal, provided the transform still produces compatible low-latency H.264.

### Keyframes

`RequestKeyFrame()` maps to the selected encoder's force-keyframe/IDR control. PLI/FIR behavior above the encoder does not change.

A resolution change or quality-ladder resize reconfigures or recreates the transform safely, flushes stale samples and forces the next decodable output to include the parameter sets/keyframe needed by all viewers.

### Runtime diagnostics

Expose at least:

- implementation name;
- selected MFT name and CLSID;
- hardware/software classification;
- configured input/output format;
- width/height/FPS/bitrate;
- rejected candidates and reasons.

## H.264 decoder

Introduce `MediaFoundationH264Decoder : IVideoDecoder`.

The decoder:

- enumerates hardware decoders first and software fallback second;
- accepts complete H.264 access units from `IViewerPeerConnection`;
- handles SPS/PPS changes and active-session resolution changes;
- applies low-latency configuration where supported;
- outputs the existing `VideoFrame` expected by `ScreenWatchPipeline` and the UI;
- isolates any NV12/BGRA conversion inside the Windows decoder boundary.

A malformed access unit is a dropped sample when recovery is possible. Repeated transform failure or unrecoverable device loss transitions the video pipeline to failed rather than producing an exception storm at frame rate.

## Audio contracts

Add platform-neutral media contracts with intentionally small surfaces:

```text
AudioFrame
  PCM payload
  sample rate
  channel count
  sample count / duration
  session-relative timestamp

EncodedAudioSample
  Opus payload
  sample count / duration
  session-relative timestamp

IAudioCaptureSource
  AudioCaptured event
  StartAsync / StopAsync / DisposeAsync

IAudioEncoder
  Encode(AudioFrame) -> EncodedAudioSample?

IAudioDecoder
  Decode(EncodedAudioSample) -> AudioFrame?

IAudioSink
  StartAsync / Write / StopAsync / DisposeAsync
```

Concrete names may vary, but Windows and SIPSorcery types must not appear in these interfaces.

## WASAPI loopback capture

Add a Windows implementation of `IAudioCaptureSource` using `NAudio.Wasapi 3.1.0` in shared-mode loopback against the current default render endpoint. Use the modern `WasapiRecorder` builder API rather than the obsolete NAudio 2-style capture classes.

Normalized capture format for the media pipeline is 48 kHz PCM with an Opus-compatible channel layout. Device-native formats are converted inside the Windows adapter when necessary.

Target framing is approximately 20 ms per encoded Opus packet. Capture callbacks may arrive at different buffer sizes, so a small accumulator assembles exact codec frames without blocking the callback thread.

Default output-device changes are handled through NAudio's notification APIs by disposing and reopening the loopback endpoint when practical. If recovery fails, audio enters a failed/muted state while video continues.

No Stereo Mix, virtual cable or microphone device is required.

## Opus codec path

Use a managed Opus implementation from the existing SIPSorcery/Concentus ecosystem.

Encoder and decoder are isolated behind `IAudioEncoder` / `IAudioDecoder`, even if the concrete implementation internally uses SIPSorcery's audio helper. RTC owns RTP transport; the media layer owns PCM/Opus conversion.

Preferred format:

- 48 kHz RTP/Opus clock;
- stereo when the system mix is stereo;
- 20 ms frames unless profiling demonstrates a concrete reason to change it.

No additional native codec DLL is introduced.

## Audio playback

Add a Windows `IAudioSink` using `NAudio.Wasapi 3.1.0` and the modern `WasapiPlayer` API against the normal default render endpoint.

The sink uses a bounded jitter/playback buffer. On underrun it outputs silence/recovers without blocking video. On sustained overflow it drops the oldest buffered audio rather than allowing unbounded latency growth.

Default playback-device changes are reopened when practical. Failure mutes audio and is surfaced in Diagnostics without tearing down the video decoder.

## Media session clock and A/V sync

Introduce a small platform-neutral `MediaSessionClock` based on a monotonic time source.

Publisher:

- the clock starts once when the publishing media session starts;
- video capture timestamps and audio capture timestamps are converted to this session-relative timeline;
- reconnect or SDP renegotiation does not recreate the clock;
- quality changes do not recreate the clock.

Viewer:

- RTP timestamps remain independent on the wire (90 kHz video, 48 kHz audio);
- received timestamps are converted to session-relative `TimeSpan` values before decode/playback scheduling;
- temporary loss on one track does not advance or stall the other through shared locks.

The design does not add a heavyweight lip-sync engine. Acceptance is no obvious long-running drift and no independent clock resets.

## RTC contract changes

Extend `IPeerConnection` with an audio send operation using `EncodedAudioSample`.

Extend `IViewerPeerConnection` with an `AudioSampleReceived` event using `EncodedAudioSample`.

`SipSorceryPeerConnection` adds:

- existing H.264 send-only video track;
- Opus send-only audio track.

`SipSorceryViewerPeerConnection` adds:

- existing H.264 receive-only video track;
- Opus receive-only audio track;
- incoming audio-frame handling that emits encoded Opus samples to the media pipeline.

The SDP produced by the publisher must contain both `m=video` and `m=audio`; the viewer answer must contain both. The existing H.264 `packetization-mode=1` requirement is preserved.

## Publisher composition

Keep video fan-out at one encode per session.

Add an audio publish pipeline with the same topology:

```text
WASAPI loopback -> PCM -> Opus -> one EncodedAudioSample event
                                      |-> viewer peer 1
                                      |-> viewer peer 2
                                      `-> viewer peer N
```

A new orchestration type may compose the existing video publisher with audio, but it must not duplicate signaling ownership or create separate peer connections per media type. Each viewer still owns exactly one `RTCPeerConnection` carrying both tracks.

`RtcVideoPublishHost` may be renamed only if necessary for correctness; broad namespace/project rebranding is out of scope. Prefer extending composition without unrelated rename churn.

## Viewer composition

The viewer still owns exactly one peer connection to the publisher.

Incoming H.264 continues through `ScreenWatchPipeline`. Incoming Opus goes through an independent audio decode/playback pipeline.

The host starts/stops both media paths under the same session lifetime but disposes them independently and in a deterministic order. An audio failure must not unsubscribe or dispose the video peer connection.

## ICE, STUN and TURN

Media backend selection is independent of ICE behavior.

Publisher and viewer continue loading credentials from:

```text
GET /api/webrtc/ice-servers
```

Normal configuration remains:

```text
ForceRelay = false
RTCIceTransportPolicy.all
```

Diagnostic/test configuration remains:

```text
ForceRelay = true
RTCIceTransportPolicy.relay
```

No TURN credential is written to logs or the Diagnostics UI.

Add a small RTC diagnostics projection that records connection state and, where SIPSorcery exposes enough information, the selected local and remote candidate types and whether the selected path is direct or relay. If the library does not expose the selected pair on the current version, diagnostics must state that the pair is unavailable rather than infer it from gathered candidates.

## Diagnostics

The existing Diagnostics screen is extended from current runtime state. It does not probe or initialize an extra encoder/decoder just to display information.

Video fields:

- capture backend (`Windows.Graphics.Capture`);
- encoder implementation and selected MFT;
- hardware/software encode;
- decoder implementation and selected MFT;
- negotiated video codec/profile when known;
- current resolution/FPS/bitrate;
- candidate rejection/failure reasons.

Audio fields:

- capture backend (`WASAPI loopback`);
- active render endpoint used for loopback;
- negotiated codec (`Opus`);
- sample rate/channels;
- playback backend and active endpoint;
- audio degraded/failure reason when applicable.

WebRTC fields:

- ICE/peer connection state;
- selected candidate types when available;
- direct vs TURN relay when known;
- safe TURN URL only when useful; never username/credential.

## Error handling

Rules:

- one failing viewer still cannot take down the shared publisher pipeline;
- media callbacks catch only expected native/device/socket failure classes and convert them into bounded failure states;
- frame-rate callbacks must not repeatedly throw/log the same terminal failure;
- MFT selection records each rejected candidate once;
- an audio device error degrades audio and leaves video alive;
- an unrecoverable video encoder/decoder error fails the screen media path cleanly;
- cancellation and disposal are idempotent;
- native resources are disposed outside locks when disposal can synchronously invoke callbacks.

## Migration and FFmpeg removal sequence

FFmpeg is not removed first.

Implementation sequence:

1. add/adjust red tests for the desired RTC/audio/native-media behavior;
2. add platform-neutral audio contracts and pipelines;
3. add Media Foundation runtime, encoder and decoder while FFmpeg remains available in the branch for parity comparison;
4. add WASAPI capture/playback and managed Opus codec path;
5. add H.264 + Opus tracks to the existing SIPSorcery peer connection;
6. switch App composition to the native Windows media path;
7. extend Diagnostics and ICE-route reporting;
8. run automated parity tests;
9. remove FFmpeg encoder/decoder/loader, package references, acquisition/build targets, environment probing and obsolete notices;
10. run the entire solution test/build suite again after deletion.

The final PR must not keep an unused production FFmpeg fallback merely as insurance. If the native path cannot satisfy the automated acceptance criteria, the FFmpeg removal step does not happen and the PR remains incomplete/draft.

## Tests

### Platform-neutral media tests

Add tests for:

- one video encode still fans out to N viewers;
- one audio encode fans out to N viewers;
- audio frame accumulation produces correct ~20 ms boundaries;
- audio timestamps are monotonic;
- audio and video use the same session clock lifetime;
- audio failure does not deadlock/stop video;
- video frame loss does not block audio;
- reconnect/renegotiation does not reset the media clock.

### RTC tests

Update tests that currently assert no audio.

Require:

- publisher offer contains H.264 video + Opus audio;
- viewer answer contains H.264 video + Opus audio;
- publisher tracks are send-only;
- viewer tracks are receive-only;
- H.264 retains `packetization-mode=1`;
- existing ICE mapping remains intact;
- `ForceRelay=false` maps to `RTCIceTransportPolicy.all`;
- `ForceRelay=true` maps to `RTCIceTransportPolicy.relay`;
- encoded audio is routed to all connected viewers and received by the viewer abstraction.

### Windows media tests

Where tests can run without physical hardware, cover:

- Media Foundation startup/lifecycle;
- MFT enumeration/selection ordering;
- software fallback when a hardware transform is unavailable/rejected;
- H.264 encode/decode round-trip for representative generated frames;
- forced keyframe propagation;
- bitrate/quality reconfiguration;
- decoder SPS/PPS/resolution changes;
- Opus encode/decode round-trip for generated PCM;
- bounded audio buffering.

Hardware-specific behavior must be structured behind small selectors/adapters so candidate-ordering and diagnostics can be tested with fakes even when GitHub-hosted runners expose no usable GPU/audio endpoint.

### Repository regression tests

After FFmpeg removal, add assertions or CI checks that fail if obsolete production references return, including:

- `FFmpeg.AutoGen` package reference;
- `FFmpegH264Encoder` / `FFmpegH264Decoder` / `FFmpegLoader` production classes;
- `build/FFmpeg.props`;
- `build/FFmpeg.targets`;
- `build/FFmpegAcquisition.targets`;
- FFmpeg runtime acquisition/copy steps in CI/release;
- obsolete FFmpeg-only environment variables and diagnostics text.

## Manual validation before merge

The PR description contains an explicit manual matrix. Items that cannot be proven in GitHub-hosted CI remain unchecked rather than being claimed as passed.

Validate at least:

- 1080p30 screen share with system audio;
- NVIDIA hardware encoder machine;
- Intel iGPU/QSV-capable machine when available;
- forced software encoder fallback;
- same-LAN direct P2P;
- internet direct P2P through STUN where NAT permits;
- forced TURN/coturn path;
- long-running session with no obvious A/V drift;
- resolution change during an active session;
- output-device change/recovery where practical.

## Acceptance criteria

The implementation is complete when:

- screen video publishes and watches with no FFmpeg runtime libraries;
- H.264 encode/decode uses Media Foundation;
- compatible hardware MFTs are preferred and a software fallback exists;
- system audio is captured through WASAPI loopback and heard through normal Windows playback;
- Opus audio and H.264 video share the same WebRTC peer connection;
- direct ICE remains preferred and coturn remains fallback;
- `ForceRelay=false` remains the normal default;
- PLI/FIR-driven keyframes and packet-loss quality reduction still work;
- multiple viewers still share a single video encode and a single audio encode;
- audio/video clocks do not obviously drift or reset independently;
- Diagnostics reports active media backends and route information without credentials;
- FFmpeg production/build/runtime dependency is removed after parity;
- automated tests and build pass;
- hardware/network-only validation gaps are called out explicitly in the PR instead of being fabricated.
