# Native Media Integration and FFmpeg Cleanup Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Switch the FrameRelay Windows app to the Media Foundation + WASAPI + Opus stack, expose native-media/WebRTC diagnostics, and remove the obsolete FFmpeg runtime/build dependency after parity is proven.

**Architecture:** `RtcVideoPublishHost` and `RtcVideoWatchHost` remain the composition boundaries to avoid rename churn. Each host owns one WebRTC peer topology carrying both media tracks while video/audio pipelines remain independently disposable; `AppComposition` still remains the only place that knows concrete implementations. Diagnostics are projections of the live media/RTC objects, never probes that instantiate a second codec/device stack.

**Tech Stack:** .NET 10, Avalonia, Media Foundation via Vortice 3.8.3, NAudio.Wasapi 3.1.0, SIPSorcery 10.0.16, xUnit, GitHub Actions

**Spec:** `docs/superpowers/specs/2026-09-17-native-media-pipeline-design.md`

## Global Constraints

- Windows media backend only; Linux remains out of scope.
- Preserve existing signaling message types and participant routing.
- Preserve `/api/webrtc/ice-servers`, `ForceRelay=false` production default, and coturn only as ICE fallback.
- Each viewer still has one `RTCPeerConnection` carrying H.264 + Opus.
- Audio failure degrades to video-only; unrecoverable video failure fails the screen media path.
- Keep legacy `SonicDesktopRelay.*` project/namespace/storage names in this PR.
- Remove FFmpeg only after native encoder/decoder and A/V tests are green.
- Never display or log TURN credentials.

---

### Task 1: Share one media-session clock across publishing audio and video

**Files:**
- Modify: `src/SonicDesktopRelay.Media/ScreenPublishPipeline.cs`
- Modify: `src/SonicDesktopRelay.Media/AudioPublishPipeline.cs`
- Modify: `tests/SonicDesktopRelay.Media.Tests/ScreenPublishPipelineTests.cs`
- Modify: `tests/SonicDesktopRelay.Media.Tests/AudioPublishPipelineTests.cs`

**Interfaces:**
- Both pipelines consume the same `MediaSessionClock` created by the publishing host.
- Samples emitted by both pipelines use `clock.Now` at capture-to-pipeline ingress.

- [ ] **Step 1: Write deterministic cross-pipeline timestamp tests**

Create a manual `TimeProvider`, one shared `MediaSessionClock`, fake video/audio capture sources and encoders. Start both pipelines, advance the provider 100 ms, push video and audio, and assert both emitted timestamps are the same session-relative value. Advance 20 ms and assert both move forward without either pipeline resetting the origin.

- [ ] **Step 2: Run the two focused tests and verify RED**

Run: `dotnet test tests/SonicDesktopRelay.Media.Tests/SonicDesktopRelay.Media.Tests.csproj --filter "FullyQualifiedName~ScreenPublishPipelineTests|FullyQualifiedName~AudioPublishPipelineTests"`

Expected: current video pipeline does not consume the shared media-session clock.

- [ ] **Step 3: Inject the shared clock into both publisher pipelines**

`ScreenPublishPipeline` receives `MediaSessionClock? clock = null` for source compatibility with existing tests/callers. When provided, create the `VideoFrame` sent to the encoder with the same pixels/dimensions but `Timestamp = clock.Now`.

`AudioPublishPipeline` stamps the encoded sample with `clock.Now` when the PCM frame enters the pipeline; it must not maintain a separate source-origin offset.

- [ ] **Step 4: Run focused tests and the full Media suite**

Run: `dotnet test tests/SonicDesktopRelay.Media.Tests/SonicDesktopRelay.Media.Tests.csproj -c Release`

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/SonicDesktopRelay.Media/ScreenPublishPipeline.cs src/SonicDesktopRelay.Media/AudioPublishPipeline.cs tests/SonicDesktopRelay.Media.Tests/ScreenPublishPipelineTests.cs tests/SonicDesktopRelay.Media.Tests/AudioPublishPipelineTests.cs
git commit -m "feat: share media session clock across audio and video"
```

---

### Task 2: Compose native publishing stack in the existing host

**Files:**
- Modify: `src/SonicDesktopRelay.App/RtcVideoPublishHost.cs`
- Modify: `src/SonicDesktopRelay.App/AppComposition.cs` only if constructor dependencies are extracted for testing.
- Create: `tests/SonicDesktopRelay.App.Tests/SonicDesktopRelay.App.Tests.csproj`
- Create: `tests/SonicDesktopRelay.App.Tests/RtcVideoPublishHostTests.cs`
- Modify: `SonicDesktopRelay.sln`

**Interfaces:**
- `RtcVideoPublishHost` composes one `MediaSessionClock`, `GraphicsCaptureScreenSource`, `MediaFoundationH264Encoder`, `ScreenPublishPipeline`, `WasapiLoopbackAudioSource`, `OpusAudioCodec`, `AudioPublishPipeline`, and one `VideoPublisher`/peer factory.
- The host exposes video/audio diagnostics and one optional `AudioFailure` string.

- [ ] **Step 1: Add an App test project and a seam for concrete media factories**

The test project references `SonicDesktopRelay.App`, `Media`, `Rtc`, and existing xUnit packages. Introduce small internal factory delegates/interfaces only where native/device construction otherwise prevents testing. Do not make Windows native objects public API.

- [ ] **Step 2: Write a failing host lifecycle test**

Use fake capture/encoder/audio source/codec and fake peer factory. Assert one shared session clock is passed to both publisher pipelines, video starts, audio starts, and stopping disposes audio/video resources exactly once.

- [ ] **Step 3: Write a failing degradation test**

Make audio capture startup throw an expected device exception while video startup succeeds. Assert `StartAsync` succeeds for the screen-share media path, `AudioFailure` is populated, and video remains active.

- [ ] **Step 4: Run focused App tests and verify RED**

- [ ] **Step 5: Switch production composition to Media Foundation + WASAPI + Opus**

Replace `new FFmpegH264Encoder()` with `new MediaFoundationH264Encoder()`. Build the audio source/codec/pipeline from the same session lifetime and pass the audio pipeline to the existing publisher fan-out. Keep ICE loading/signaling behavior unchanged.

Start video first; audio start failure is caught, recorded once and leaves the video publisher alive. A video startup failure still tears down all partially-created resources and rethrows.

- [ ] **Step 6: Run focused App tests and verify GREEN**

- [ ] **Step 7: Commit**

```bash
git add src/SonicDesktopRelay.App/RtcVideoPublishHost.cs src/SonicDesktopRelay.App/AppComposition.cs tests/SonicDesktopRelay.App.Tests SonicDesktopRelay.sln
git commit -m "feat: compose native publisher media stack"
```

---

### Task 3: Compose native viewer stack in the existing host

**Files:**
- Modify: `src/SonicDesktopRelay.App/RtcVideoWatchHost.cs`
- Create: `tests/SonicDesktopRelay.App.Tests/RtcVideoWatchHostTests.cs`

**Interfaces:**
- `RtcVideoWatchHost` composes `MediaFoundationH264Decoder`, `ScreenWatchPipeline`, `OpusAudioCodec`, `WasapiAudioSink`, `AudioWatchPipeline`, and one `VideoSubscriber`/viewer peer.
- Exposes `AudioFailure` plus video/audio diagnostics.

- [ ] **Step 1: Write a failing viewer lifecycle test**

Assert video decoder and audio playback pipeline share one host lifetime but dispose independently and exactly once.

- [ ] **Step 2: Write a failing audio-degradation test**

Make WASAPI playback startup fail; assert watching continues, video samples still decode, and `AudioFailure` is exposed.

- [ ] **Step 3: Run focused tests and verify RED**

- [ ] **Step 4: Replace FFmpeg decoder composition with Media Foundation and add audio playback**

Do not make the audio pipeline own or close the peer connection. Keep the existing video watchdog; audio underrun/failure does not transition `ScreenWatchPipeline` to stalled/failed.

- [ ] **Step 5: Run focused App tests and verify GREEN**

- [ ] **Step 6: Commit**

```bash
git add src/SonicDesktopRelay.App/RtcVideoWatchHost.cs tests/SonicDesktopRelay.App.Tests/RtcVideoWatchHostTests.cs
git commit -m "feat: compose native viewer media stack"
```

---

### Task 4: Expose RTC connection and selected-route diagnostics safely

**Files:**
- Create: `src/SonicDesktopRelay.Rtc/RtcConnectionDiagnostics.cs`
- Modify: `src/SonicDesktopRelay.Rtc/IPeerConnection.cs`
- Modify: `src/SonicDesktopRelay.Rtc/IViewerPeerConnection.cs`
- Modify: `src/SonicDesktopRelay.Rtc/SipSorceryPeerConnection.cs`
- Modify: `src/SonicDesktopRelay.Rtc/SipSorceryViewerPeerConnection.cs`
- Modify: `tests/SonicDesktopRelay.Rtc.Tests/SipSorceryPeerConnectionTests.cs`
- Modify: `tests/SonicDesktopRelay.Rtc.Tests/SipSorceryViewerPeerConnectionTests.cs`

**Interfaces:**
- `RtcConnectionDiagnostics` contains peer/ICE state, selected local candidate type, selected remote candidate type, and route classification (`Direct`, `Relay`, `Unknown`).
- If SIPSorcery 10.0.16 does not expose its nominated candidate pair through a supported public API, selected candidate fields remain `null` and route is `Unknown`; never infer the winning path from gathered candidates.

- [ ] **Step 1: Write tests for route classification as a pure function**

```csharp
[Theory]
[InlineData("host", "host", RtcRouteKind.Direct)]
[InlineData("srflx", "prflx", RtcRouteKind.Direct)]
[InlineData("relay", "host", RtcRouteKind.Relay)]
[InlineData(null, null, RtcRouteKind.Unknown)]
public void Candidate_types_classify_route(string? local, string? remote, RtcRouteKind expected)
{
    Assert.Equal(expected, RtcConnectionDiagnostics.Classify(local, remote));
}
```

- [ ] **Step 2: Run RED**

- [ ] **Step 3: Add the immutable diagnostics model and expose it on peer abstractions**

Peer wrappers update connection/ICE state from SIPSorcery callbacks. Populate selected-pair fields only through a documented/public SIPSorcery API. If unavailable, keep them unknown and document that limitation in the projection.

- [ ] **Step 4: Add a credential-leak regression test**

Construct ICE settings containing username/password and assert no diagnostics string/property contains either secret.

- [ ] **Step 5: Run RTC suite and verify GREEN**

- [ ] **Step 6: Commit**

```bash
git add src/SonicDesktopRelay.Rtc/RtcConnectionDiagnostics.cs src/SonicDesktopRelay.Rtc/IPeerConnection.cs src/SonicDesktopRelay.Rtc/IViewerPeerConnection.cs src/SonicDesktopRelay.Rtc/SipSorceryPeerConnection.cs src/SonicDesktopRelay.Rtc/SipSorceryViewerPeerConnection.cs tests/SonicDesktopRelay.Rtc.Tests
git commit -m "feat: expose safe WebRTC route diagnostics"
```

---

### Task 5: Replace FFmpeg-specific Diagnostics UI text

**Files:**
- Modify: `src/SonicDesktopRelay.App/Shell.cs`
- Modify: existing Diagnostics view/bindings if fields are rendered separately.
- Create or modify: `tests/SonicDesktopRelay.App.Tests/ShellDiagnosticsTests.cs`

**Interfaces:**
- Diagnostics text derives only from running host/peer projections.
- Video: WGC, MF transform, hardware/software, dimensions/FPS/bitrate.
- Audio: WASAPI loopback endpoint, Opus 48 kHz/channels, WASAPI playback endpoint/failure.
- WebRTC: ICE state and route (`direct`, `TURN relay`, or `unknown`).

- [ ] **Step 1: Write failing diagnostics projection tests**

Assert publisher text contains `Windows.Graphics.Capture`, `Media Foundation`, transform name, hardware/software classification, `WASAPI loopback`, `Opus`, and route state when known. Assert it does not contain `FFmpeg`, TURN username, or TURN credential.

- [ ] **Step 2: Run RED**

- [ ] **Step 3: Replace `FFmpegLoader.LibraryPath` and FFmpeg-specific wording in `Shell.MediaStatusText`**

Use the host diagnostics objects; do not initialize codecs/devices from the diagnostics getter.

- [ ] **Step 4: Run App tests and verify GREEN**

- [ ] **Step 5: Commit**

```bash
git add src/SonicDesktopRelay.App/Shell.cs tests/SonicDesktopRelay.App.Tests/ShellDiagnosticsTests.cs
git commit -m "feat: show native media diagnostics"
```

---

### Task 6: Prove native parity before deleting FFmpeg

**Files:**
- No production-file changes in this task.

**Interfaces:**
- Gate for the destructive cleanup task.

- [ ] **Step 1: Run platform-neutral media tests**

Run: `dotnet test tests/SonicDesktopRelay.Media.Tests/SonicDesktopRelay.Media.Tests.csproj -c Release`

- [ ] **Step 2: Run Windows media parity tests**

Run: `dotnet test tests/SonicDesktopRelay.Media.Windows.Tests/SonicDesktopRelay.Media.Windows.Tests.csproj -c Release`

Expected: Media Foundation encoder/decoder tests pass on Windows; do not proceed to FFmpeg deletion otherwise.

- [ ] **Step 3: Run RTC tests**

Run: `dotnet test tests/SonicDesktopRelay.Rtc.Tests/SonicDesktopRelay.Rtc.Tests.csproj -c Release`

- [ ] **Step 4: Run App composition/diagnostics tests**

Run: `dotnet test tests/SonicDesktopRelay.App.Tests/SonicDesktopRelay.App.Tests.csproj -c Release`

- [ ] **Step 5: Record the successful test commands in the PR description/checklist before cleanup**

No commit is needed unless a documentation file is changed.

---

### Task 7: Remove FFmpeg code, packages, build acquisition and tests

**Files:**
- Delete: `src/SonicDesktopRelay.Media.Windows/FFmpegH264Encoder.cs`
- Delete: `src/SonicDesktopRelay.Media.Windows/FFmpegH264Decoder.cs`
- Delete: `src/SonicDesktopRelay.Media.Windows/FFmpegLoader.cs`
- Delete: `tests/SonicDesktopRelay.Media.Windows.Tests/FFmpegH264EncoderTests.cs`
- Delete: `tests/SonicDesktopRelay.Media.Windows.Tests/FFmpegH264DecoderTests.cs`
- Delete: `tests/SonicDesktopRelay.Media.Windows.Tests/FFmpegLoaderTests.cs`
- Delete: `build/FFmpeg.props`
- Delete: `build/FFmpeg.targets`
- Delete: `build/FFmpegAcquisition.targets`
- Modify: `src/SonicDesktopRelay.Media.Windows/SonicDesktopRelay.Media.Windows.csproj`
- Modify: `.github/workflows/ci.yml`
- Modify: `.github/workflows/release.yml`
- Modify any project/import file returned by repo-wide `FFmpeg` search.

**Interfaces:**
- Final production Windows media stack has no FFmpeg runtime/package/build dependency.

- [ ] **Step 1: Search the branch before deleting**

Run: `git grep -n -i ffmpeg -- ':!docs/superpowers/specs/*' ':!docs/superpowers/plans/*'`

Classify every hit as production/build/test/current-doc/history. No production/build/test FFmpeg dependency may survive this task.

- [ ] **Step 2: Delete obsolete codec/loader code and FFmpeg-specific tests**

- [ ] **Step 3: Remove `FFmpeg.AutoGen` and the FFmpeg acquisition import from `SonicDesktopRelay.Media.Windows.csproj`**

- [ ] **Step 4: Remove CI/release acquisition, caching, publishing and validation steps for FFmpeg DLLs**

Do not weaken unrelated release validation.

- [ ] **Step 5: Build immediately after deletion**

Run: `dotnet build SonicDesktopRelay.sln -c Release`

Expected: PASS with no missing FFmpeg symbols/imports.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "refactor: remove FFmpeg runtime dependency"
```

---

### Task 8: Update current documentation and third-party notices

**Files:**
- Modify: `README.md`
- Modify: `docs/screen-publishing.md`
- Modify: `THIRD-PARTY-NOTICES.md`
- Create: `docs/native-media-validation.md`

**Interfaces:**
- Current docs describe Media Foundation + WASAPI + Opus and P2P-first WebRTC accurately.
- Historical Superpowers specs/plans may retain FFmpeg references as historical context.

- [ ] **Step 1: Write/update current docs**

`README.md` no longer says FFmpeg is required/bundled. `docs/screen-publishing.md` documents WGC → Media Foundation H.264 and WASAPI → Opus, one encode per session, audio-first SDP compatibility note for SIPSorcery 10.0.16, direct ICE preference and TURN fallback.

- [ ] **Step 2: Update third-party notices**

Remove FFmpeg/FFmpeg.AutoGen notices only after package/code removal. Add notices required by Vortice and NAudio if their licenses/current repo policy require explicit attribution.

- [ ] **Step 3: Add manual native-media validation checklist**

Include:

```text
[ ] 1080p30 screen video
[ ] system audio audible
[ ] NVIDIA hardware encoder path when available
[ ] Intel hardware encoder path when available
[ ] Microsoft software H.264 fallback
[ ] same-LAN direct ICE
[ ] internet STUN/direct ICE where NAT permits
[ ] ForceRelay=true TURN path
[ ] reconnect/renegotiation keeps A/V timeline
[ ] 30+ minute drift/underrun observation
[ ] no FFmpeg DLLs in publish output
```

- [ ] **Step 4: Commit**

```bash
git add README.md docs/screen-publishing.md THIRD-PARTY-NOTICES.md docs/native-media-validation.md
git commit -m "docs: document native Windows media pipeline"
```

---

### Task 9: Final automated verification and release-output inspection

**Files:**
- Modify only if verification exposes a bug; every fix returns to RED→GREEN TDD first.

**Interfaces:**
- Final automated gate before code review.

- [ ] **Step 1: Run full solution tests**

Run: `dotnet test SonicDesktopRelay.sln -c Release`

Expected: PASS, zero failures.

- [ ] **Step 2: Run full solution build**

Run: `dotnet build SonicDesktopRelay.sln -c Release --no-restore`

Expected: PASS, zero errors.

- [ ] **Step 3: Run the repository's existing publish/release verification commands from `.github/workflows/ci.yml`**

Use the same Windows RID/configuration the workflow uses; do not invent a different publish shape.

- [ ] **Step 4: Inspect publish output**

Search output recursively for `avcodec`, `avutil`, `swscale`, `swresample`, `ffmpeg`, and `FFmpeg.AutoGen`. Expected: no runtime artifacts.

- [ ] **Step 5: Search current repository state**

Run: `git grep -n -i ffmpeg -- ':!docs/superpowers/specs/*' ':!docs/superpowers/plans/*'`

Expected: no production/build/test/current-doc dependency references.

- [ ] **Step 6: Fetch GitHub Actions runs for the PR head after pushing all commits**

Expected: required CI jobs green. Any failure is investigated with `superpowers:systematic-debugging`; do not mark the PR ready while required checks fail.
