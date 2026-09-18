# Screen publishing and watching

The Windows client uses the operating system media stack end to end. Capture, encode and audio
capture happen once per publishing session; encoded samples are then fanned out to each viewer.

## Publishing pipeline

```text
Windows.Graphics.Capture
        |
        v
      BGRA
        |
        v
   BGRA -> NV12
        |
        v
Media Foundation H.264 encoder ----+
                                   |
WASAPI loopback -> PCM -> Opus ----+--> SIPSorcery WebRTC --> viewers
```

`GraphicsCaptureScreenSource` is the only screen source. `ScreenPublishPipeline` owns one
`IVideoEncoder` for the entire session, so adding viewers adds peer subscriptions rather than
additional encoders.

### Encoder selection

`MediaFoundationH264Encoder` enumerates H.264 Media Foundation transforms in this order:

1. hardware transforms;
2. system software transforms.

A candidate is accepted only after it can be configured for the requested NV12 -> H.264
low-latency contract. Asynchronous hardware transforms are driven through
`MediaFoundationAsyncMftPump`. Every rejected candidate is retained with its reason for
Diagnostics.

The encoder output is normalized to Annex B access units. Recovery-only keyframe requests use
Media Foundation `ICodecAPI` with `CODECAPI_AVEncVideoForceKeyFrame` when the selected transform
supports it, so the next input becomes a recovery point without tearing the transform down.
Transforms that do not support the codec-control property use the older reconfigure behavior as
an explicit, diagnosed correctness fallback. Real geometry/FPS/bitrate changes still reconfigure
the native transform and request a clean random-access frame.

## System audio

`WasapiLoopbackAudioSource` captures the active render endpoint. The publishing path normalizes
audio to the RTC contract and `OpusAudioCodec` sends 48 kHz stereo Opus on the same peer
connection as video.

Video is required for a screen-sharing session. System audio is degradable: if the endpoint
cannot be opened or disappears, video continues and Diagnostics records the audio reason.

## One clock for audio and video

A publishing host creates one `MediaSessionClock` and passes it to both the video and audio
pipelines. Timestamps are assigned at pipeline ingress so both streams share the same monotonic
origin instead of inheriting unrelated device clocks.

## Publisher quality and FPS ceilings

Before starting a share, the publisher chooses a quality ceiling (1080p, 720p, 540p or 360p) and
an FPS ceiling (15, 30 or 60 FPS). Defaults are 1080p / 30 FPS. The selections are session-start
controls for this PR; manual mid-session profile switching is not exposed.

The session still uses one global adaptive quality target because capture and encoding are shared.
The selected profile limits how high the adaptive controller may start or recover. Sustained
packet loss can reduce bitrate/resolution/FPS for every viewer, but recovery never exceeds the
user-selected ceiling.

The bitrate-first ladder is:

| Rung | Height | Base FPS | Target bitrate |
|---|---:|---:|---:|
| 0 | 1080 | 30 | 4 Mbit/s |
| 1 | 1080 | 30 | 3 Mbit/s |
| 2 | 1080 | 30 | 2 Mbit/s |
| 3 | 720 | 30 | 2 Mbit/s |
| 4 | 720 | 30 | 1.5 Mbit/s |
| 5 | 540 | 20 | 1 Mbit/s |
| 6 | 360 | 15 | 600 kbit/s |

For 30-FPS ladder rungs, a selected 60-FPS ceiling can run that resolution/bitrate rung at 60 FPS;
lower-FPS rungs remain capped at their base FPS. A 15-FPS ceiling caps every rung at 15 FPS.

Scaling preserves aspect ratio, never upscales, and keeps dimensions compatible with 4:2:0
chroma. When effective FPS changes, the WGC throttle is updated without restarting capture.
Encoded samples carry their actual duration, and the RTC sender converts that duration to the
90 kHz video RTP clock instead of assuming 30 FPS. A quality change also requests a clean
keyframe so viewers can resynchronize immediately.

## Watching pipeline

```text
SIPSorcery WebRTC -> H.264 access unit -> Media Foundation decoder
                                           |
                                           v
                                          NV12
                                           |
                                           v
                                          BGRA -> VideoSurface

SIPSorcery WebRTC -> Opus -> PCM -> WASAPI render endpoint
```

`MediaFoundationH264Decoder` enumerates native H.264 decoders hardware-first and then software.
Candidates that cannot satisfy the supported transform contract are rejected and recorded.
Output is selected as NV12 and converted into a reusable BGRA buffer.

The decoder:

- accepts the publisher's Annex B access units;
- returns `null` for corrupt/lost input rather than terminating the session;
- handles a mid-stream resolution change by rebuilding the transform state;
- reuses the BGRA conversion buffer for frames with unchanged geometry;
- reports the live transform name, CLSID, acceleration type, formats and candidate rejections.

The UI hand-off is synchronous because the decoder owns and reuses the BGRA buffer. Posting a
reference asynchronously would allow the decode thread to overwrite pixels before Avalonia has
blitted them.

## WebRTC and signaling

Each viewer gets one SIPSorcery peer connection. The publisher sends one H.264 track and one
Opus track. ICE configuration comes from `GET /api/webrtc/ice-servers` and the client keeps
`ForceRelay=false`, so direct/STUN connectivity is attempted first and TURN is a fallback.
After SIPSorcery nominates the winning ICE pair, FrameRelay records only its safe classification:
Direct or TURN, UDP or TCP, and local/remote candidate types. It does not copy endpoint addresses,
ports, candidate strings, ICE username fragments or credentials into application diagnostics.

Signaling uses the existing session WebSocket:

1. publisher announces readiness;
2. publisher sends an offer;
3. viewer answers;
4. both sides exchange ICE candidates;
5. RTP/RTCP carries H.264 and Opus.

A publisher encodes once regardless of viewer count. A viewer owns exactly one decoder and one
audio sink.

## Diagnostics

The Diagnostics page reports runtime state rather than probing a second codec instance:

- capture backend;
- selected Media Foundation encoder/decoder transform;
- transform CLSID and hardware/software path;
- input/output pixel formats;
- active/effective video geometry, frame rate and bitrate when available;
- encoder keyframe mode (`codec-api` or `reconfigure-fallback`) and latest recovery latency;
- sampled encode and WebRTC video fan-out duration;
- selected Direct/TURN and UDP/TCP transport classification;
- rejected transform candidates and reasons;
- WASAPI capture/render endpoint state;
- Opus encoder/decoder state;
- session and bounded signaling metadata.

Diagnostics never record SDP bodies, ICE candidate contents, candidate addresses/ports,
credentials or media payloads.

The following performance work is deliberately deferred to separate changes: a bounded
latest-frame-wins capture -> encode channel (which first needs explicit frame-buffer ownership),
full queue/capture/convert stage timing, TURN transport preference tuning, hardware-decoder
selection, GPU-native texture -> NV12 conversion, and broader RTP pacing/congestion tuning.

## Failure model

- Failure to start capture or H.264 video is terminal for sharing/watching and is surfaced as
  `media_unavailable`.
- Audio startup/runtime failure degrades audio only.
- Decoder corruption/loss drops the affected frame and waits for recovery rather than tearing
  down the session.
- A viewer stall is media state, not signaling state, and can request one recovery keyframe.

## Packaging

The application relies on Windows Media Foundation and WASAPI already present in the supported
operating system. Release packaging therefore contains no separately acquired video-codec DLL
set. CI checks both the portable folder and single-file executable for legacy codec artifacts.

See [native-media-validation.md](native-media-validation.md) for the automated and manual
validation matrix.
