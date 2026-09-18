# Stream Performance and Publisher Quality Controls Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reduce recovery-induced video stalls, expose the real selected WebRTC transport, and add end-to-end publisher quality/FPS controls without expanding this PR into the deferred capture-queue/GPU pipeline work.

**Architecture:** Keep the existing WGC -> Media Foundation -> SIPSorcery architecture. Split recovery keyframe control from encoder reconfiguration, carry explicit publisher profile/cadence through Presentation -> media -> RTC, and derive safe transport diagnostics from SIPSorcery's nominated ICE pair. Adaptive quality remains global for the shared encoder but is capped by the user's selected profile.

**Tech Stack:** .NET 10, C#, Avalonia, Windows.Graphics.Capture, Vortice.MediaFoundation 3.8.3, SIPSorcery 10.0.16, xUnit, Microsoft.Extensions.Logging.

**Spec:** `docs/superpowers/specs/2026-09-18-stream-performance-quality-controls-design.md`

## Global Constraints

- WebRTC remains SIPSorcery and P2P-first with `ForceRelay=false`.
- Media Foundation remains the Windows H.264 encoder/decoder.
- One capture + one shared encode remains the publisher architecture.
- No FFmpeg, simulcast, SVC or per-viewer encoder is introduced.
- Never log SDP, raw ICE candidate strings, ICE/TURN credentials, candidate addresses/ports or media payloads.
- Quality/FPS UI values are ceilings; adaptive quality may move below them but never above them.
- Do not add the bounded capture -> encode `Channel` in this PR.
- Do not add TURN preference tuning, hardware decoder selection, GPU zero-copy NV12 or broad congestion/pacing changes in this PR.
- Every production behavior change follows RED -> GREEN -> REFACTOR.

## Scope Ledger

### Implemented by this plan

- P0: `ICodecAPI / CODECAPI_AVEncVideoForceKeyFrame` fast recovery path.
- P0: selected ICE candidate-pair classification in Diagnostics.
- P1 partial: keyframe latency, encode/send timing and effective-quality diagnostics.
- P2 partial: correct RTP cadence instead of hardcoded 30 FPS.
- Publisher quality selector: 1080p / 720p / 540p / 360p.
- Publisher FPS selector: 15 / 30 / 60 FPS.
- Adaptive-quality ceiling based on user selection.
- Runtime capture FPS update without restarting WGC.

### Explicitly not implemented by this plan

- P0: bounded latest-frame-wins capture -> encode `Channel`.
- P1 remainder: queue depth and full independent capture/convert/encode/send stage timings.
- P1: prefer TURN/UDP over TURN/TCP/TLS.
- P1: hardware H.264 decoder investigation/selection.
- P2: D3D11 texture -> NV12 zero-copy path.
- P2 remainder: broader RTP pacing/congestion-controller redesign.

The deferred items are not omissions. They are intentionally separated because they either require a new frame-ownership contract or depend on evidence this PR is being added to collect.

---

### Task 1: Publisher profile and quality-ceiling model

**Files:**
- Modify: `src/SonicDesktopRelay.Media/VideoContracts.cs`
- Modify: `src/SonicDesktopRelay.Media/ScreenPublishPipeline.cs`
- Test: `tests/SonicDesktopRelay.Media.Tests/ScreenPublishPipelineTests.cs`
- Test: existing quality-adaptation tests under `tests/SonicDesktopRelay.Media.Tests`

**Interfaces:**
- Produces: a publisher-start profile/value that contains maximum height and maximum FPS.
- Produces: quality-ladder helpers that return the highest allowed rung for a user ceiling and never improve above that ceiling.
- Consumes: existing `VideoQuality` bitrate ladder and per-viewer quality evidence.

- [ ] **Step 1: Write RED tests for the quality ceiling**

Add focused tests proving:

```csharp
[Fact]
public void A_720p_ceiling_never_selects_a_1080p_quality()
{
    var ceiling = new VideoPublishProfile(MaxHeight: 720, MaxFramesPerSecond: 30);
    var quality = VideoQuality.InitialFor(ceiling);

    Assert.Equal(720, quality.MaxHeight);
    Assert.True(quality.FramesPerSecond <= 30);
}

[Fact]
public void Stable_recovery_stops_at_the_user_ceiling()
{
    // Build the existing pipeline/adaptation harness with a 720p/30 ceiling.
    // Drive it down one rung using the existing sustained-loss evidence.
    // Feed the existing stable-reception evidence until recovery is allowed.
    // Assert the resulting quality never has MaxHeight > 720 or FPS > 30.
}
```

- [ ] **Step 2: Run focused tests and verify RED**

Run the relevant Media test project/filter. Expected: compile/test failure because `VideoPublishProfile` / ceiling-aware API does not exist yet.

- [ ] **Step 3: Implement the minimal profile/ceiling API**

Introduce the smallest platform-neutral model needed, for example:

```csharp
public sealed record VideoPublishProfile(int MaxHeight, int MaxFramesPerSecond)
{
    public static VideoPublishProfile Default { get; } = new(1080, 30);
}
```

Add a ceiling-aware initial-quality selection and clamp `Improved()`/pipeline recovery so no effective rung exceeds the profile.

Do not create a second bitrate ladder.

- [ ] **Step 4: Run focused quality tests and verify GREEN**

Run Media quality-adaptation tests. Expected: all focused tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/SonicDesktopRelay.Media tests/SonicDesktopRelay.Media.Tests
git commit -m "feat: add publisher quality ceiling"
```

---

### Task 2: End-to-end Share quality/FPS selection

**Files:**
- Modify: `src/SonicDesktopRelay.Presentation/SessionSnapshot.cs`
- Modify: `src/SonicDesktopRelay.Presentation/SessionRuntime.cs`
- Modify: `src/SonicDesktopRelay.App/Shell.cs`
- Modify: `src/SonicDesktopRelay.App/Views/ShareView.axaml`
- Modify: `src/SonicDesktopRelay.App/RtcVideoPublishHost.cs`
- Test: `tests/SonicDesktopRelay.Presentation.Tests/SessionRuntimeTests.cs`
- Test: application/view-model tests where Shell selection behavior is already covered

**Interfaces:**
- Change: `IVideoPublishHost.StartAsync(MonitorInfo monitor, VideoPublishProfile profile, CancellationToken ct)`.
- Change: `SessionRuntime.StartSharingAsync(MonitorInfo monitor, VideoPublishProfile profile, int maxViewers, CancellationToken ct)`.
- Shell exposes selectable supported quality heights `[1080, 720, 540, 360]` and FPS values `[15, 30, 60]`.
- The selected values are disabled while sharing is active.

- [ ] **Step 1: Write RED Presentation tests**

Add tests proving the selected profile reaches the publish host:

```csharp
[Fact]
public async Task Start_sharing_passes_the_selected_profile_to_the_publish_host()
{
    var profile = new VideoPublishProfile(720, 15);
    var host = new FakePublishHost();
    var runtime = CreateRuntime(host);

    await runtime.StartSharingAsync(Monitor, profile, 3, CancellationToken.None);

    Assert.Equal(profile, host.StartedProfile);
    Assert.Equal(720, runtime.Snapshot.VideoHeight);
    Assert.Equal(15, runtime.Snapshot.FramesPerSecond);
}
```

Update fakes only enough to capture the profile.

- [ ] **Step 2: Run the focused Presentation test and verify RED**

Expected: compile failure from the old `StartSharingAsync` / `IVideoPublishHost.StartAsync` signatures.

- [ ] **Step 3: Thread the profile through runtime and host**

Change the signatures above and start `ScreenPublishPipeline` with the selected ceiling.

The snapshot's reported FPS/height must reflect the selected/effective initial quality, not always `VideoQuality.Default`.

- [ ] **Step 4: Add Share UI selectors**

Expose bindable selected quality and FPS values on `Shell` and render two ComboBoxes above the share controls.

Use user-facing labels:

```text
1080p
720p
540p
360p
```

and:

```text
15 FPS
30 FPS
60 FPS
```

Defaults: 1080p / 30 FPS.

Do not allow profile mutation while a share is active.

- [ ] **Step 5: Run Presentation/App tests and verify GREEN**

Run affected projects. Expected: all existing and new profile-flow tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/SonicDesktopRelay.Presentation src/SonicDesktopRelay.App tests/SonicDesktopRelay.Presentation.Tests
git commit -m "feat: add share quality and fps controls"
```

---

### Task 3: Make capture cadence updateable

**Files:**
- Modify: `src/SonicDesktopRelay.Media/IScreenCaptureSource.cs`
- Modify: `src/SonicDesktopRelay.Media/ScreenPublishPipeline.cs`
- Modify: `src/SonicDesktopRelay.Media.Windows/GraphicsCaptureScreenSource.cs`
- Test: `tests/SonicDesktopRelay.Media.Tests/ScreenPublishPipelineTests.cs`
- Test: `tests/SonicDesktopRelay.Media.Windows.Tests/GraphicsCaptureScreenSourceTests.cs` if an existing seam exists; otherwise test the platform-neutral rate-update contract with a fake capture source

**Interfaces:**
- Add a focused rate-update operation to `IScreenCaptureSource`, e.g. `void SetFrameRate(int framesPerSecond)`.
- `ScreenPublishPipeline` invokes it when effective quality FPS changes.

- [ ] **Step 1: Write RED pipeline test**

```csharp
[Fact]
public void Applying_a_quality_with_new_fps_updates_capture_rate_without_restart()
{
    // Start with 30 FPS using a fake capture source that records StartAsync and SetFrameRate.
    // Drive the quality controller to a rung with lower FPS.
    // Assert StartAsync was called once and SetFrameRate received the new FPS.
}
```

- [ ] **Step 2: Run focused Media test and verify RED**

Expected: failure because capture rate is currently fixed at `StartAsync`.

- [ ] **Step 3: Implement the contract and WGC update**

In `GraphicsCaptureScreenSource`, update `_minimumInterval` under the existing gate:

```csharp
public void SetFrameRate(int framesPerSecond)
{
    if (framesPerSecond <= 0) throw new ArgumentOutOfRangeException(nameof(framesPerSecond));
    lock (_gate)
        _minimumInterval = TimeSpan.FromSeconds(1.0 / framesPerSecond);
}
```

Use the exact locking/lifetime style already present in the source.

- [ ] **Step 4: Run focused tests and verify GREEN**

Expected: capture-rate update tests pass; no capture restart was introduced.

- [ ] **Step 5: Commit**

```bash
git add src/SonicDesktopRelay.Media src/SonicDesktopRelay.Media.Windows tests
git commit -m "feat: update capture cadence with effective fps"
```

---

### Task 4: Carry explicit video duration into RTC and remove hardcoded 30 FPS

**Files:**
- Modify: `src/SonicDesktopRelay.Media/VideoContracts.cs`
- Modify: `src/SonicDesktopRelay.Media.Windows/MediaFoundationH264Encoder.cs`
- Modify: `src/SonicDesktopRelay.Rtc/SipSorceryPeerConnection.cs`
- Update fakes/callers implementing `EncodedVideoSample`
- Test: `tests/SonicDesktopRelay.Rtc.Tests/SipSorceryPeerConnectionTests.cs`
- Test: Media encoder/pipeline tests that construct encoded samples

**Interfaces:**
- Extend `EncodedVideoSample` with explicit frame/media duration, e.g. `TimeSpan Duration`.
- RTC timestamp increment becomes `round(Duration.TotalSeconds * 90000)`.

- [ ] **Step 1: Write RED timing tests**

Create deterministic tests around a small pure helper used by `SipSorceryPeerConnection`:

```csharp
[Theory]
[InlineData(15, 6000u)]
[InlineData(30, 3000u)]
[InlineData(60, 1500u)]
public void RTP_video_duration_matches_configured_fps(int fps, uint expected)
{
    var duration = TimeSpan.FromSeconds(1d / fps);
    Assert.Equal(expected, VideoRtpTiming.ToTimestampUnits(duration));
}
```

- [ ] **Step 2: Run focused RTC tests and verify RED**

Expected: helper/duration contract is missing.

- [ ] **Step 3: Add explicit duration to encoded samples**

Have the encoder/pipeline propagate the configured frame duration already used for Media Foundation input samples.

- [ ] **Step 4: Replace `VideoClockRate / 30`**

Use the sample duration conversion in `SendVideo` and validate non-zero bounded output.

Do not derive cadence from wall clock.

- [ ] **Step 5: Run Media + RTC focused tests and verify GREEN**

Expected: 15/30/60 timing tests and existing RTP tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/SonicDesktopRelay.Media src/SonicDesktopRelay.Media.Windows src/SonicDesktopRelay.Rtc tests
git commit -m "fix: derive video rtp timing from sample cadence"
```

---

### Task 5: Add Media Foundation codec-control keyframe seam

**Files:**
- Create: `src/SonicDesktopRelay.Media.Windows/MediaFoundationCodecControl.cs`
- Modify: `src/SonicDesktopRelay.Media.Windows/MediaFoundationH264Encoder.cs`
- Test: `tests/SonicDesktopRelay.Media.Windows.Tests/MediaFoundationCodecControlTests.cs`
- Test: `tests/SonicDesktopRelay.Media.Windows.Tests/MediaFoundationH264EncoderTests.cs`

**Interfaces:**
- Internal codec-control abstraction with an operation equivalent to `bool TryForceNextKeyFrame()`.
- Encoder diagnostics expose `KeyFrameMode = "codec-api" | "reconfigure-fallback"`.
- No COM type leaves `SonicDesktopRelay.Media.Windows`.

- [ ] **Step 1: Write RED policy tests before COM plumbing**

Extract/test the decision independently:

```csharp
[Fact]
public void Keyframe_request_uses_codec_control_without_reconfigure_when_supported()
{
    var control = new FakeCodecControl(forceResult: true);
    var policy = new EncoderKeyFramePolicy(control);

    policy.Request();
    var action = policy.BeforeNextInput();

    Assert.Equal(KeyFrameAction.CodecControl, action);
    Assert.Equal(1, control.ForceCalls);
}

[Fact]
public void Unsupported_codec_control_uses_explicit_reconfigure_fallback()
{
    var control = new FakeCodecControl(forceResult: false);
    var policy = new EncoderKeyFramePolicy(control);

    policy.Request();

    Assert.Equal(KeyFrameAction.ReconfigureFallback, policy.BeforeNextInput());
}
```

Use an internal testable seam; do not make the test depend on a physical NVIDIA encoder.

- [ ] **Step 2: Run focused tests and verify RED**

Expected: policy/control types do not exist.

- [ ] **Step 3: Implement minimal policy and COM adapter**

Implement only the `ICodecAPI` functionality required to set `CODECAPI_AVEncVideoForceKeyFrame` on the selected transform.

Use official Media Foundation GUID/VARIANT semantics. Keep QueryInterface, PROPVARIANT/VARIANT lifetime and HRESULT handling inside the new Windows file.

Do not generalize this into a full CodecAPI wrapper.

- [ ] **Step 4: Change `MediaFoundationH264Encoder.RequestKeyFrame` behavior**

Remove `_forceKeyFrame` from the generic `requiresReconfigure` condition.

Before the next `ProcessInput`:

1. consume one pending keyframe request;
2. try codec-control force-keyframe;
3. if unsupported/fails in the defined fallback family, reconfigure once and mark diagnostics as fallback;
4. do not reconfigure when codec control succeeds.

Quality/geometry/FPS/bitrate changes still reconfigure through the existing path.

- [ ] **Step 5: Add diagnostics/timing**

Record:

- keyframe mode;
- recovery request timestamp;
- keyframe production latency;
- sampled/Trace encode duration.

Do not emit per-frame Information logs.

- [ ] **Step 6: Run Windows media tests and verify GREEN**

Required assertions include no reconfigure on supported codec-control recovery, one-shot request consumption and fallback correctness.

- [ ] **Step 7: Commit**

```bash
git add src/SonicDesktopRelay.Media.Windows tests/SonicDesktopRelay.Media.Windows.Tests
git commit -m "fix: force recovery keyframes without rebuilding encoder"
```

---

### Task 6: Selected ICE pair classification

**Files:**
- Create: `src/SonicDesktopRelay.Rtc/RtcTransportDiagnostics.cs`
- Modify: `src/SonicDesktopRelay.Rtc/IPeerConnection.cs`
- Modify: `src/SonicDesktopRelay.Rtc/IViewerPeerConnection.cs`
- Modify: `src/SonicDesktopRelay.Rtc/SipSorceryPeerConnection.cs`
- Modify: `src/SonicDesktopRelay.Rtc/SipSorceryViewerPeerConnection.cs`
- Modify: `src/SonicDesktopRelay.Rtc/VideoPublisher.cs`
- Test: `tests/SonicDesktopRelay.Rtc.Tests/SipSorceryPeerConnectionTests.cs`
- Test: `tests/SonicDesktopRelay.Rtc.Tests/SipSorceryViewerPeerConnectionTests.cs`
- Test: pure classification tests in RTC tests

**Interfaces:**
- Produce metadata-only `RtcTransportDiagnostics`.
- Connection interfaces expose current diagnostics and/or a transport-changed event without exposing SIPSorcery candidate objects.

- [ ] **Step 1: Write RED pure classification tests**

Cover at least:

```text
host + host, udp   -> Direct / UDP
host + srflx, udp  -> Direct / UDP
relay + host, udp  -> TURN / UDP
host + relay, udp  -> TURN / UDP
relay + host, tcp  -> TURN / TCP
```

Also prove the DTO has no address, port, candidate-string, username or credential fields.

- [ ] **Step 2: Run focused RTC tests and verify RED**

Expected: diagnostics/classifier types missing.

- [ ] **Step 3: Implement classifier**

Read `RTCPeerConnection.GetRtpChannel().NominatedEntry` after ICE reaches connected.

Map only:

- candidate type;
- candidate protocol;
- Direct vs TURN.

If no nominated pair exists yet, expose `null`/pending rather than guessing.

- [ ] **Step 4: Wire publisher and viewer events/state**

The viewer emits a metadata-only transport diagnostic once selected/changed.

Publisher peers surface their current safe transport classification to `VideoPublisher` / publish-host diagnostics. Preserve one-peer-per-viewer behavior.

- [ ] **Step 5: Run RTC tests and verify GREEN**

Expected: classification and connection adapter tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/SonicDesktopRelay.Rtc tests/SonicDesktopRelay.Rtc.Tests
git commit -m "feat: expose selected webrtc transport diagnostics"
```

---

### Task 7: Surface transport and performance diagnostics in App

**Files:**
- Modify: `src/SonicDesktopRelay.App/RtcVideoPublishHost.cs`
- Modify: `src/SonicDesktopRelay.App/RtcVideoWatchHost.cs`
- Modify: `src/SonicDesktopRelay.App/Shell.cs`
- Modify: `src/SonicDesktopRelay.App/Views/DiagnosticsView.axaml` only if a distinct display row is needed
- Test: relevant App/Presentation formatting tests if present

**Interfaces:**
- Publish diagnostics include effective quality, keyframe mode/latency, sampled encode/send timing and safe transport path.
- Watch diagnostics include selected transport path and keep existing RTP loss/recovery counters.

- [ ] **Step 1: Write RED formatting/projection tests**

Assert safe output contains classifications such as:

```text
transport=TURN/UDP
keyframeMode=codec-api
effective=1920x1080@30
```

and cannot contain injected candidate address/credential fields because those are absent from the DTO.

- [ ] **Step 2: Run focused tests and verify RED**

Expected: new diagnostics are absent.

- [ ] **Step 3: Project diagnostics into the existing Diagnostics screen/logging**

Keep high-frequency timing at Trace or sampled aggregates. State transitions/selected transport may be Information.

- [ ] **Step 4: Update stalled viewer wording**

Change the user-facing stalled state to communicate that the connection is alive while video is stalled, e.g. `Connected — video stalled, showing last received frame`.

Do not change `SessionPhase`; `WatchState.Stalled` remains media state, not signaling state.

- [ ] **Step 5: Run focused UI/Presentation tests and verify GREEN**

- [ ] **Step 6: Commit**

```bash
git add src/SonicDesktopRelay.App src/SonicDesktopRelay.Presentation tests
git commit -m "chore: expose media transport performance diagnostics"
```

---

### Task 8: Documentation and full verification

**Files:**
- Modify: `README.md`
- Modify: `docs/screen-publishing.md`
- Modify: `docs/native-media-validation.md`
- Keep this spec/plan synchronized if implementation discovers a factual constraint

**Interfaces:**
- Documentation states the actual implemented behavior and the explicitly deferred performance work.

- [ ] **Step 1: Update docs**

Document:

- Share quality/FPS controls;
- profile as an adaptive ceiling;
- dynamic RTP cadence;
- codec-api recovery fast path and fallback;
- selected Direct/TURN transport diagnostics;
- privacy boundary for ICE diagnostics;
- deferred bounded-channel / GPU / decoder / TURN-preference work.

- [ ] **Step 2: Run full restore/build/tests**

```powershell
dotnet restore SonicDesktopRelay.sln
dotnet build SonicDesktopRelay.sln --configuration Release --no-restore
dotnet test SonicDesktopRelay.sln --configuration Release --no-build --no-restore
dotnet restore src/SonicDesktopRelay.App/SonicDesktopRelay.App.csproj --runtime win-x64
```

Also run the repository's existing release/native-media publish verification commands/workflow.

- [ ] **Step 3: Inspect the final diff against the Scope Ledger**

Confirm none of these accidentally entered the PR:

- bounded capture/encode Channel;
- TURN transport preference changes;
- decoder-selection changes;
- GPU zero-copy work;
- broad congestion-controller rewrite.

- [ ] **Step 4: Perform manual two-machine validation**

Follow the spec's Manual Validation section. Record what was and was not manually reproduced in the PR description.

- [ ] **Step 5: Request code review**

Use `superpowers:requesting-code-review`. Fix any Critical/Important findings using RED -> GREEN regression tests.

- [ ] **Step 6: Re-run full verification after review fixes**

No completion claim before fresh build/test/publish evidence.

- [ ] **Step 7: Update PR description with evidence**

Include:

- root cause/recovery rationale;
- exact implemented/deferred scope;
- test counts and CI run;
- manual-validation status;
- selected transport diagnostics behavior;
- no-credential/no-candidate privacy guarantee.

Do not auto-merge.
