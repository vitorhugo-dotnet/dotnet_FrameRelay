# Stream Performance and Publisher Quality Controls Design

## Status

Approved for implementation on `feat/stream-performance-quality-controls`.

This design intentionally separates the work that belongs in this PR from follow-up performance work. The current production symptom is no longer H.264 visual corruption; the remaining problem is repeated visible stalling/recovery while the WebRTC session itself stays connected, plus the lack of explicit publisher quality/FPS controls.

## Context

FrameRelay currently uses:

- Windows.Graphics.Capture for desktop capture;
- Media Foundation H.264 encoding/decoding;
- SIPSorcery 10.0.16 for WebRTC;
- P2P-first ICE with backend-provided STUN/TURN servers and `ForceRelay=false`;
- one shared publisher encode for all viewers;
- the bitrate-first adaptive quality ladder introduced by the H.264 loss-recovery work.

The viewer now rejects incomplete RTP/H.264 access units and waits for a clean IDR instead of feeding corrupt data to Media Foundation. That prevents the previously visible corruption, but it also makes recovery latency directly dependent on how quickly the publisher can produce a keyframe.

The current Media Foundation encoder implements `RequestKeyFrame()` by setting a flag that causes the next `Encode()` call to fully release, re-enumerate, recreate and reconfigure the selected H.264 MFT. That is deterministic but expensive. During repeated packet-loss recovery it can turn feedback that should be cheap into visible stalls.

Microsoft documents `ICodecAPI` on the H.264 encoder and `CODECAPI_AVEncVideoForceKeyFrame`, which requests that the next frame be encoded as a keyframe without rebuilding the encoder. FrameRelay's pinned Vortice.MediaFoundation 3.8.3 surface does not currently expose the required codec API directly, so this design allows a narrowly scoped Windows COM interop seam contained entirely inside `SonicDesktopRelay.Media.Windows`.

The current RTC sender also hardcodes the H.264 RTP timestamp increment to `90000 / 30`, which makes any future FPS selector incorrect unless the sender uses the configured/effective video cadence.

## Goals

1. Make H.264 recovery keyframe requests cheap on the normal Media Foundation path.
2. Expose the selected ICE transport class so Diagnostics can distinguish direct media from TURN-relayed media without logging private ICE data.
3. Add explicit publisher quality and FPS selectors that are real end-to-end controls rather than UI-only values.
4. Keep adaptive congestion behavior below the user-selected ceiling.
5. Add enough timing/transport observability to verify this PR without expanding it into a full media-pipeline rewrite.

## Scope Matrix

| Priority | Item | This PR | Decision |
| --- | --- | --- | --- |
| P0 | Replace encoder recreation for recovery keyframes with `ICodecAPI / CODECAPI_AVEncVideoForceKeyFrame` | **Yes** | Primary recovery fix. Use codec control when supported; retain an explicit, diagnosed recreate fallback only for unsupported MFTs. |
| P0 | Record selected ICE candidate pair / transport class | **Yes** | Expose metadata-only `Direct/UDP`, `Direct/TCP`, `TURN/UDP`, `TURN/TCP` or equivalent candidate classification. Never log candidate addresses, SDP or credentials. |
| P0 | Separate capture -> encode using bounded `Channel`, latest-frame-wins | **No** | Deferred. The current WGC source intentionally reuses one BGRA buffer; asynchronously queueing that `VideoFrame` without changing ownership can race with the next capture and corrupt pixels. This needs its own buffer-ownership design first. |
| P1 | Measure `captureMs`, `convertMs`, `encodeMs`, `sendMs`, queue depth | **Partial** | Add recovery/keyframe timing and encoder/send timing that can be measured safely at current boundaries. Queue depth and full capture-stage timing belong with the future bounded-channel pipeline. |
| P1 | Prefer TURN/UDP, TCP/TLS only as fallback | **No** | Deferred until selected-pair diagnostics prove which transport is actually winning in production. Do not tune an unmeasured path. |
| P1 | Investigate hardware decoder on viewer | **No** | Deferred to a separate investigation/PR. The current symptom must first be measured independently from decoder acceleration choice. |
| P2 | GPU texture -> NV12 without CPU roundtrip | **No** | Deferred. Large optimization with different ownership and native-surface boundaries. |
| P2 | Pacing/congestion tuning | **Partial** | Correct the existing hardcoded 30 FPS RTP timestamp increment so the requested FPS is represented correctly. Broader pacing/congestion-controller changes are deferred. |
| Feature | Publisher quality selector | **Yes** | User chooses the maximum output resolution profile before sharing. |
| Feature | Publisher FPS selector | **Yes** | User chooses 15/30/60 FPS before sharing; the selection flows through capture, encoder and RTP timing. |

## Non-goals

This PR does not:

- replace SIPSorcery;
- replace WebRTC;
- force every session through Coturn;
- implement simulcast or SVC;
- create per-viewer encoders;
- reintroduce FFmpeg;
- redesign the signaling protocol;
- add a general-purpose media queue;
- implement GPU zero-copy capture/encode;
- change decoder selection policy;
- expose raw ICE candidates, private/public IP addresses, TURN usernames/passwords, SDP bodies or media payloads.

## Design

### 1. Media Foundation keyframe control

Add a small Windows-only codec-control abstraction next to `MediaFoundationH264Encoder`.

The encoder will distinguish two reasons for reconfiguration:

- **quality/geometry configuration changes**: width, height, FPS or bitrate changed, so the MFT may need reconfiguration;
- **recovery keyframe request**: same media configuration, only the next encoded picture needs to be random-access.

`RequestKeyFrame()` must no longer itself imply `SelectAndConfigure(...)`.

Normal path:

```text
PLI/FIR/recovery request
-> mark one keyframe pending
-> before next ProcessInput, invoke CODECAPI_AVEncVideoForceKeyFrame
-> ProcessInput next frame
-> encoder emits keyframe
-> no MFT teardown/re-enumeration
```

The codec-control seam should query `ICodecAPI` from the selected MFT's COM object and set `CODECAPI_AVEncVideoForceKeyFrame` for the next input. COM details remain internal to `Media.Windows`.

If the selected vendor MFT does not expose the interface/property, FrameRelay keeps correctness by using the existing recreate behavior as a **fallback only**. Diagnostics must report the active mode:

- `codec-api`;
- `reconfigure-fallback`.

The fallback is not considered equivalent performance; it exists so an unusual encoder does not lose the ability to recover at all.

Repeated requests before the next encoded frame remain coalesced by the existing recovery policy. The encoder must not repeatedly issue force-keyframe control for a single pending recovery frame.

### 2. Selected ICE transport diagnostics

SIPSorcery 10.0.16 exposes the nominated ICE checklist entry through the RTP ICE channel. The connection adapters can inspect candidate metadata after ICE becomes connected.

Introduce a transport diagnostic DTO owned by the RTC layer, containing only safe classification metadata, for example:

```csharp
public sealed record RtcTransportDiagnostics(
    string Path,
    string Protocol,
    string LocalCandidateType,
    string RemoteCandidateType);
```

Allowed values should be normalized rather than copying candidate strings:

- path: `Direct` or `TURN`;
- protocol: `UDP` or `TCP`;
- candidate types: `host`, `srflx`, `prflx`, `relay`.

A path is classified as `TURN` when the nominated pair includes a relay candidate. Otherwise it is `Direct`.

Do not expose candidate `address`, `port`, `relatedAddress`, ICE username fragments, server URI credentials or SDP.

Both publisher and viewer diagnostics should expose the chosen path once connected. The viewer's existing WebRTC diagnostics event can carry a metadata-only transport event, while the publisher host should expose current transport summaries per viewer or a safe aggregate suitable for the existing Diagnostics page.

This PR does not claim to distinguish TURN-over-TLS from TURN-over-TCP unless the exact nominated candidate/server metadata exposed by the pinned SIPSorcery version makes that distinction reliable. The UI must not invent precision the runtime cannot prove.

### 3. Publisher quality profile

Add a user-selected publisher profile that defines the **ceiling** for the adaptive quality controller, not a permanently locked rung.

The Share screen exposes a quality selector:

- 1080p;
- 720p;
- 540p;
- 360p.

The default remains 1080p.

The selected maximum height is applied when the share starts. Adaptive quality may move downward under sustained loss and later recover upward, but must never exceed the selected ceiling.

For example, selecting 720p means the adaptive policy starts at the highest 720p rung and can reduce bitrate/resolution below that if necessary; it must not recover to 1080p until a future session starts with a 1080p ceiling.

The existing bitrate ladder remains the source of concrete bitrate values. Do not introduce a second independent bitrate table in the UI.

### 4. Publisher FPS profile

The Share screen exposes:

- 15 FPS;
- 30 FPS;
- 60 FPS.

The default remains 30 FPS.

The selected FPS must affect:

1. capture throttling;
2. encoder media type/sample duration;
3. effective quality representation;
4. RTP video timestamp progression.

No UI-only setting is acceptable.

The adaptive ladder may reduce FPS below the selected maximum if an existing lower rung requires it. It must not exceed the selected maximum.

The current `SipSorceryPeerConnection.SendVideo` hardcoded `VideoClockRate / 30` must be replaced by a duration derived from the encoded sample or effective configured cadence. Prefer carrying explicit RTP/sample duration at the media contract boundary rather than making RTC infer timing from wall-clock deltas.

For 90 kHz video:

- 15 FPS => 6000 ticks/frame;
- 30 FPS => 3000 ticks/frame;
- 60 FPS => 1500 ticks/frame.

### 5. Capture FPS updates

`GraphicsCaptureScreenSource` currently computes `_minimumInterval` once in `StartAsync`. A configured 15/30/60 FPS start value can use that path immediately, but adaptive FPS changes during a session also need a supported update path.

Add a small capture-rate update contract rather than restarting WGC. The capture source should update its minimum delivery interval atomically when effective quality FPS changes.

Do not introduce the bounded asynchronous channel in this PR.

### 6. Performance diagnostics included here

This PR adds bounded measurements at existing safe boundaries:

- requested-keyframe timestamp;
- keyframe produced timestamp / recovery latency;
- Media Foundation encode duration;
- RTC send duration for video fan-out;
- current effective resolution/FPS/bitrate;
- selected ICE path/protocol classification;
- codec keyframe mode (`codec-api` or fallback).

These should use existing logging/Diagnostics patterns and avoid high-volume Information logs. Per-frame timing belongs at Trace or is aggregated/sampled.

Full `captureMs -> queue -> convertMs -> encodeMs -> sendMs` stage telemetry is deliberately deferred because the current capture and conversion boundaries do not yet have independent ownership/queue stages. Fabricating those numbers in this PR would create fake observability.

## Data Flow

```text
Share UI
  quality ceiling + FPS ceiling
        |
        v
SessionRuntime / IVideoPublishHost
        |
        v
ScreenPublishPipeline
  effective VideoQuality
  adaptive quality <= user ceiling
        |
        +--> GraphicsCaptureScreenSource.UpdateFrameRate(...)
        |
        v
MediaFoundationH264Encoder
  configured width / height / fps / bitrate
  RequestKeyFrame -> ICodecAPI fast path
        |
        v
EncodedVideoSample
  encoded H.264 + explicit media duration/cadence
        |
        v
VideoPublisher
        |
        v
SipSorceryPeerConnection
  RTP timestamp increment from sample duration
  selected ICE transport diagnostics
```

## UI

On the Share page, before `Start sharing`:

```text
Monitor
[ DISPLAY1 ... ]

Quality
[ 1080p v ]

Frame rate
[ 30 FPS v ]

[ Start sharing ]
```

Controls are enabled only when sharing can start. Once a session is active, they are disabled for this PR. Mid-session manual profile switching is intentionally out of scope; adaptive runtime changes still work.

## Testing Strategy

Use TDD for production changes.

Required regression coverage:

1. A recovery keyframe request with unchanged quality does not select/reconfigure the MFT when codec control succeeds.
2. The next encoded sample after a codec-control request is treated as the requested recovery point and the request is consumed once.
3. Unsupported codec control takes the diagnosed fallback path.
4. Quality ceiling 720p cannot adapt/recover above 720p.
5. FPS ceiling 15 cannot produce a higher configured FPS.
6. The Share selection reaches `IVideoPublishHost.StartAsync`.
7. RTP timestamp increments are 6000/3000/1500 for 15/30/60 FPS respectively, or equivalent duration-derived assertions.
8. Capture throttle updates when effective FPS changes without restarting capture.
9. Selected ICE pair classification returns Direct vs TURN and UDP vs TCP from synthetic candidate pairs without exposing address/port data.
10. Diagnostics output contains the safe transport classification and keyframe mode but no SDP/candidate/credential payloads.

## Manual Validation

On two Windows machines:

1. Start at 1080p / 30 FPS.
2. Confirm Diagnostics shows effective resolution/FPS/bitrate.
3. Confirm Diagnostics reports Direct or TURN and UDP/TCP classification.
4. Generate heavy-motion desktop content.
5. Trigger/observe normal recovery PLIs.
6. Confirm recovery keyframes no longer correlate with MFT teardown/reselection on a codec-api-capable encoder.
7. Confirm recovery latency remains bounded and materially lower than the previous recreate path.
8. Repeat at 720p / 15 FPS and verify actual encoded geometry/cadence.
9. Repeat at 1080p / 60 FPS on capable hardware and verify RTP timing remains correct.
10. Verify no raw ICE candidate, address, credential or SDP is written to logs.

## Deferred Follow-up Work

The following work is explicitly preserved for later specs/PRs rather than silently disappearing:

### Bounded capture -> encode channel

Design safe frame ownership first, then use a bounded latest-frame-wins queue so WGC's `CreateFreeThreaded` worker thread never waits on encode/network work. The current reused BGRA buffer makes a naive `Channel<VideoFrame>` unsafe.

### Full stage timing

After capture and encode are separated, instrument independent `captureMs`, queue wait/depth, conversion, encode and send timing.

### TURN preference tuning

Use the selected-pair evidence gathered by this PR to decide whether TURN/UDP preference or TCP/TLS ordering needs adjustment.

### Hardware decoder investigation

Benchmark/enumerate hardware H.264 decoder candidates separately from the publisher recovery work.

### GPU-native capture -> NV12

Investigate D3D11 surface conversion/Media Foundation input without the current GPU -> CPU BGRA -> CPU NV12 path.

### Broader pacing/congestion work

Only after the transport path, encode timing and effective FPS are measurable should FrameRelay change broader pacing or congestion behavior.
