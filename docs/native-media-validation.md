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
- audio/video publishing share the same `MediaSessionClock`.

The CI job also rejects tracked legacy codec files/references outside historical
`docs/superpowers/plans` and `docs/superpowers/specs`.


## Decoder output-sample ownership

`MediaFoundationH264Decoder` follows the allocation model returned by
`IMFTransform::GetOutputStreamInfo`:

- `MFT_OUTPUT_STREAM_PROVIDES_SAMPLES`: the MFT owns allocation and FrameRelay passes no sample.
- `MFT_OUTPUT_STREAM_CAN_PROVIDE_SAMPLES`: FrameRelay deliberately supplies its reusable
  caller-allocated output sample.
- neither flag: caller allocation is required.

With the Vortice/SharpGen marshalling used by this project, the `pSample` interface field copied
back from `ProcessOutput` can be represented by a different managed wrapper around the same
native COM pointer that FrameRelay supplied. SharpGen constructs that wrapper from the returned
pointer without adding another COM reference. The old cleanup used managed `ReferenceEquals`
to decide whether both wrappers should be disposed, which could release the same native
`IMFSample` reference twice.

Cleanup is now driven by the selected allocation mode instead of wrapper identity. Caller-owned
output disposes the caller-created sample exactly once; MFT-owned output disposes the returned
sample exactly once; `output.Events` is disposed independently. Conversion completes before
the owned sample is released.

Decoder diagnostics record the output-stream flags and allocation mode at Debug level and sample
`ProcessOutput` results at Trace level, including HRESULT, whether the caller supplied a sample,
whether a sample was returned, and whether a stream change occurred.

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
5. Join from a second client and verify video decodes/renders and audio plays.
6. At 1920x1080, keep continuous high-motion content running for at least 3 minutes and beyond
   1,000 decoded frames. The previous lifetime failure appeared around 46 seconds / 396 decoded
   frames, so a short smoke test is not sufficient.
7. Verify received access-unit and decoded-frame counters continue increasing, no
   `SEHException` is logged, and watch media never transitions from `Receiving` to `Failed`.
8. Verify the log identifies the Media Foundation output ownership mode and contains no repeated
   native lifetime warnings.
9. Leave the screen static, then resume activity; verify the viewer remains current rather than
   showing a permanently stale frame.
10. Trigger or simulate packet loss and verify recovery requests a keyframe without restarting
    the session.
11. Change effective quality/resolution and verify the viewer continues after the keyframe-bound
    reconfiguration.
12. Stop and restart sharing/watching to verify native transforms and WASAPI devices are released
    cleanly.
13. Verify diagnostics contain no SDP body, ICE candidate body, credential or media payload.

## Network validation

Use a pair of machines/networks when possible:

- direct/STUN path succeeds when reachable;
- TURN is used only when direct connectivity cannot be established;
- audio and video remain on the same peer connection;
- adding viewers does not create additional encoders.

Record the selected transform names and whether each side used hardware or software acceleration
with the release test notes.
