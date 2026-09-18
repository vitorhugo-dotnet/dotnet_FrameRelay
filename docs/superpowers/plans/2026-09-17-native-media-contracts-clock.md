# Native Media Contracts and Clock Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add platform-neutral audio contracts, audio pipelines, and one monotonic media-session clock without introducing Windows or SIPSorcery types into `SonicDesktopRelay.Media`.

**Architecture:** Audio mirrors the existing video pipeline: capture/encode produces one encoded sample stream per session, while decode/playback consumes one stream on the viewer. `MediaSessionClock` is deliberately tiny: it exposes elapsed monotonic session time. Both audio and video publisher pipelines stamp samples from the same clock at media ingress, so renegotiation cannot reset either timeline.

**Tech Stack:** .NET 10, C# 14, xUnit, `TimeProvider`

**Spec:** `docs/superpowers/specs/2026-09-17-native-media-pipeline-design.md`

## Global Constraints

- `SonicDesktopRelay.Media` stays `net10.0` and platform-neutral.
- No Windows, NAudio, Media Foundation, or SIPSorcery types may appear in public Media contracts.
- Preferred Opus clock is 48 kHz; target frame duration is 20 ms.
- Audio and video failures must not share locks or block one another.
- No broad rename of legacy `SonicDesktopRelay.*` projects/namespaces.

---

### Task 1: Audio value contracts

**Files:**
- Create: `src/SonicDesktopRelay.Media/AudioContracts.cs`
- Create: `tests/SonicDesktopRelay.Media.Tests/AudioContractsTests.cs`

**Interfaces:**
- Produces: `AudioFrame`, `EncodedAudioSample`.
- `AudioFrame` carries `ReadOnlyMemory<byte> Data`, `int SampleRate`, `int Channels`, `int SampleCount`, `TimeSpan Timestamp`, and derived `TimeSpan Duration`.
- `EncodedAudioSample` carries `ReadOnlyMemory<byte> Data`, `int SampleCount`, `TimeSpan Duration`, `TimeSpan Timestamp`.

- [ ] **Step 1: Write the failing value-contract tests**

```csharp
[Fact]
public void Audio_frame_duration_is_derived_from_sample_count_and_rate()
{
    var frame = new AudioFrame(new byte[3840], 48_000, 2, 960, TimeSpan.FromSeconds(2));
    Assert.Equal(TimeSpan.FromMilliseconds(20), frame.Duration);
}

[Theory]
[InlineData(0, 2, 960)]
[InlineData(48_000, 0, 960)]
[InlineData(48_000, 2, -1)]
public void Audio_frame_rejects_invalid_clock_shape(int sampleRate, int channels, int sampleCount)
{
    Assert.Throws<ArgumentOutOfRangeException>(() =>
        new AudioFrame(ReadOnlyMemory<byte>.Empty, sampleRate, channels, sampleCount, TimeSpan.Zero));
}
```

- [ ] **Step 2: Run the focused tests and verify RED**

Run: `dotnet test tests/SonicDesktopRelay.Media.Tests/SonicDesktopRelay.Media.Tests.csproj --filter FullyQualifiedName~AudioContractsTests`

Expected: compile failure because `AudioFrame` / `EncodedAudioSample` do not exist.

- [ ] **Step 3: Implement immutable value contracts with constructor validation**

```csharp
namespace SonicDesktopRelay.Media;

public readonly record struct AudioFrame
{
    public AudioFrame(ReadOnlyMemory<byte> data, int sampleRate, int channels, int sampleCount, TimeSpan timestamp)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));
        if (sampleCount < 0) throw new ArgumentOutOfRangeException(nameof(sampleCount));
        Data = data;
        SampleRate = sampleRate;
        Channels = channels;
        SampleCount = sampleCount;
        Timestamp = timestamp;
    }

    public ReadOnlyMemory<byte> Data { get; }
    public int SampleRate { get; }
    public int Channels { get; }
    public int SampleCount { get; }
    public TimeSpan Timestamp { get; }
    public TimeSpan Duration => TimeSpan.FromSeconds(SampleCount / (double)SampleRate);
}

public readonly record struct EncodedAudioSample(
    ReadOnlyMemory<byte> Data,
    int SampleCount,
    TimeSpan Duration,
    TimeSpan Timestamp);
```

- [ ] **Step 4: Run the focused tests and verify GREEN**

Run: same command as Step 2.

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/SonicDesktopRelay.Media/AudioContracts.cs tests/SonicDesktopRelay.Media.Tests/AudioContractsTests.cs
git commit -m "feat: add platform-neutral audio contracts"
```

---

### Task 2: Audio abstraction interfaces

**Files:**
- Create: `src/SonicDesktopRelay.Media/IAudioCaptureSource.cs`
- Create: `src/SonicDesktopRelay.Media/IAudioEncoder.cs`
- Create: `src/SonicDesktopRelay.Media/IAudioDecoder.cs`
- Create: `src/SonicDesktopRelay.Media/IAudioSink.cs`
- Create: `tests/SonicDesktopRelay.Media.Tests/AudioAbstractionsTests.cs`

**Interfaces:**
- `IAudioCaptureSource : IAsyncDisposable`: `event Action<AudioFrame>? AudioCaptured`, `Task StartAsync(CancellationToken)`, `Task StopAsync()`.
- `IAudioEncoder : IDisposable`: `string Name`, `EncodedAudioSample? Encode(AudioFrame)`.
- `IAudioDecoder : IDisposable`: `string Name`, `AudioFrame? Decode(EncodedAudioSample)`.
- `IAudioSink : IAsyncDisposable`: `string Name`, `Task StartAsync(CancellationToken)`, `void Write(AudioFrame)`, `Task StopAsync()`.

- [ ] **Step 1: Write compile-time fake implementations in tests**

```csharp
private sealed class FakeCapture : IAudioCaptureSource
{
    public event Action<AudioFrame>? AudioCaptured;
    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
    public Task StopAsync() => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public void Push(AudioFrame frame) => AudioCaptured?.Invoke(frame);
}

private sealed class FakeEncoder : IAudioEncoder
{
    public string Name => "fake-opus-encoder";
    public EncodedAudioSample? Encode(AudioFrame frame) =>
        new(new byte[] { 1 }, frame.SampleCount, frame.Duration, frame.Timestamp);
    public void Dispose() { }
}

private sealed class FakeDecoder : IAudioDecoder
{
    public string Name => "fake-opus-decoder";
    public AudioFrame? Decode(EncodedAudioSample sample) =>
        new(new byte[3840], 48_000, 2, sample.SampleCount, sample.Timestamp);
    public void Dispose() { }
}

private sealed class FakeSink : IAudioSink
{
    public string Name => "fake-sink";
    public List<AudioFrame> Frames { get; } = [];
    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
    public void Write(AudioFrame frame) => Frames.Add(frame);
    public Task StopAsync() => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
```

Add a test that constructs each fake through the interface type and passes only `AudioFrame` / `EncodedAudioSample`; this is the compile-time guard that the abstractions remain platform-neutral.

- [ ] **Step 2: Run tests and verify RED**

Expected: interfaces are missing.

- [ ] **Step 3: Add the four interfaces exactly as specified above**

Do not add lifecycle/state concepts beyond those members.

- [ ] **Step 4: Run tests and verify GREEN**

Run: `dotnet test tests/SonicDesktopRelay.Media.Tests/SonicDesktopRelay.Media.Tests.csproj --filter FullyQualifiedName~AudioAbstractionsTests`

- [ ] **Step 5: Commit**

```bash
git add src/SonicDesktopRelay.Media/IAudioCaptureSource.cs src/SonicDesktopRelay.Media/IAudioEncoder.cs src/SonicDesktopRelay.Media/IAudioDecoder.cs src/SonicDesktopRelay.Media/IAudioSink.cs tests/SonicDesktopRelay.Media.Tests/AudioAbstractionsTests.cs
git commit -m "feat: add audio media abstractions"
```

---

### Task 3: Monotonic media-session clock

**Files:**
- Create: `src/SonicDesktopRelay.Media/MediaSessionClock.cs`
- Create: `tests/SonicDesktopRelay.Media.Tests/MediaSessionClockTests.cs`

**Interfaces:**
- Produces: `sealed class MediaSessionClock(TimeProvider timeProvider)` with read-only `TimeSpan Now`.
- The session origin is captured once in the constructor. There is deliberately no per-track source origin and no renegotiation/reset API.

- [ ] **Step 1: Write deterministic elapsed-time tests**

```csharp
[Fact]
public void Now_is_monotonic_elapsed_time_from_one_session_origin()
{
    var time = new ManualTimeProvider();
    var clock = new MediaSessionClock(time);

    Assert.Equal(TimeSpan.Zero, clock.Now);
    time.Advance(TimeSpan.FromMilliseconds(20));
    Assert.Equal(TimeSpan.FromMilliseconds(20), clock.Now);
    time.Advance(TimeSpan.FromMilliseconds(80));
    Assert.Equal(TimeSpan.FromMilliseconds(100), clock.Now);
}
```

`ManualTimeProvider` is a test-only subclass whose `GetTimestamp()` starts at zero, `TimestampFrequency` is `TimeSpan.TicksPerSecond`, and `Advance` increments the timestamp by `delta.Ticks`.

- [ ] **Step 2: Run focused tests and verify RED**

Run: `dotnet test tests/SonicDesktopRelay.Media.Tests/SonicDesktopRelay.Media.Tests.csproj --filter FullyQualifiedName~MediaSessionClockTests`

- [ ] **Step 3: Implement only the shared elapsed-time clock**

```csharp
public sealed class MediaSessionClock(TimeProvider timeProvider)
{
    private readonly long _started = timeProvider.GetTimestamp();
    public TimeSpan Now => timeProvider.GetElapsedTime(_started, timeProvider.GetTimestamp());
}
```

No locks are required because `TimeProvider.GetTimestamp()` is the monotonic source and the start timestamp is immutable.

- [ ] **Step 4: Run tests and verify GREEN**

- [ ] **Step 5: Commit**

```bash
git add src/SonicDesktopRelay.Media/MediaSessionClock.cs tests/SonicDesktopRelay.Media.Tests/MediaSessionClockTests.cs
git commit -m "feat: add monotonic media session clock"
```

---

### Task 4: Audio publish pipeline

**Files:**
- Create: `src/SonicDesktopRelay.Media/AudioPublishPipeline.cs`
- Create: `tests/SonicDesktopRelay.Media.Tests/AudioPublishPipelineTests.cs`

**Interfaces:**
- Consumes: `IAudioCaptureSource`, `IAudioEncoder`, `MediaSessionClock`.
- Produces: `event Action<EncodedAudioSample>? SampleEncoded`, `event Action<Exception>? Failed`, `StartAsync`, `StopAsync`, `DisposeAsync`.

- [ ] **Step 1: Write one-encode/one-event and timestamp tests**

```csharp
[Fact]
public async Task One_captured_frame_is_encoded_once_and_stamped_from_session_clock()
{
    var time = new ManualTimeProvider();
    var clock = new MediaSessionClock(time);
    var capture = new FakeCapture();
    var encoder = new FakeEncoder();
    await using var pipeline = new AudioPublishPipeline(capture, encoder, clock);
    var samples = new List<EncodedAudioSample>();
    pipeline.SampleEncoded += samples.Add;

    await pipeline.StartAsync(CancellationToken.None);
    time.Advance(TimeSpan.FromMilliseconds(100));
    capture.Push(Pcm20Ms(timestamp: TimeSpan.FromHours(7)));

    Assert.Equal(1, encoder.EncodeCalls);
    Assert.Single(samples);
    Assert.Equal(TimeSpan.FromMilliseconds(100), samples[0].Timestamp);
}
```

- [ ] **Step 2: Write a terminal encoder-failure test**

Configure `FakeEncoder.Encode` to throw `InvalidOperationException`; push two frames; assert `EncodeCalls == 1`, `Failed` fires once, and the second frame is ignored.

- [ ] **Step 3: Run focused tests and verify RED**

Run: `dotnet test tests/SonicDesktopRelay.Media.Tests/SonicDesktopRelay.Media.Tests.csproj --filter FullyQualifiedName~AudioPublishPipelineTests`

- [ ] **Step 4: Implement lifecycle semantics matching `ScreenPublishPipeline`**

On capture: if running, replace the PCM frame timestamp with `clock.Now`, encode once, and replace the encoded sample timestamp with the stamped frame timestamp before raising `SampleEncoded`. On terminal encoder failure: set `_running=false`, unsubscribe `AudioCaptured`, and raise `Failed` once.

- [ ] **Step 5: Run focused tests and verify GREEN**

- [ ] **Step 6: Commit**

```bash
git add src/SonicDesktopRelay.Media/AudioPublishPipeline.cs tests/SonicDesktopRelay.Media.Tests/AudioPublishPipelineTests.cs
git commit -m "feat: add audio publish pipeline"
```

---

### Task 5: Audio watch pipeline

**Files:**
- Create: `src/SonicDesktopRelay.Media/AudioWatchPipeline.cs`
- Create: `tests/SonicDesktopRelay.Media.Tests/AudioWatchPipelineTests.cs`

**Interfaces:**
- Consumes: `IAudioDecoder`, `IAudioSink`.
- Produces: `Task StartAsync(CancellationToken)`, `void Push(EncodedAudioSample)`, `Task StopAsync()`, `DisposeAsync`, `event Action<Exception>? Failed`.

- [ ] **Step 1: Write decode/write behavior test**

```csharp
[Fact]
public async Task Encoded_audio_is_decoded_and_written_to_sink()
{
    var decoder = new FakeDecoder();
    var sink = new FakeSink();
    await using var pipeline = new AudioWatchPipeline(decoder, sink);
    await pipeline.StartAsync(CancellationToken.None);

    pipeline.Push(new EncodedAudioSample(new byte[] { 1 }, 960, TimeSpan.FromMilliseconds(20), TimeSpan.Zero));

    Assert.Equal(1, decoder.DecodeCalls);
    Assert.Single(sink.Frames);
}
```

- [ ] **Step 2: Write decoder- and sink-failure isolation tests**

For decoder failure: make the first `Decode` throw `InvalidOperationException`, push two packets, and assert decoder called once and `Failed` fired once. For sink failure: return one decoded frame but make `Write` throw; push twice and assert sink called once and `Failed` fired once.

- [ ] **Step 3: Run focused tests and verify RED**

- [ ] **Step 4: Implement a bounded synchronous handoff**

`Push` returns immediately after one decode/write attempt; it owns no RTC object and keeps a terminal `_failed` flag so a bad decoder/sink is not invoked on every packet. `StopAsync` stops the sink; `DisposeAsync` stops then disposes sink and decoder exactly once.

- [ ] **Step 5: Run focused tests and verify GREEN**

- [ ] **Step 6: Run the entire platform-neutral media suite**

Run: `dotnet test tests/SonicDesktopRelay.Media.Tests/SonicDesktopRelay.Media.Tests.csproj -c Release`

Expected: all existing video tests plus new audio/clock tests PASS.

- [ ] **Step 7: Commit**

```bash
git add src/SonicDesktopRelay.Media/AudioWatchPipeline.cs tests/SonicDesktopRelay.Media.Tests/AudioWatchPipelineTests.cs
git commit -m "feat: add audio watch pipeline"
```
