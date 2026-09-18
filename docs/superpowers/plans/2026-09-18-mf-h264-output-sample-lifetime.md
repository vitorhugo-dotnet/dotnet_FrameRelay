# Media Foundation H.264 Output Sample Lifetime Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stop long-running viewers from entering a terminal Media Foundation decoder failure by making `ProcessOutput` sample ownership explicit and releasing each native output reference exactly once.

**Architecture:** Keep `MediaFoundationH264Decoder` as the native boundary and extract only a small allocation/cleanup policy seam that can be tested without mocking COM. Select the output allocation mode from `MFT_OUTPUT_STREAM_INFO` flags, use the caller-owned sample directly when FrameRelay supplied it, treat the Vortice-returned wrapper as a non-owning alias in that path, and dispose MFT-provided output only on the MFT-owned path. `output.Events` remains an independent lifetime.

**Tech Stack:** .NET 10, C#, Windows Media Foundation, Vortice.MediaFoundation 3.8.3 / SharpGen.Runtime, xUnit, Microsoft.Extensions.Logging.

**Spec:** User-provided SYSTEMATICDEBUGGING request for the viewer freeze reproduced around 401 access units / 396 decoded frames.

## Global Constraints

- Do not change signaling, WebRTC negotiation, TURN/STUN, capture, RTP routing, or publisher behavior.
- Do not replace Media Foundation, restore the legacy codec stack, rename legacy namespaces/projects/storage paths, or swallow `SEHException`.
- Follow Microsoft `IMFTransform::ProcessOutput` allocation rules for `PROVIDES_SAMPLES`, `CAN_PROVIDE_SAMPLES`, and neither flag.
- Treat managed wrapper identity as distinct from COM/native identity.
- Keep diagnostics at Debug/Trace except existing state/error transitions.
- Use TDD: add ownership regression tests before production changes.
- Verify the full solution and inspect the final diff before opening the final review PR; because repository CI only runs on `pull_request`/main and the execution host has no .NET SDK, use a draft PR solely to obtain the required Windows CI evidence, then mark ready only after green.

---

### Task 1: Lock down allocation and cleanup semantics

**Files:**
- Create: `src/SonicDesktopRelay.Media.Windows/MediaFoundationOutputSampleLifetime.cs`
- Create: `tests/SonicDesktopRelay.Media.Windows.Tests/MediaFoundationOutputSampleLifetimeTests.cs`

**Interfaces:**
- Produces: `OutputSampleAllocationMode`, `OutputSampleCleanupPlan`, and `MediaFoundationOutputSampleLifetime` helpers used by the decoder.

- [ ] **Step 1: Write failing allocation/cleanup tests**

Cover:
- `PROVIDES_SAMPLES` -> MFT-provided output.
- `CAN_PROVIDE_SAMPLES` -> caller-optional output, with FrameRelay choosing to supply.
- Neither flag -> caller-required output.
- Caller-owned paths schedule one caller disposal and zero returned-wrapper disposals even when a different managed wrapper is returned.
- MFT-owned path schedules one returned-sample disposal and zero caller disposals.
- Event cleanup remains independent.
- Need-more-input and stream-change reuse the same ownership cleanup plan without leaks/double-dispose.
- Successful output cleanup is explicitly deferred until after conversion.

- [ ] **Step 2: Verify RED**

Run on Windows:
```powershell
dotnet test tests/SonicDesktopRelay.Media.Windows.Tests/SonicDesktopRelay.Media.Windows.Tests.csproj --configuration Release
```

Expected before implementation: compile/test failure because the ownership policy types do not exist.

- [ ] **Step 3: Implement the minimal policy**

Use an explicit enum:
```csharp
internal enum OutputSampleAllocationMode
{
    MftProvided,
    CallerOptional,
    CallerRequired
}
```

The cleanup plan must identify the single owned sample reference and whether a returned Vortice wrapper must merely be detached after a caller-owned sample is disposed. It must not infer native ownership from `ReferenceEquals`.

- [ ] **Step 4: Verify GREEN**

Run the focused test project and confirm all ownership tests pass.

### Task 2: Apply explicit lifetime ownership in DrainOutput

**Files:**
- Modify: `src/SonicDesktopRelay.Media.Windows/MediaFoundationH264Decoder.cs`
- Test: `tests/SonicDesktopRelay.Media.Windows.Tests/MediaFoundationH264DecoderTests.cs`

**Interfaces:**
- Consumes: allocation mode and cleanup plan from Task 1.
- Produces: native decoder behavior that conforms to Media Foundation output allocation rules.

- [ ] **Step 1: Select allocation mode from both output flags**

If `PROVIDES_SAMPLES` is present, pass `pSample = NULL`. If only `CAN_PROVIDE_SAMPLES` is present, FrameRelay continues to supply a correctly-sized sample. If neither is present, FrameRelay must supply a sample.

- [ ] **Step 2: Keep caller-owned and MFT-owned samples separate**

When FrameRelay supplies the sample, convert from that caller-owned sample and dispose it exactly once. The Vortice wrapper reconstructed into `output.Sample` is not a second COM reference and must not be independently disposed; neutralize the wrapper pointer after the caller-owned release. When the MFT supplies the sample, convert from and dispose `output.Sample` exactly once.

- [ ] **Step 3: Preserve event and conversion ordering**

Dispose `output.Events` independently. Do not release the output sample until `ConvertOutput` (including `ConvertToContiguousBuffer`) has completed. Apply the same cleanup path for success, `MF_E_TRANSFORM_NEED_MORE_INPUT`, stream change, and failure returns.

- [ ] **Step 4: Add focused diagnostics**

At Debug/Trace log output flags, `PROVIDES_SAMPLES`, `CAN_PROVIDE_SAMPLES`, selected allocation mode, whether the caller supplied a sample, whether `ProcessOutput` returned a sample, HRESULT/status, native pointer alias evidence when useful, and cleanup ownership path. Do not add per-frame Information noise.

- [ ] **Step 5: Run focused native decoder tests**

```powershell
dotnet test tests/SonicDesktopRelay.Media.Windows.Tests/SonicDesktopRelay.Media.Windows.Tests.csproj --configuration Release
```

### Task 3: Document, verify, and review

**Files:**
- Modify: `docs/native-media-validation.md`
- Modify only if useful: `README.md`

- [ ] **Step 1: Document the proven root cause and diagnostic fields**

Document that SharpGen reconstructs COM interface fields from the native pointer into a new managed wrapper without `AddRef`, while `ComObject.Dispose` releases the pointer. Explain why `ReferenceEquals` was invalid for ownership and how the explicit mode prevents duplicate release.

- [ ] **Step 2: Run full verification**

```powershell
dotnet restore SonicDesktopRelay.sln
dotnet build SonicDesktopRelay.sln --configuration Release --no-restore
dotnet test SonicDesktopRelay.sln --configuration Release --no-build --no-restore
```

No new warnings are allowed.

- [ ] **Step 3: Inspect the full diff**

Confirm only the decoder ownership fix, regression tests, focused diagnostics, plan, and relevant docs changed.

- [ ] **Step 4: Request code review**

Review specifically for COM reference ownership, Media Foundation flag semantics, cleanup on non-success HRESULTs, wrapper detachment safety, conversion-before-dispose ordering, and accidental publisher/signaling changes.

- [ ] **Step 5: Runtime verification**

If a Windows two-client environment is available, stream 1920x1080 with continuous movement for well beyond the prior ~46 second / 401-access-unit failure point and record duration/frame count. If unavailable, state that limitation explicitly in the PR.

- [ ] **Step 6: Open PR against main without merging**

Use title `fix: correct Media Foundation H.264 output sample lifetime`. Because GitHub Actions is configured to run Windows CI on pull requests rather than feature-branch pushes, create the PR as draft to trigger CI, address real findings, and mark ready only after required CI is green.
