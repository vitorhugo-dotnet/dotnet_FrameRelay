# Native Media Contracts and Clock Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add platform-neutral audio contracts, audio pipelines, and one monotonic media-session clock without introducing Windows or SIPSorcery types into `SonicDesktopRelay.Media`.

**Architecture:** Audio mirrors the existing video pipeline: capture/encode produces one encoded sample stream per session, while decode/playback consumes one stream on the viewer. `MediaSessionClock` normalizes independent capture timestamps onto a stable session-relative timeline and survives renegotiation because it belongs to the media session, not to a peer connection.

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
- `AudioFrame` carries `ReadOnlyMemory<byte> Data`, `int SampleRate`, `int Channels`, `int SampleCount`, `TimeSpan Timestamp`.
- `EncodedAudioSample` carries `ReadOnlyMemory<byte> Data`, `int SampleCount`, `TimeSpan Duration`, `TimeSpan Timestamp`.

- [ ] **Step 1: Write the failing value-contract tests**

```csharp
[Fact]
public void Audio_frame_duration_is_derived_from_sample_count_and_rate()
{
    var frame = new AudioFrame(new byte[3840], 48_000, 2, 960, TimeSpan.FromSeconds(2));
    Assert.Equal(TimeSpan.FromMilliseconds(20), frame.Duration);
}

[Fact]
public void Audio_contracts_reject_invalid_clock_values()
{
    Assert.Throws<ArgumentOutOfRangeException>(() =>
        new AudioFrame(ReadOnlyMemory<byte>.Empty, 0, 2, 960, TimeSpan.Zero));
    Assert.Throws<ArgumentOutOfRangeException>(() =>
        new AudioFrame(ReadOnlyMemory<byte>.Empty, 48_000, 0, 960, TimeSpan.Zero));
}
```

- [ ] **Step 2: Run the focused tests and verify RED**

Run: `dotnet test tests/SonicDesktopRelay.Media.Tests/SonicDesktopRelay.Media.Tests.csproj --filter FullyQualifiedName~AudioContractsTests`

Expected: compile failure because `AudioFrame` / `EncodedAudioSample` do not exist.

- [ ] **Step 3: Implement immutable value contracts**

```csharp
namespace SonicDesktopRelay.Media;

public readonly record struct AudioFrame(
    ReadOnlyMemory<byte> Data,
    int SampleRate,
    int Channels,
    int SampleCount,
    TimeSpan Timestamp)
{
    public TimeSpan Duration
    {
        get
        {
            if (SampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(SampleRate));
            return TimeSpan.FromSeconds(SampleCount / (double)SampleRate);
        }
    }

    public AudioFrame Validate()
    {
        if (SampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(SampleRate));
        if (Channels <= 0) throw new ArgumentOutOfRangeException(nameof(Channels));
        if (SampleCount < 0) throw new ArgumentOutOfRangeException(nameof(SampleCount));
        return this;
    }
}

public readonly record struct EncodedAudioSample(
    ReadOnlyMemory<byte> Data,
    int SampleCount,
    TimeSpan Duration,
    TimeSpan Timestamp);
```

Use a constructor/factory that performs validation immediately; tests must not rely on calling `Validate()` manually.

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
- `IAudioCaptureSource : IAsyncDisposable` exposes `event Action<AudioFrame>? AudioCaptured`, `Task StartAsync(CancellationToken)`, `Task StopAsync()`.
- `IAudioEncoder : IDisposable` exposes `string Name`, `EncodedAudioSample? Encode(AudioFrame)`.
- `IAudioDecoder : IDisposable` exposes `string Name`, `AudioFrame? Decode(EncodedAudioSample)`.
- `IAudioSink : IAsyncDisposable` exposes `string Name`, `Task StartAsync(CancellationToken)`, `void Write(AudioFrame)`, `Task StopAsync()`.

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
```

Add analogous minimal fake encoder/decoder/sink and assert they can be consumed only through platform-neutral types.

- [ ] **Step 2: Run tests and verify RED**

Expected: interfaces are missing.

- [ ] **Step 3: Add the four focused interfaces**

Do not add lifecycle/state concepts that are not required by the spec.

- [ ] **Step 4: Run tests and verify GREEN**

Run: `dotnet test tests/SonicDesktopRelay.Media.Tests/SonicDesktopRelay.Media.Tests.csproj --filter FullyQualifiedName~AudioAbstractionsTests`

- [ ] **Step 5: Commit**

```bash
git add src/SonicDesktopRelay.Media/I*.cs tests/SonicDesktopRelay.Media.Tests/AudioAbstractionsTests.cs
git commit -m "feat: add audio media abstractions"
```

---

### Task 3: Monotonic media-session clock

**Files:**
- Create: `src/SonicDesktopRelay.Media/MediaSessionClock.cs`
- Create: `tests/SonicDesktopRelay.Media.Tests/MediaSessionClockTests.cs`

**Interfaces:**
- Produces: `sealed class MediaSessionClock(TimeProvider timeProvider)` with `TimeSpan Now` and `TimeSpan Normalize(TimeSpan sourceTimestamp)`.
- The first normalized source timestamp maps to the current session time; subsequent timestamps preserve deltas and never move backwards.

- [ ] **Step 1: Write deterministic tests using `FakeTimeProvider` or a small test `TimeProvider`**

```csharp
[Fact]
public void Normalization_preserves_source_delta_and_never_moves_backwards()
{
    var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
    var clock = new MediaSessionClock(time);

    Assert.Equal(TimeSpan.Zero, clock.Normalize(TimeSpan.FromSeconds(10)));
    Assert.Equal(TimeSpan.FromMilliseconds(20), clock.Normalize(TimeSpan.FromSeconds(10.020)));
    Assert.Equal(TimeSpan.FromMilliseconds(20), clock.Normalize(TimeSpan.FromSeconds(9)));
}
```

Also assert that advancing the provider advances `Now`, and no peer/renegotiation API exists on the clock.

- [ ] **Step 2: Run focused tests and verify RED**

- [ ] **Step 3: Implement with a lock over only clock state**

```csharp
public sealed class MediaSessionClock(TimeProvider timeProvider)
{
    private readonly object _gate = new();
    private readonly long _started = timeProvider.GetTimestamp();
    private TimeSpan? _sourceOrigin;
    private TimeSpan _last;

    public TimeSpan Now => timeProvider.GetElapsedTime(_started, timeProvider.GetTimestamp());

    public TimeSpan Normalize(TimeSpan sourceTimestamp)
    {
        lock (_gate)
        {
            _sourceOrigin ??= sourceTimestamp;
            var normalized = sourceTimestamp - _sourceOrigin.Value;
            if (normalized < _last) return _last;
            _last = normalized;
            return normalized;
        }
    }
}
```

Do not share this lock with capture, encode, decode, or playback.

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

- [ ] **Step 1: Write tests for one encode per capture sample, monotonic timestamps, stop/dispose, and terminal encoder failure**

```csharp
[Fact]
public async Task One_captured_frame_is_encoded_once_and_emitted_once()
{
    var capture = new FakeCapture();
    var encoder = new FakeEncoder();
    await using var pipeline = new AudioPublishPipeline(capture, encoder, new MediaSessionClock(TimeProvider.System));
    var samples = new List<EncodedAudioSample>();
    pipeline.SampleEncoded += samples.Add;

    await pipeline.StartAsync(CancellationToken.None);
    capture.Push(Pcm20Ms(timestamp: TimeSpan.FromSeconds(4)));

    Assert.Equal(1, encoder.EncodeCalls);
    Assert.Single(samples);
}
```

Add a failing-encoder test that pushes twice and asserts the encoder is not called repeatedly after terminal failure.

- [ ] **Step 2: Run focused tests and verify RED**

- [ ] **Step 3: Implement the pipeline by mirroring `ScreenPublishPipeline` lifecycle semantics**

Normalize the outgoing sample timestamp with the session clock. Catch the encoder's terminal exception once, unsubscribe capture, set `_running=false`, and raise `Failed` once.

- [ ] **Step 4: Run focused tests and verify GREEN**

- [ ] **Step 5: Commit**

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

- [ ] **Step 1: Write tests for decode/write, decoder failure isolation, and sink failure isolation**

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

- [ ] **Step 2: Run focused tests and verify RED**

- [ ] **Step 3: Implement a bounded, non-blocking viewer pipeline**

`Push` must return quickly, must not own or dispose RTC, and must stop invoking a terminally failed decoder/sink on every packet.

- [ ] **Step 4: Run focused tests and verify GREEN**

- [ ] **Step 5: Run the entire platform-neutral media suite**

Run: `dotnet test tests/SonicDesktopRelay.Media.Tests/SonicDesktopRelay.Media.Tests.csproj`

Expected: all existing video tests plus new audio/clock tests PASS.

- [ ] **Step 6: Commit**

```bash
git add src/SonicDesktopRelay.Media/AudioWatchPipeline.cs tests/SonicDesktopRelay.Media.Tests/AudioWatchPipelineTests.cs
git commit -m "feat: add audio watch pipeline"
```
