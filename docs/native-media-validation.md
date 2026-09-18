# Native media validation

This checklist is the release gate for the Windows-native media pipeline.

## Automated parity

Run on a Windows 10/11 compatible machine:

```powershell
dotnet restore SonicDesktopRelay.sln
dotnet build SonicDesktopRelay.sln --configuration Release --no-restore
dotnet test SonicDesktopRelay.sln --configuration Release --no-build --no-restore
```

The Windows media integration suite must exercise, not merely discover:

- H.264 encoder selection and a non-empty selected transform name;
- first access unit is a keyframe;
- SPS/PPS are present for a fresh decoder;
- quality scaling changes encoded geometry correctly;
- requested recovery keyframe is emitted;
- resolution change reconfigures and emits a keyframe;
- encoder diagnostics project the live transform and rejection log;
- publisher-encoded H.264 decodes to the original frame size;
- decoder survives corrupt input;
- decoder handles a mid-stream resolution change;
- decoder reuses its BGRA conversion buffer;
- decoder diagnostics project the live transform and rejection log;
- decoder output-sample ownership tests cover caller-required, caller-optional, and MFT-provided allocation paths without duplicate sample disposal;
- sustained decode runs beyond the previous 401-access-unit / 396-frame terminal failure point;
- audio/video publishing share the same `MediaSessionClock`.

The CI job also rejects tracked legacy codec files/references outside historical
`docs/superpowers/plans` and `docs/superpowers/specs`.

## Decoder output sample ownership

`IMFTransform::ProcessOutput` has three output allocation cases:

- `MFT_OUTPUT_STREAM_PROVIDES_SAMPLES`: pass `pSample = NULL`; the MFT supplies the sample.
- `MFT_OUTPUT_STREAM_CAN_PROVIDE_SAMPLES`: either side may supply it. FrameRelay deliberately
  supplies a correctly-sized sample to preserve the existing decode path.
- neither flag: FrameRelay must allocate and supply the output sample.

Vortice.MediaFoundation 3.8.3 is generated with SharpGenTools 2.4.2-beta. For interface
fields inside marshalled structs, SharpGen's native-to-managed step constructs a new managed
wrapper around the returned native pointer without adding a COM reference. Therefore a
caller-supplied `IMFSample` and `OutputDataBuffer.Sample` can be different managed objects
while representing the same single caller-owned COM reference.

The decoder must never infer native ownership from managed `ReferenceEquals`. Caller-owned
paths release the original caller sample exactly once and neutralize the returned non-owning
wrapper. MFT-owned paths release the returned sample exactly once. `output.Events` is an
independent COM reference and is always released independently when present.

A useful reproduction log should show the selected `streamFlags`, `providesSamples`,
`canProvideSamples`, `allocationMode`, whether the caller supplied a sample, the
`ProcessOutput` HRESULT/status, whether a sample was returned, native-pointer alias evidence,
and the final cleanup path. Per-output detail is Trace-level; allocation selection is Debug-level.

References:

- https://learn.microsoft.com/windows/win32/api/mftransform/nf-mftransform-imftransform-processoutput
- https://learn.microsoft.com/windows/win32/api/mftransform/ns-mftransform-mft_output_data_buffer
- https://learn.microsoft.com/windows/win32/api/mftransform/ns-mftransform-mft_output_stream_info
- https://github.com/SharpGenTools/SharpGenTools/commit/6990bcafe124a4c22515ad19cee5a081da8db67b

## Publish-output inspection

Build both release shapes and inspect them:

```powershell
dotnet publish src/SonicDesktopRelay.App/SonicDesktopRelay.App.csproj --configuration Release --runtime win-x64 --self-contained true -p:PublishSingleFile=false -o artifacts/publish/portable

dotnet publish src/SonicDesktopRelay.App/SonicDesktopRelay.App.csproj --configuration Release --runtime win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o artifacts/publish/single-file
```

Neither output may contain legacy codec artifacts such as `avcodec-*`, `avutil-*`,
`swscale-*`, `swresample-*`, or the removed managed binding assembly.

## Manual desktop validation

On a supported Windows desktop:

1. Start sharing a monitor and verify the preview/session starts without a separate codec
   installation.
2. In Diagnostics, verify capture is Windows.Graphics.Capture and video reports a selected
   Media Foundation H.264 transform.
3. Verify Diagnostics identifies hardware or software selection and shows candidate rejection
   reasons when fallback occurs.
4. Play system audio and verify WASAPI loopback + Opus are active without affecting video.
5. Join from a second client and verify video decodes/renders and audio plays. At 1920x1080, keep continuous screen movement/video playback running for at least two minutes (well beyond the previous ~46 second failure) and verify decoded-frame counters continue increasing without `SEHException` or a `Receiving -> Failed` transition.
6. Leave the screen static, then resume activity; verify the viewer remains current rather than
   showing a permanently stale frame.
7. Trigger or simulate packet loss and verify recovery requests a keyframe without restarting the
   session.
8. Change effective quality/resolution and verify the viewer continues after the keyframe-bound
   reconfiguration.
9. Stop and restart sharing/watching to verify native transforms and WASAPI devices are released
   cleanly.
10. Verify diagnostics contain no SDP body, ICE candidate body, credential or media payload.

## Network validation

Use a pair of machines/networks when possible:

- direct/STUN path succeeds when reachable;
- TURN is used only when direct connectivity cannot be established;
- audio and video remain on the same peer connection;
- adding viewers does not create additional encoders.

Record the selected transform names and whether each side used hardware or software acceleration
with the release test notes.
