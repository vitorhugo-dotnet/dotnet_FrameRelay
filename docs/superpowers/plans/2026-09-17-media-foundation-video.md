# Media Foundation Video Backend Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the FFmpeg-backed Windows H.264 encoder/decoder with Media Foundation implementations that preserve the existing `IVideoEncoder` / `IVideoDecoder` contracts and behavior.

**Architecture:** `SonicDesktopRelay.Media.Windows` owns Media Foundation startup, MFT discovery/configuration, BGRA→NV12 conversion, H.264 access-unit normalization, and decoder output conversion. The first implementation deliberately preserves the existing CPU-backed `VideoFrame` seam; the native boundary is designed so a later D3D11 surface path can be introduced internally without changing `Media`, RTC, signaling, or Presentation.

**Tech Stack:** .NET 10 Windows, Vortice.MediaFoundation 3.8.3, Vortice.Direct3D11 3.8.3, Media Foundation `IMFTransform` / `ICodecAPI`, xUnit

**Spec:** `docs/superpowers/specs/2026-09-17-native-media-pipeline-design.md`

## Global Constraints

- Keep `Windows.Graphics.Capture` and current monitor/cursor/throttling behavior.
- Prefer hardware H.264 MFTs, then Microsoft/system software fallback.
- Configure H.264 for WebRTC `packetization-mode=1` compatibility and low latency.
- Preserve the current quality ladder, forced keyframes, mid-session resolution changes, and one encode per session.
- Do not remove FFmpeg until Media Foundation parity tests pass.
- Do not leak Media Foundation COM types outside `SonicDesktopRelay.Media.Windows`.
- `Vortice.MediaFoundation` and `Vortice.Direct3D11` must both be pinned to 3.8.3.

---

### Task 1: Align native Windows package dependencies

**Files:**
- Modify: `src/SonicDesktopRelay.Media.Windows/SonicDesktopRelay.Media.Windows.csproj`
- Modify: `tests/SonicDesktopRelay.Media.Windows.Tests/SonicDesktopRelay.Media.Windows.Tests.csproj` only if direct package visibility is required by compile-time tests.

**Interfaces:**
- Produces the package surface used by subsequent tasks.

- [ ] **Step 1: Add a dependency assertion test/script or inspect restore lock through CI**

Expected project references after the change:

```xml
<PackageReference Include="FFmpeg.AutoGen" Version="8.1.0" />
<PackageReference Include="Vortice.Direct3D11" Version="3.8.3" />
<PackageReference Include="Vortice.MediaFoundation" Version="3.8.3" />
```

FFmpeg remains temporarily because parity work is not complete yet.

- [ ] **Step 2: Run restore/build and verify the current project still builds**

Run: `dotnet restore SonicDesktopRelay.sln && dotnet build src/SonicDesktopRelay.Media.Windows/SonicDesktopRelay.Media.Windows.csproj -c Release --no-restore`

- [ ] **Step 3: Update the project file**

Upgrade `Vortice.Direct3D11` from 3.6.2 to 3.8.3 and add `Vortice.MediaFoundation` 3.8.3.

- [ ] **Step 4: Run build again and verify GREEN**

- [ ] **Step 5: Commit**

```bash
git add src/SonicDesktopRelay.Media.Windows/SonicDesktopRelay.Media.Windows.csproj
git commit -m "build: add Media Foundation bindings"
```

---

### Task 2: Reference-counted Media Foundation runtime

**Files:**
- Create: `src/SonicDesktopRelay.Media.Windows/MediaFoundationRuntime.cs`
- Create: `tests/SonicDesktopRelay.Media.Windows.Tests/MediaFoundationRuntimeTests.cs`

**Interfaces:**
- Produces: internal `MediaFoundationRuntime.Acquire()` returning an `IDisposable` lease.
- Startup occurs on first lease; shutdown occurs after last lease.

- [ ] **Step 1: Write lifecycle tests around an injectable native adapter**

```csharp
[Fact]
public void Runtime_starts_once_and_shuts_down_after_last_lease()
{
    var native = new FakeMediaFoundationNative();
    var runtime = new MediaFoundationRuntime(native);

    using var a = runtime.Acquire();
    using (var b = runtime.Acquire())
        Assert.Equal(1, native.StartupCalls);

    Assert.Equal(0, native.ShutdownCalls);
    a.Dispose();
    Assert.Equal(1, native.ShutdownCalls);
}
```

Also test that failed startup leaves the reference count at zero and a later retry is possible.

- [ ] **Step 2: Run focused tests and verify RED**

- [ ] **Step 3: Implement a tiny adapter around `MFStartup` / `MFShutdown`**

Use a private lock only around reference-count state. Do not call startup/shutdown per frame or per peer.

- [ ] **Step 4: Run focused tests and verify GREEN**

- [ ] **Step 5: Commit**

```bash
git add src/SonicDesktopRelay.Media.Windows/MediaFoundationRuntime.cs tests/SonicDesktopRelay.Media.Windows.Tests/MediaFoundationRuntimeTests.cs
git commit -m "feat: manage Media Foundation runtime lifecycle"
```

---

### Task 3: H.264 access-unit normalization helper

**Files:**
- Create: `src/SonicDesktopRelay.Media.Windows/H264AccessUnit.cs`
- Create: `tests/SonicDesktopRelay.Media.Windows.Tests/H264AccessUnitTests.cs`

**Interfaces:**
- Produces pure helpers that convert length-prefixed AVC samples into Annex-B access units and identify IDR/SPS/PPS NAL units.

- [ ] **Step 1: Write pure tests with known NAL byte sequences**

```csharp
[Fact]
public void Length_prefixed_nals_are_normalized_to_annex_b()
{
    byte[] avcc = [0,0,0,2,0x67,0x01, 0,0,0,2,0x65,0x02];
    var annexB = H264AccessUnit.ToAnnexB(avcc, 4);

    Assert.Equal(new byte[] {0,0,0,1,0x67,0x01, 0,0,0,1,0x65,0x02}, annexB);
    Assert.True(H264AccessUnit.ContainsKeyFrame(annexB));
}
```

Add tests for already-Annex-B input and malformed lengths returning a controlled failure rather than out-of-range memory access.

- [ ] **Step 2: Run focused tests and verify RED**

- [ ] **Step 3: Implement the helper without native dependencies**

Keep it internal to the Windows project because it exists to normalize MFT output for the existing RTP path.

- [ ] **Step 4: Run focused tests and verify GREEN**

- [ ] **Step 5: Commit**

```bash
git add src/SonicDesktopRelay.Media.Windows/H264AccessUnit.cs tests/SonicDesktopRelay.Media.Windows.Tests/H264AccessUnitTests.cs
git commit -m "feat: normalize Media Foundation H264 access units"
```

---

### Task 4: BGRA to NV12 frame conversion boundary

**Files:**
- Create: `src/SonicDesktopRelay.Media.Windows/BgraToNv12Converter.cs`
- Create: `tests/SonicDesktopRelay.Media.Windows.Tests/BgraToNv12ConverterTests.cs`

**Interfaces:**
- Consumes: contiguous BGRA `VideoFrame`.
- Produces: reusable NV12 system-memory buffer for the current CPU bridge.

- [ ] **Step 1: Write deterministic color/layout tests on tiny even-sized frames**

Test 2x2 and 4x2 input for output length `width * height * 3 / 2`, Y plane size, UV interleaving, and buffer reuse for same dimensions.

```csharp
[Fact]
public void Nv12_buffer_is_reused_for_same_dimensions()
{
    using var converter = new BgraToNv12Converter();
    var first = converter.Convert(Frame(4, 2));
    var second = converter.Convert(Frame(4, 2));
    Assert.Same(first.Buffer, second.Buffer);
}
```

- [ ] **Step 2: Run focused tests and verify RED**

- [ ] **Step 3: Implement the isolated CPU fallback**

Use integer or vectorized conversion with clamping. Reject odd dimensions because `VideoQuality.ScaleFor` already guarantees even dimensions.

- [ ] **Step 4: Run focused tests and verify GREEN**

- [ ] **Step 5: Commit**

```bash
git add src/SonicDesktopRelay.Media.Windows/BgraToNv12Converter.cs tests/SonicDesktopRelay.Media.Windows.Tests/BgraToNv12ConverterTests.cs
git commit -m "feat: add isolated BGRA to NV12 bridge"
```

---

### Task 5: Media Foundation H.264 encoder selection and diagnostics

**Files:**
- Create: `src/SonicDesktopRelay.Media.Windows/MediaFoundationH264Encoder.cs`
- Create: `src/SonicDesktopRelay.Media.Windows/MediaFoundationTransformInfo.cs`
- Create: `tests/SonicDesktopRelay.Media.Windows.Tests/MediaFoundationH264EncoderTests.cs`

**Interfaces:**
- Implements existing `IVideoEncoder`.
- Exposes existing `string Name` plus Windows-only diagnostics: selected transform name/CLSID, `bool IsHardware`, configured dimensions/bitrate, `IReadOnlyList<string> RejectionLog`.

- [ ] **Step 1: Port the behavioral FFmpeg encoder tests to Media Foundation names**

```csharp
[Fact]
public void The_first_encoded_frame_is_a_keyframe()
{
    if (!MediaFoundationH264Encoder.IsSupported) return;
    using var encoder = new MediaFoundationH264Encoder();
    var sample = EncodeOne(encoder, 1280, 720);
    Assert.NotNull(sample);
    Assert.True(sample!.Value.IsKeyFrame);
}
```

Copy equivalent coverage for quality scaling, forced keyframe, resolution change, non-empty name, and rejection diagnostics.

- [ ] **Step 2: Run focused tests and verify RED**

- [ ] **Step 3: Implement transform enumeration**

Enumerate hardware transforms first, then software transforms for H.264 encoding. For each candidate:

1. activate transform;
2. configure H.264 output type first;
3. configure NV12 input type;
4. set frame size/rate/bitrate;
5. apply `MF_LOW_LATENCY` / `ICodecAPI` low-latency controls when supported;
6. reject and record one concise reason if configuration fails.

Do not log full COM stack traces per frame.

- [ ] **Step 4: Implement synchronous encode flow**

On first frame or quality/dimension change: configure/reconfigure transform, flush stale output, and request IDR. Convert BGRA→NV12, wrap in an MF sample with session timestamp/duration, call `ProcessInput`, drain `ProcessOutput`, normalize to Annex-B, and return `EncodedVideoSample`.

- [ ] **Step 5: Implement `RequestKeyFrame()` using codec controls**

The next successfully emitted access unit after a PLI/FIR request must be an IDR/keyframe or include an equivalent random-access unit.

- [ ] **Step 6: Run focused tests and verify GREEN**

Run: `dotnet test tests/SonicDesktopRelay.Media.Windows.Tests/SonicDesktopRelay.Media.Windows.Tests.csproj --filter FullyQualifiedName~MediaFoundationH264EncoderTests`

- [ ] **Step 7: Commit**

```bash
git add src/SonicDesktopRelay.Media.Windows/MediaFoundationH264Encoder.cs src/SonicDesktopRelay.Media.Windows/MediaFoundationTransformInfo.cs tests/SonicDesktopRelay.Media.Windows.Tests/MediaFoundationH264EncoderTests.cs
git commit -m "feat: encode H264 with Media Foundation"
```

---

### Task 6: Media Foundation H.264 decoder

**Files:**
- Create: `src/SonicDesktopRelay.Media.Windows/MediaFoundationH264Decoder.cs`
- Create: `tests/SonicDesktopRelay.Media.Windows.Tests/MediaFoundationH264DecoderTests.cs`

**Interfaces:**
- Implements existing `IVideoDecoder`.
- Exposes diagnostics equivalent to encoder selection.

- [ ] **Step 1: Port decoder parity tests**

Cover:

- publisher-encoded frame decodes to original size;
- mid-stream 1280x720 → 640x360 resolution change;
- garbage access unit returns `null` rather than throwing;
- diagnostic name contains H.264/transform identity;
- BGRA output buffer is reused for equal dimensions.

- [ ] **Step 2: Run focused tests and verify RED**

- [ ] **Step 3: Implement hardware-first decoder selection**

Configure H.264 input and a low-latency output format supported by the selected transform, preferring NV12. Apply stream-change handling when `ProcessOutput` reports a changed media type.

- [ ] **Step 4: Implement decode/output conversion**

Feed one complete H.264 access unit per input sample. Drain available output. Convert NV12 to the existing reusable BGRA `VideoFrame` buffer. Preserve the encoded sample timestamp.

Malformed/lost data that the decoder can recover from returns `null`; repeated native failure transitions the decoder into one terminal failed state rather than throwing on every packet.

- [ ] **Step 5: Run focused tests and verify GREEN**

- [ ] **Step 6: Run full Windows media suite with both FFmpeg and MF implementations still present**

Run: `dotnet test tests/SonicDesktopRelay.Media.Windows.Tests/SonicDesktopRelay.Media.Windows.Tests.csproj -c Release`

Expected: all existing FFmpeg parity tests and new Media Foundation parity tests PASS on a Windows runner with the relevant codecs available.

- [ ] **Step 7: Commit**

```bash
git add src/SonicDesktopRelay.Media.Windows/MediaFoundationH264Decoder.cs tests/SonicDesktopRelay.Media.Windows.Tests/MediaFoundationH264DecoderTests.cs
git commit -m "feat: decode H264 with Media Foundation"
```

---

### Task 7: Native video diagnostics model

**Files:**
- Create: `src/SonicDesktopRelay.Media.Windows/NativeVideoDiagnostics.cs`
- Modify: `src/SonicDesktopRelay.Media.Windows/MediaFoundationH264Encoder.cs`
- Modify: `src/SonicDesktopRelay.Media.Windows/MediaFoundationH264Decoder.cs`
- Create: `tests/SonicDesktopRelay.Media.Windows.Tests/NativeVideoDiagnosticsTests.cs`

**Interfaces:**
- Produces a credential-free immutable projection containing backend, transform name/CLSID, hardware flag, input/output formats, dimensions/FPS/bitrate and rejection reasons.

- [ ] **Step 1: Write a test that diagnostics are populated from live selected transform state and contain no URLs/credentials**

- [ ] **Step 2: Run RED**

- [ ] **Step 3: Add immutable diagnostics projection and expose it from encoder/decoder**

Do not instantiate extra transforms for Diagnostics UI; only project the transform already selected by the running session.

- [ ] **Step 4: Run GREEN and full Windows media suite**

- [ ] **Step 5: Commit**

```bash
git add src/SonicDesktopRelay.Media.Windows/NativeVideoDiagnostics.cs src/SonicDesktopRelay.Media.Windows/MediaFoundationH264Encoder.cs src/SonicDesktopRelay.Media.Windows/MediaFoundationH264Decoder.cs tests/SonicDesktopRelay.Media.Windows.Tests/NativeVideoDiagnosticsTests.cs
git commit -m "feat: expose Media Foundation video diagnostics"
```
