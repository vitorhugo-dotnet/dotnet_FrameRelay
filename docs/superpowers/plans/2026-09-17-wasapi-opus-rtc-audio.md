# WASAPI, Opus and RTC Audio Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Capture Windows system audio through WASAPI loopback, encode/decode Opus through the existing SIPSorcery managed codec stack, and carry audio beside H.264 on each existing peer connection.

**Architecture:** Windows device I/O stays in `Media.Windows`; PCM↔Opus is wrapped behind the platform-neutral audio contracts; RTC transports already-encoded Opus samples and exposes received samples without owning playback. Audio and video share a peer connection but keep independent media pipelines and failure states.

**Tech Stack:** .NET 10, NAudio.Wasapi 3.1.0, SIPSorcery 10.0.16, SIPSorcery `AudioEncoder`/Concentus Opus, xUnit

**Spec:** `docs/superpowers/specs/2026-09-17-native-media-pipeline-design.md`

## Global Constraints

- Capture system mix from the default Windows render endpoint; no microphone, Stereo Mix, or virtual cable.
- Normalize to 48 kHz PCM; target 20 ms Opus packets (960 samples/channel at 48 kHz).
- Use Opus on the same `RTCPeerConnection` as H.264.
- Production ICE remains `ForceRelay=false` / `RTCIceTransportPolicy.all`.
- `SIPSorcery` stays on 10.0.16 unless a separately verified compatibility/security reason requires an upgrade.
- Compatibility constraint: SIPSorcery issue #1763 is still open for 10.0.16; add the **audio track before the video track** so the BUNDLE-tag/candidate placement matches SIPSorcery's current audio-first stream ordering. Source: https://github.com/sipsorcery-org/sipsorcery/issues/1763
- Do not hand-edit generated SDP as a workaround.

---

### Task 1: Add NAudio WASAPI package

**Files:**
- Modify: `src/SonicDesktopRelay.Media.Windows/SonicDesktopRelay.Media.Windows.csproj`

**Interfaces:**
- Makes `WasapiRecorder` / `WasapiRecorderBuilder`, `WasapiPlayer` / `WasapiPlayerBuilder`, MMDevice enumeration and notification APIs available to the Windows adapter.

- [ ] **Step 1: Add the package reference while retaining temporary FFmpeg references**

```xml
<PackageReference Include="NAudio.Wasapi" Version="3.1.0" />
```

- [ ] **Step 2: Restore and build the Windows media project**

Run: `dotnet restore SonicDesktopRelay.sln && dotnet build src/SonicDesktopRelay.Media.Windows/SonicDesktopRelay.Media.Windows.csproj -c Release --no-restore`

Expected: PASS.

- [ ] **Step 3: Commit**

```bash
git add src/SonicDesktopRelay.Media.Windows/SonicDesktopRelay.Media.Windows.csproj
git commit -m "build: add WASAPI audio dependency"
```

---

### Task 2: System-audio loopback source and exact 20 ms framing

**Files:**
- Create: `src/SonicDesktopRelay.Media.Windows/WasapiLoopbackAudioSource.cs`
- Create: `src/SonicDesktopRelay.Media.Windows/PcmFrameAccumulator.cs`
- Create: `tests/SonicDesktopRelay.Media.Windows.Tests/PcmFrameAccumulatorTests.cs`
- Create: `tests/SonicDesktopRelay.Media.Windows.Tests/WasapiLoopbackAudioSourceTests.cs`

**Interfaces:**
- Implements `IAudioCaptureSource`.
- Exposes Windows-only diagnostics: active endpoint id/name, source format, normalized format, degraded reason.
- `PcmFrameAccumulator` accepts arbitrary callback byte counts and emits exact 20 ms 48 kHz frames.

- [ ] **Step 1: Write accumulator tests before touching WASAPI**

```csharp
[Fact]
public void Arbitrary_chunks_emit_exact_20ms_stereo_frames()
{
    var acc = new PcmFrameAccumulator(sampleRate: 48_000, channels: 2, bitsPerSample: 16, frameSamples: 960);
    var output = new List<byte[]>();
    output.AddRange(acc.Append(new byte[1_000]));
    output.AddRange(acc.Append(new byte[3_000]));

    Assert.Single(output);
    Assert.Equal(960 * 2 * 2, output[0].Length);
}
```

Also test remainder preservation across callbacks and reset on device reopen.

- [ ] **Step 2: Run focused accumulator tests and verify RED**

- [ ] **Step 3: Implement the bounded accumulator**

Use a fixed/reusable buffer sized for a small number of codec frames; do not append indefinitely. Emit copied frame slices only at the boundary handed to the encoder.

- [ ] **Step 4: Write source lifecycle tests around an injectable recorder factory**

Assert start/stop idempotence, normalized 48 kHz output metadata, endpoint diagnostics, and one `AudioCaptured` event per full 20 ms frame.

- [ ] **Step 5: Implement WASAPI loopback source**

Use the modern NAudio 3 WASAPI recording API against the current default render endpoint. Convert native float/PCM formats to signed 16-bit 48 kHz PCM inside this Windows adapter. If endpoint-notification support indicates the default device changed, stop/dispose/reopen once; on unrecoverable failure emit one bounded failure state while video remains untouched.

- [ ] **Step 6: Run focused Windows audio tests and verify GREEN**

- [ ] **Step 7: Commit**

```bash
git add src/SonicDesktopRelay.Media.Windows/WasapiLoopbackAudioSource.cs src/SonicDesktopRelay.Media.Windows/PcmFrameAccumulator.cs tests/SonicDesktopRelay.Media.Windows.Tests/PcmFrameAccumulatorTests.cs tests/SonicDesktopRelay.Media.Windows.Tests/WasapiLoopbackAudioSourceTests.cs
git commit -m "feat: capture system audio with WASAPI loopback"
```

---

### Task 3: Managed Opus encoder/decoder adapters

**Files:**
- Create: `src/SonicDesktopRelay.Rtc/OpusAudioCodec.cs`
- Create: `tests/SonicDesktopRelay.Rtc.Tests/OpusAudioCodecTests.cs`
- Modify: `src/SonicDesktopRelay.Rtc/SonicDesktopRelay.Rtc.csproj` only if a direct abstractions package reference is needed for compile clarity.

**Interfaces:**
- Implements project `IAudioEncoder` and `IAudioDecoder` using `SIPSorcery.Media.AudioEncoder` with Opus enabled.
- Uses one negotiated `AudioFormat` representing Opus/48 kHz/stereo.

- [ ] **Step 1: Write an Opus round-trip test**

```csharp
[Fact]
public void Twenty_ms_pcm_round_trips_through_opus()
{
    using var codec = new OpusAudioCodec(channels: 2);
    var pcm = TestTone.Sine48KhzStereo(milliseconds: 20);
    var encoded = codec.Encode(new AudioFrame(pcm, 48_000, 2, 960, TimeSpan.Zero));
    Assert.NotNull(encoded);
    Assert.NotEmpty(encoded!.Value.Data.ToArray());

    var decoded = codec.Decode(encoded.Value);
    Assert.NotNull(decoded);
    Assert.Equal(48_000, decoded!.Value.SampleRate);
    Assert.Equal(2, decoded.Value.Channels);
    Assert.Equal(960, decoded.Value.SampleCount);
}
```

Do not assert byte-for-byte PCM equality because Opus is lossy.

- [ ] **Step 2: Run focused test and verify RED**

- [ ] **Step 3: Implement using SIPSorcery's managed `AudioEncoder`**

Create/reuse one `AudioFormat` restricted to `AudioCodecsEnum.OPUS`, 48 kHz, configured channel count. Convert little-endian PCM16 bytes to `short[]` for `EncodeAudio`, and `short[]` back to little-endian bytes after `DecodeAudio`.

- [ ] **Step 4: Run focused test and verify GREEN**

- [ ] **Step 5: Commit**

```bash
git add src/SonicDesktopRelay.Rtc/OpusAudioCodec.cs tests/SonicDesktopRelay.Rtc.Tests/OpusAudioCodecTests.cs
git commit -m "feat: encode and decode Opus with SIPSorcery"
```

---

### Task 4: WASAPI playback sink with bounded buffer

**Files:**
- Create: `src/SonicDesktopRelay.Media.Windows/WasapiAudioSink.cs`
- Create: `src/SonicDesktopRelay.Media.Windows/BoundedPcmBuffer.cs`
- Create: `tests/SonicDesktopRelay.Media.Windows.Tests/BoundedPcmBufferTests.cs`
- Create: `tests/SonicDesktopRelay.Media.Windows.Tests/WasapiAudioSinkTests.cs`

**Interfaces:**
- Implements project `IAudioSink`.
- Exposes active playback endpoint diagnostics and one degraded reason.

- [ ] **Step 1: Write buffer tests**

Assert bounded capacity, FIFO reads, silence/empty read on underrun, and dropping oldest frames on overflow.

```csharp
[Fact]
public void Overflow_discards_oldest_audio_instead_of_growing_latency()
{
    var buffer = new BoundedPcmBuffer(maxBytes: 8);
    buffer.Write(new byte[] {1,2,3,4,5,6});
    buffer.Write(new byte[] {7,8,9,10});
    Assert.Equal(new byte[] {3,4,5,6,7,8,9,10}, buffer.Snapshot());
}
```

- [ ] **Step 2: Run RED, implement buffer, run GREEN**

- [ ] **Step 3: Write sink lifecycle tests around an injectable player factory**

Assert start/stop idempotence, endpoint name projection, 48 kHz format, and that `Write` after terminal sink failure is a bounded no-op rather than an exception storm.

- [ ] **Step 4: Implement modern NAudio WASAPI playback**

Use `WasapiPlayer`/builder in shared mode with a provider backed by `BoundedPcmBuffer`. Reopen on default-device change when practical. Keep buffer depth intentionally low and document the selected upper bound in milliseconds.

- [ ] **Step 5: Run focused tests and verify GREEN**

- [ ] **Step 6: Commit**

```bash
git add src/SonicDesktopRelay.Media.Windows/WasapiAudioSink.cs src/SonicDesktopRelay.Media.Windows/BoundedPcmBuffer.cs tests/SonicDesktopRelay.Media.Windows.Tests/BoundedPcmBufferTests.cs tests/SonicDesktopRelay.Media.Windows.Tests/WasapiAudioSinkTests.cs
git commit -m "feat: play received audio through WASAPI"
```

---

### Task 5: Extend RTC interfaces for encoded audio

**Files:**
- Modify: `src/SonicDesktopRelay.Rtc/IPeerConnection.cs`
- Modify: `src/SonicDesktopRelay.Rtc/IViewerPeerConnection.cs`
- Modify fakes in: `tests/SonicDesktopRelay.Rtc.Tests/VideoPublisherTests.cs`
- Modify fakes in: `tests/SonicDesktopRelay.Rtc.Tests/VideoSubscriberTests.cs`

**Interfaces:**
- `IPeerConnection.SendAudio(EncodedAudioSample sample)`.
- `IViewerPeerConnection.event Action<EncodedAudioSample>? AudioSampleReceived`.

- [ ] **Step 1: Update test fakes first and add assertions that audio can traverse interfaces without any SIPSorcery type**

- [ ] **Step 2: Run RTC suite and verify RED until production interfaces match**

- [ ] **Step 3: Add only the two required interface members**

Do not introduce SIPSorcery `AudioFormat`, RTP packet, or device types into these interfaces.

- [ ] **Step 4: Run RTC suite and verify GREEN**

- [ ] **Step 5: Commit**

```bash
git add src/SonicDesktopRelay.Rtc/IPeerConnection.cs src/SonicDesktopRelay.Rtc/IViewerPeerConnection.cs tests/SonicDesktopRelay.Rtc.Tests/VideoPublisherTests.cs tests/SonicDesktopRelay.Rtc.Tests/VideoSubscriberTests.cs
git commit -m "feat: extend RTC contracts for audio"
```

---

### Task 6: Publisher WebRTC SDP with Opus + H.264

**Files:**
- Modify: `src/SonicDesktopRelay.Rtc/SipSorceryPeerConnection.cs`
- Modify: `tests/SonicDesktopRelay.Rtc.Tests/SipSorceryPeerConnectionTests.cs`

**Interfaces:**
- Audio payload uses Opus at 48 kHz.
- Audio track send-only; video track send-only.
- **Audio track is added before video track** for SIPSorcery 10.0.16 BUNDLE compatibility.

- [ ] **Step 1: Replace the old no-audio test with explicit A/V SDP tests**

```csharp
[Fact]
public async Task Offer_advertises_sendonly_opus_audio_and_h264_video()
{
    var factory = new SipSorceryPeerConnectionFactory(Ice);
    await using var peer = factory.Create(Guid.NewGuid());
    var sdp = await peer.CreateOfferAsync(CancellationToken.None);

    Assert.Contains("m=audio", sdp);
    Assert.Contains("opus/48000", sdp, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("m=video", sdp);
    Assert.Contains("H264", sdp, StringComparison.OrdinalIgnoreCase);
    Assert.Equal(2, CountOccurrences(sdp, "a=sendonly"));
    Assert.True(sdp.IndexOf("m=audio", StringComparison.Ordinal) < sdp.IndexOf("m=video", StringComparison.Ordinal));
}
```

Retain existing relay-policy and pre-answer-send tests; add `SendAudio` pre-answer no-throw test.

- [ ] **Step 2: Run focused tests and verify RED**

- [ ] **Step 3: Add Opus track before H.264 track**

Use SIPSorcery `AudioFormat` for Opus 48 kHz and `MediaStreamStatusEnum.SendOnly`, then add the existing H.264 track.

- [ ] **Step 4: Implement `SendAudio`**

After negotiation/connected checks, call `_connection.SendAudio((uint)sample.Duration.TotalMilliseconds, sample.Data.ToArray())`; catch the same bounded socket/disposal failure family as video.

- [ ] **Step 5: Run focused tests and verify GREEN**

- [ ] **Step 6: Commit**

```bash
git add src/SonicDesktopRelay.Rtc/SipSorceryPeerConnection.cs tests/SonicDesktopRelay.Rtc.Tests/SipSorceryPeerConnectionTests.cs
git commit -m "feat: negotiate and send Opus audio"
```

---

### Task 7: Viewer WebRTC SDP and encoded audio receive path

**Files:**
- Modify: `src/SonicDesktopRelay.Rtc/SipSorceryViewerPeerConnection.cs`
- Modify: `tests/SonicDesktopRelay.Rtc.Tests/SipSorceryViewerPeerConnectionTests.cs`

**Interfaces:**
- Audio track recv-only and added before video.
- `OnAudioFrameReceived` maps `EncodedAudioFrame` to project `EncodedAudioSample`.

- [ ] **Step 1: Replace the hard-coded video-only offer fixture with an audio-first A/V offer**

Fixture must contain `a=group:BUNDLE 0 1`, `m=audio ... opus/48000` with `a=mid:0`, and `m=video ... H264/90000` with `a=mid:1`.

- [ ] **Step 2: Write answer assertions**

Assert both m-lines, two `a=recvonly` directions, Opus 48 kHz, H.264 packetization mode 1, and audio-first m-line order.

- [ ] **Step 3: Run focused tests and verify RED**

- [ ] **Step 4: Add receive tracks in audio-first order and wire `OnAudioFrameReceived`**

Convert the SIPSorcery frame's encoded payload and duration to `EncodedAudioSample`. Track a 48 kHz audio RTP/session timestamp accumulator monotonically; do not infer audio time from wall-clock arrival time.

- [ ] **Step 5: Run focused tests and verify GREEN**

- [ ] **Step 6: Run all RTC tests**

Run: `dotnet test tests/SonicDesktopRelay.Rtc.Tests/SonicDesktopRelay.Rtc.Tests.csproj -c Release`

- [ ] **Step 7: Commit**

```bash
git add src/SonicDesktopRelay.Rtc/SipSorceryViewerPeerConnection.cs tests/SonicDesktopRelay.Rtc.Tests/SipSorceryViewerPeerConnectionTests.cs
git commit -m "feat: receive Opus audio over WebRTC"
```

---

### Task 8: Fan out encoded audio to every existing viewer peer

**Files:**
- Modify: `src/SonicDesktopRelay.Rtc/VideoPublisher.cs`
- Modify: `tests/SonicDesktopRelay.Rtc.Tests/VideoPublisherTests.cs`

**Interfaces:**
- Constructor gains/accepts the audio publish pipeline or a shared media-publisher abstraction without creating another peer connection.
- One encoded audio sample is sent once to each connected viewer peer.

- [ ] **Step 1: Write a multi-viewer fan-out test**

```csharp
[Fact]
public async Task One_audio_encode_is_fanned_out_to_all_viewer_peers()
{
    // Arrange one AudioPublishPipeline and two fake peers.
    // Push exactly one PCM frame through capture.
    // Assert encoder.EncodeCalls == 1 and each peer.AudioSamples.Count == 1.
}
```

- [ ] **Step 2: Run RED**

- [ ] **Step 3: Subscribe the existing publisher to `AudioPublishPipeline.SampleEncoded`**

Do not move encoding into the per-peer loop. Dispose/unsubscribe audio pipeline deterministically.

- [ ] **Step 4: Run GREEN**

- [ ] **Step 5: Commit**

```bash
git add src/SonicDesktopRelay.Rtc/VideoPublisher.cs tests/SonicDesktopRelay.Rtc.Tests/VideoPublisherTests.cs
git commit -m "feat: fan out encoded audio to viewers"
```

---

### Task 9: Route received Opus into audio watch pipeline

**Files:**
- Modify: `src/SonicDesktopRelay.Rtc/VideoSubscriber.cs`
- Modify: `tests/SonicDesktopRelay.Rtc.Tests/VideoSubscriberTests.cs`

**Interfaces:**
- Subscriber accepts both `ScreenWatchPipeline` and `AudioWatchPipeline` but still owns exactly one `IViewerPeerConnection`.

- [ ] **Step 1: Write test that video and audio events route independently**

Push one fake video sample and one fake audio sample; assert the video decoder path receives only video and audio decoder/sink receives only audio. Make the fake audio decoder fail and assert subsequent video still decodes.

- [ ] **Step 2: Run RED**

- [ ] **Step 3: Wire `AudioSampleReceived` to `AudioWatchPipeline.Push`**

Do not make audio failure dispose the viewer peer or screen pipeline.

- [ ] **Step 4: Run GREEN and full RTC suite**

- [ ] **Step 5: Commit**

```bash
git add src/SonicDesktopRelay.Rtc/VideoSubscriber.cs tests/SonicDesktopRelay.Rtc.Tests/VideoSubscriberTests.cs
git commit -m "feat: route received Opus to audio playback pipeline"
```
