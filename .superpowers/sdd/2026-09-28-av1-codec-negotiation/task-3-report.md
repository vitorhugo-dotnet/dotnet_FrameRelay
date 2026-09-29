# Task 3 report: Media Foundation AV1 probing and codecs

**Status: DONE_WITH_CONCERNS**

## Implemented

- Added hardware-only AV1 encoder and decoder capability enumeration through separate Media Foundation categories, AV1 subtype `31305641-0000-0010-8000-00AA00389B71` (`AV01`), activation, and concrete media-type configuration.
- The probe rejects software transforms, records category-specific empty enumeration, enables asynchronous hardware encoders before configuration, disposes temporary activations/transforms, and requires AV1 profile value `0` plus NV12 at 640x360@30.
- Added an AV1 Media Foundation encoder. It uses the existing bounded input conversion and async MFT pump, tags outputs `Codec = VideoCodec.Av1`, and carries output timestamp, duration, keyframe flag, and dimensions.
- Added an AV1 Media Foundation decoder for synchronous hardware MFTs. It accepts only samples tagged AV1, configures AV1 Main profile input, selects NV12 output, and normalizes decoded frames to the existing BGRA `VideoFrame` contract.
- Added injectable tests for absent transforms, software-only transforms, hardware acceptance, activation/configuration failure, async decoder rejection, and COM candidate disposal. Added hardware encoder sample-contract and encoder/decoder pair integration coverage. xUnit discovery metadata skips integration tests with the measured host reason.

## Host evidence and limitation

On Windows 11 build 26200 with the RTX 5060, `MFTEnumEx` found `NVIDIA AV1 Encoder MFT`. Initial configuration returned `MF_E_TRANSFORM_ASYNC_LOCKED`; enabling `MF_TRANSFORM_ASYNC_UNLOCK` allowed the candidate to configure successfully for AV1 profile 0 output and NV12 input at 640x360@30. The hardware encoder integration test produced tagged AV1 output with the expected dimensions and 30 FPS duration.

The hardware decoder category returned no AV1 decoder activation. Therefore the host integration test for the encoder/decoder pair is skipped with: `MFTEnumEx returned no hardware AV1 decoder activation.` The published capabilities contain AV1 encode but no AV1 decode, so session selection must fall back to H.264 on this host.

The current AV1 decoder deliberately rejects asynchronous MFTs because no decoder event-pump path was implemented or validated. The probe mirrors that restriction and never advertises such a decoder. Synchronous hardware decoder support remains unverified on this host. Media Foundation exposes no maximum AV1 level through the inspected type negotiation, so the probe conservatively bounds its advertised constraint to AV1 Main profile `0`, AV1 `seq_level_idx` 4 (Level 3.0), and the tested 640x360@30 configuration; it does not claim arbitrary transform capacity.

## Verification

- `dotnet build src\SonicDesktopRelay.Media.Windows\SonicDesktopRelay.Media.Windows.csproj --no-restore --verbosity:minimal` — passed, 0 warnings and 0 errors.
- `dotnet test tests\SonicDesktopRelay.Media.Windows.Tests\SonicDesktopRelay.Media.Windows.Tests.csproj --no-restore --filter "FullyQualifiedName~MediaFoundationAv1CapabilityProbeTests|FullyQualifiedName~MediaFoundationAv1CodecTests" --logger "console;verbosity=minimal"` — 6 passed, 1 skipped, 0 failed. The skipped test is the pair integration because no hardware AV1 decoder was enumerated.
- `git diff --check` — no whitespace errors in Task 3 source/tests; the shared plan file had an existing extra blank line at EOF from concurrent Task 4 progress.

## Self-review

The new AV1 paths contain no H.264 bitstream conversion or software fallback. The encoder's AV1 bytes are kept as emitted by the AV1 MFT. The decoder copies only the generic stride, output allocation, stream-change, and BGRA normalization logic from the H.264 implementation; codec-specific negotiation uses AV1 subtype/profile. The principal limitation is lack of an AV1 hardware decoder on the validation host and no support for asynchronous AV1 decoders yet.

## Review follow-up (fix round 1)

- Decoder candidate selection now configures AV1 profile 0 input and a concrete NV12 output for the requested dimensions before making the transform active. An activation or media-type/stream-start failure is recorded, disposed, and advances to the next candidate.
- Resolution changes and runtime fallback select/configure a candidate for the new size. Selection/configuration failures are surfaced to the caller rather than swallowed as packet loss or retried on the same transform.
- Input presentation time is written to the submitted `IMFSample`. Decoded frames use the output sample's Media Foundation time when available. If an MFT omits output time, a bounded FIFO maps the oldest still-pending submitted time; this fallback assumes output order for untimestamped samples. Timestamp metadata from the MFT takes precedence, including reordered outputs.
- Encoder candidate `transform.Attributes` is now disposed deterministically.
- Added tests proving a failed decoder configuration advances to and disposes candidates, and timestamp tests for reordered explicit output time, FIFO fallback, and queue clearing on reconfiguration.

### Fix-round verification

- `dotnet build src\SonicDesktopRelay.Media.Windows\SonicDesktopRelay.Media.Windows.csproj --no-restore --verbosity:minimal` — passed, 0 warnings and 0 errors.
- `dotnet test tests\SonicDesktopRelay.Media.Windows.Tests\SonicDesktopRelay.Media.Windows.Tests.csproj --no-restore --filter "FullyQualifiedName~MediaFoundationAv1" --logger "console;verbosity=minimal"` — 10 passed, 1 skipped, 0 failed. The skipped encoder/decoder pair integration still reports that this host has no hardware AV1 decoder.

## Fix round 2: asynchronous encoder timestamps

The AV1 encoder now records a bounded queue of submitted frame timestamps, durations, and dimensions. It associates delayed output with the matching `IMFSample.SampleTime`; when the transform omits that value, it uses FIFO submission order. An output carrying an unmatched explicit timestamp retains that output timestamp and uses the oldest pending frame metadata. The queue is cleared when the transform is released.

Focused tracker tests cover delayed output from frame N after frame N+1 was submitted, FIFO fallback when the MFT timestamp is unavailable, and preservation of an explicit output timestamp without an exact submission match.

- `dotnet build src\\SonicDesktopRelay.Media.Windows\\SonicDesktopRelay.Media.Windows.csproj --no-restore --verbosity:minimal` — passed, 0 warnings and 0 errors.
- `dotnet test tests\\SonicDesktopRelay.Media.Windows.Tests\\SonicDesktopRelay.Media.Windows.Tests.csproj --no-restore --filter "FullyQualifiedName~MediaFoundationEncoderTimestampTrackerTests|FullyQualifiedName~MediaFoundationAv1CapabilityProbeTests|FullyQualifiedName~MediaFoundationAv1CodecTests|FullyQualifiedName~MediaFoundationAv1DecoderTests" --logger "console;verbosity=minimal"` — 13 passed, 1 expected skip, 0 failed. The skipped hardware pair integration requires a hardware AV1 decoder, which this host does not enumerate.
