# RTP H.264 Unknown-Dimensions Decoder Fix Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the Media Foundation viewer decode H.264 RTP access units whose width/height are unknown at transport time, while adding diagnostics that separate transport, decode, and render-side failures.

**Architecture:** Keep SIPSorcery transport metadata-free for picture geometry. Configure the Media Foundation H.264 decoder with a partial compressed input type when dimensions are unknown, then accept `MF_E_TRANSFORM_STREAM_CHANGE` after SPS/PPS processing and derive the actual output geometry from the negotiated NV12 output type. Track access-unit and decoded-frame counters in `ScreenWatchPipeline`, expose decoder failure/last-frame age through the watch host, and include them in the existing Diagnostics media status.

**Tech Stack:** .NET 10, Vortice.MediaFoundation, SIPSorcery, xUnit, Avalonia.

**Spec:** https://github.com/vitorhugo-dotnet/dotnet_FrameRelay/issues/11

## Global Constraints

- Use systematic debugging first and confirm the root cause with a failing regression test before changing production code.
- H.264 RTP samples may have `Width = 0` and `Height = 0`; do not invent dimensions.
- Preserve the existing known-dimension path and mid-stream resolution-change handling.
- Handle `MF_E_TRANSFORM_STREAM_CHANGE` by selecting a fresh NV12 output type and reading its `MF_MT_FRAME_SIZE`.
- Avoid unrelated refactors.
- Diagnostics must not include SDP, ICE credentials, candidate payloads, or media payload contents.

---

### Task 1: Lock the transport regression with a failing Windows test

**Files:**
- Modify: `tests/SonicDesktopRelay.Media.Windows.Tests/MediaFoundationH264DecoderTests.cs`

**Interfaces:**
- Consumes: `MediaFoundationH264Encoder.Encode(...)` and `MediaFoundationH264Decoder.Decode(EncodedVideoSample)`.
- Produces: regression coverage proving decoder input can arrive with zero geometry.

- [ ] **Step 1: Write the failing test**

Add:

```csharp
[Fact]
public void Decoder_accepts_h264_from_rtp_without_dimensions()
{
    if (!Available) return;

    using var encoder = new MediaFoundationH264Encoder();
    using var decoder = new MediaFoundationH264Decoder();

    var frame = DecodeUntilOutput(
        encoder,
        decoder,
        640,
        360,
        stripTransportDimensions: true);

    Assert.Equal(640, frame.Width);
    Assert.Equal(360, frame.Height);
}
```

Extend `DecodeUntilOutput` with an optional `stripTransportDimensions` parameter and, before decoding, replace the encoded sample with:

```csharp
if (stripTransportDimensions)
    encoded = encoded with { Width = 0, Height = 0 };
```

- [ ] **Step 2: Run RED on Windows CI**

Run the Media.Windows test project in GitHub Actions.

Expected failure: `Decoder_accepts_h264_from_rtp_without_dimensions` produces no frame and reports the decoder's zero-dimension configuration failure.

- [ ] **Step 3: Commit the RED test**

Commit message:

```text
test: reproduce dimensionless RTP H264 decode failure
```

### Task 2: Configure Media Foundation from a partial H.264 input type

**Files:**
- Modify: `src/SonicDesktopRelay.Media.Windows/MediaFoundationH264Decoder.cs`
- Test: `tests/SonicDesktopRelay.Media.Windows.Tests/MediaFoundationH264DecoderTests.cs`

**Interfaces:**
- Consumes: `EncodedVideoSample.Width/Height`, where both may be zero.
- Produces: decoded `VideoFrame` whose dimensions come from the decoder's negotiated output type.

- [ ] **Step 1: Allow unknown geometry only when both dimensions are absent**

Replace unconditional zero-dimension rejection with validation that accepts `0x0` as unknown but rejects partially-known or negative geometry.

- [ ] **Step 2: Build a partial H.264 input media type for unknown geometry**

Always set:

```csharp
mediaType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video).CheckError();
mediaType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264).CheckError();
```

Only add frame size/rate/aspect/interlace attributes when width and height are known.

- [ ] **Step 3: Do not reconfigure every dimensionless RTP sample**

Use sample dimensions for a resolution-change reconfigure only when both are positive. Once geometry has been learned from Media Foundation, `0x0` transport metadata must not reset the decoder.

- [ ] **Step 4: Treat the initial output type as a placeholder when geometry is unknown**

Select an NV12 output type so the MFT can process samples, but do not require placeholder geometry to be complete. Keep output buffer/visible geometry unset until the MFT announces a stream change.

- [ ] **Step 5: Handle `MF_E_TRANSFORM_STREAM_CHANGE` as the source of truth**

On stream change:
1. enumerate the new output types,
2. select NV12,
3. call `SetOutputType`,
4. read `MF_MT_FRAME_SIZE`,
5. configure stride/buffer/BGRA storage from that output type,
6. resume draining output.

If the post-stream-change output type still has no valid geometry, fail that decode with an actionable `LastFailure`.

- [ ] **Step 6: Verify GREEN on Windows CI**

Expected:
- dimensionless regression passes,
- existing same-size decode passes,
- mid-stream resolution-change test passes,
- garbage input remains non-fatal.

- [ ] **Step 7: Commit**

```text
fix: derive viewer H264 geometry from Media Foundation
```

### Task 3: Add viewer decode-path diagnostics

**Files:**
- Modify: `src/SonicDesktopRelay.Media/ScreenWatchPipeline.cs`
- Modify: `tests/SonicDesktopRelay.Media.Tests/ScreenWatchPipelineTests.cs`
- Modify: `src/SonicDesktopRelay.App/RtcVideoWatchHost.cs`
- Modify: `src/SonicDesktopRelay.App/Shell.cs`

**Interfaces:**
- `ScreenWatchPipeline.VideoAccessUnitsReceived : long`
- `ScreenWatchPipeline.DecodedFrames : long`
- `ScreenWatchPipeline.LastDecodedFrameAt : DateTimeOffset?`
- `RtcVideoWatchHost.VideoDecoderFailure : string?`
- `RtcVideoWatchHost.VideoAccessUnitsReceived : long`
- `RtcVideoWatchHost.DecodedFrames : long`
- `RtcVideoWatchHost.LastDecodedFrameAt : DateTimeOffset?`
- `RtcVideoWatchHost.LastDecodedFrameAge : TimeSpan?`

- [ ] **Step 1: Write failing pipeline diagnostics tests**

Add tests asserting:
- submitting a swallowed sample increments access units but not decoded frames,
- successful decode increments both counters and records the fake provider's current UTC time.

- [ ] **Step 2: Run RED**

Run `SonicDesktopRelay.Media.Tests`; expect missing diagnostics members.

- [ ] **Step 3: Implement minimal pipeline counters**

Increment access units before decode. Increment decoded frames and set `LastDecodedFrameAt` only after a non-null frame is produced.

- [ ] **Step 4: Verify pipeline GREEN**

Run `SonicDesktopRelay.Media.Tests`.

- [ ] **Step 5: Project decoder/pipeline diagnostics from the watch host**

Expose `MediaFoundationH264Decoder.LastFailure` and pipeline counters/last-frame age without activating any extra codec instances.

- [ ] **Step 6: Extend existing Diagnostics media status**

Append values equivalent to:

```text
videoAccessUnits=<n> decodedFrames=<n> lastFrame=<timestamp-or-never> age=<duration-or-n/a> decoderFailure=<none-or-message>
```

This makes:
- zero access units => transport/RTP path,
- access units > 0 with zero decoded frames/failure => decoder path,
- decoded frames > 0 while picture is black => UI/render path.

- [ ] **Step 7: Commit**

```text
feat: expose viewer video decode diagnostics
```

### Task 4: Verify and review

**Files:**
- No new production files.

- [ ] **Step 1: Run full Windows CI**

Required commands mirror `.github/workflows/ci.yml`:
```powershell
dotnet restore SonicDesktopRelay.sln
dotnet build SonicDesktopRelay.sln --configuration Release --no-restore
./tests/Publish-GitHubRelease.Tests.ps1
dotnet test SonicDesktopRelay.sln --configuration Release --no-build --no-restore
```

- [ ] **Step 2: Verify issue requirements**

Confirm:
- dimensionless RTP input is accepted,
- actual geometry comes from MF output negotiation,
- stream change is handled,
- known-dimension and mid-stream change tests still pass,
- viewer diagnostics distinguish transport/decode/render stages.

- [ ] **Step 3: Request code review**

Review the branch diff against issue #11. Fix Critical/Important findings before marking ready.

- [ ] **Step 4: Open PR against `main`**

Reference `Fixes #11` and include RED/GREEN CI evidence.
