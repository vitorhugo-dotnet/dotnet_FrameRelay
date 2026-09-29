# AV1 and H.264 screen-sharing comparison

## Scope

This is a short, local Media Foundation encoder comparison on one Windows host. It compares the synchronous `IVideoEncoder.Encode()` call and the encoded samples it returned. It does not compare a WebRTC session or end-to-end screen sharing quality, delay, or network behavior.

## Host and encoder configuration

| Item | Observed value |
| --- | --- |
| OS | Windows 11 Home, version 10.0.26200, build 26200 |
| .NET SDK | 10.0.301 |
| GPU | NVIDIA GeForce RTX 5060, driver 616.92 |
| System board | Gigabyte AB350M-DS3H V2 |
| CPU | Unavailable: the host's `Win32_Processor` query returned no model value |
| AV1 transform | NVIDIA AV1 Encoder MFT, hardware; CLSID `80b80715-8c5a-420d-b346-1a9dc40a5880` |
| H.264 transform | NVIDIA H.264 Encoder MFT, hardware; CLSID `60f44560-5a20-4857-bfef-d29773cb8040` |
| AV1 profile | Main, profile 0, as configured by the encoder |
| H.264 profile | Baseline, profile 66, as configured by the encoder |
| Input and output | BGRA input frames converted by the encoder to NV12; AV1 or H.264 output |
| Target workload | 640×360, 30 fps, 1,500,000 bits/s for both encoders |

The quality and timing target were identical for AV1 and H.264. Each encoder was constructed before its measured calls; encoder construction took about 1.92 seconds for each and is reported separately from call timings.

## Workload and method

The temporary measurement fact called the existing public Media Foundation encoder APIs directly. It ran AV1 first, then H.264, once each:

1. Construct an encoder and use `VideoQuality(360, 30, 1_500_000)`.
2. Submit 5 warmup frames, measure the next 30 calls, and submit 5 additional drain frames.
3. Use timestamps `frameIndex × TimeSpan.TicksPerSecond / 30` for frame indexes 0 through 39.
4. Generate the same deterministic BGRA pattern for both encoders. For every pixel `(x,y)` at frame index `n`, set B=`(x+n) mod 256`, G=`(2y+n) mod 256`, R=`((x XOR y)+n) mod 256`, A=255. Frame generation is outside the timed `Encode()` call.
5. Select returned samples whose timestamps fall in the 30-frame measurement window (indexes 5 through 34). Calculate encoded output bitrate as `8 × sum(sample byte lengths) / sum(sample durations)`. Call-time statistics cover only the 30 measured `Encode()` calls.

The measurement source was temporary and was removed after the run. It was not added to the repository. The short, one-pass procedure and pixel formula above describe the collected comparison; this is a spot measurement, not a benchmark with repeated trials or confidence intervals.

## Results

| Metric | AV1 | H.264 |
| --- | ---: | ---: |
| Encoder | NVIDIA AV1 Encoder MFT (hardware) | NVIDIA H.264 Encoder MFT (hardware) |
| Measured `Encode()` calls | 30 | 30 |
| Mean `Encode()` call time | 30.247 ms | 29.751 ms |
| Median `Encode()` call time | 29.285 ms | 29.198 ms |
| P95 `Encode()` call time | 40.398 ms | 30.207 ms |
| Maximum `Encode()` call time | 44.008 ms | 45.503 ms |
| Returned samples in measured timestamp window | 30 | 30 |
| Encoded bytes in measured timestamp window | 191,537 | 204,252 |
| Sum of encoded sample durations | 0.999999 s | 0.999999 s |
| Encoded output bitrate from those samples | 1,532,298 bits/s | 1,634,018 bits/s |
| Null outputs across warmup, measurement, and drain calls | 0 | 0 |
| Submitted timestamps without a returned measured sample | 0 | 0 |

The last row counts only returned encoder samples matched to submitted timestamps within this short run. It is not a capture, queue, RTP, or network drop metric. Encoded output bitrate is based on the returned elementary-stream sample bytes and durations; it is not the configured target and does not include RTP, transport, or container overhead.

## What this does not measure

- The synthetic gradient is not a representative desktop recording, and the result is from one pass in a fixed AV1-then-H.264 order.
- `Encode()` call time includes conversion and synchronous encoder work; it is not encode-only MFT time or end-to-end latency. Transform construction is excluded from the call-time statistics.
- AV1 decode performance is unavailable: this host has a hardware AV1 encoder but no usable hardware AV1 decoder MFT. Decode duration and decoder drops are therefore unavailable.
- Capture cadence, frame queues, RTP packetization, transport, receiver latency, and visual quality were not measured.
- CPU model and background system load were unavailable, so the results should not be generalized to other machines or workloads.

## Focused regression checks

Commands were run sequentially on the same host:

```powershell
dotnet test tests/SonicDesktopRelay.Media.Windows.Tests/SonicDesktopRelay.Media.Windows.Tests.csproj --no-restore --filter FullyQualifiedName~MediaFoundationAv1CodecTests --logger "console;verbosity=minimal"
```

Result: 1 passed, 1 skipped. The hardware AV1 encoder sample test passed. The AV1 encoder/decoder pair test skipped because no usable hardware AV1 decoder was found.

```powershell
dotnet test tests/SonicDesktopRelay.Media.Windows.Tests/SonicDesktopRelay.Media.Windows.Tests.csproj --no-build --filter FullyQualifiedName~MediaFoundationH264EncoderTests --logger "console;verbosity=minimal"
```

Result: 12 passed, 0 skipped.

```powershell
dotnet test tests/SonicDesktopRelay.Rtc.Tests/SonicDesktopRelay.Rtc.Tests.csproj --no-restore --filter "FullyQualifiedName~VideoPublisherTests|FullyQualifiedName~SipSorceryPeerConnectionTests|FullyQualifiedName~Av1RtpAccessUnitAssemblerTests|FullyQualifiedName~H264RtpIntegrityInvestigationTests" --logger "console;verbosity=minimal"
```

Result: 53 passed, 0 skipped. This covered the focused publisher negotiation, peer offer/answer, AV1 RTP assembly, and H.264 RTP integrity tests.
