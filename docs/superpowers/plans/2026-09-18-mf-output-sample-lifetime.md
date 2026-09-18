# Media Foundation H.264 Output Sample Lifetime Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stop the Windows viewer from entering a terminal Media Foundation decoder failure by making H.264 output-sample ownership explicit and disposing each native COM reference exactly once.

**Architecture:** Keep the existing decoder and Media Foundation pipeline. Extract only a small internal ownership policy/cleanup seam that distinguishes caller-allocated output from MFT-allocated output, then make `DrainOutput` follow that policy instead of comparing managed wrapper identity. Preserve existing decode-null recovery and signaling/publisher behavior.

**Tech Stack:** .NET 10, C#, Vortice.MediaFoundation 3.8.3, SharpGen.Runtime, xUnit, Microsoft.Extensions.Logging.

**Spec:** User-provided systematic-debugging task for the 2026-09-18 viewer freeze.

## Global Constraints

- Do not change signaling, WebRTC negotiation, TURN/STUN, publisher encoding, or restore the legacy codec stack.
- Do not swallow `SEHException` as a recovery mechanism.
- Do not rename legacy `SonicDesktopRelay.*` projects/namespaces/storage paths.
- Follow Media Foundation `MFT_OUTPUT_STREAM_PROVIDES_SAMPLES` / `MFT_OUTPUT_STREAM_CAN_PROVIDE_SAMPLES` allocation rules.
- Do not use managed `ReferenceEquals` as COM identity.
- Keep diagnostics at Debug/Trace except existing warnings/errors.
- Preserve existing `decode-null` recovery unless evidence proves it shares this root cause.
- Run build/test verification and inspect the final diff before opening the PR.
- Do not merge the PR.

---

### Task 1: Prove the ownership model and encode it as tests

**Files:**
- Modify: `tests/SonicDesktopRelay.Media.Windows.Tests/MediaFoundationH264DecoderTests.cs`
- Create: `src/SonicDesktopRelay.Media.Windows/MediaFoundationOutputSampleLifetime.cs` only after RED is established.

**Interfaces:**
- Produces: `OutputSampleAllocationMode`, `MediaFoundationOutputSampleLifetime.ResolveAllocationMode(...)`, `SelectSampleForConversion(...)`, and `Dispose(...)`.

- [ ] **Step 1: Add failing regression tests**

Add tests for:
- caller-allocated sample disposed exactly once;
- MFT-allocated sample disposed exactly once;
- distinct managed wrappers for one logical caller-owned sample do not double-dispose the native resource;
- `PROVIDES_SAMPLES` resolves to MFT allocation;
- `CAN_PROVIDE_SAMPLES` resolves according to whether the caller supplied a sample;
- no allocation flags require caller allocation;
- events are disposed independently;
- NeedMoreInput and StreamChange cleanup paths do not leak/double-dispose;
- conversion selects the caller wrapper while caller-owned and the returned wrapper while MFT-owned.

- [ ] **Step 2: Run focused tests and verify RED**

Run:

```powershell
dotnet test tests/SonicDesktopRelay.Media.Windows.Tests/SonicDesktopRelay.Media.Windows.Tests.csproj -c Release --filter "FullyQualifiedName~MediaFoundationH264DecoderTests"
```

Expected: FAIL because the ownership API does not exist yet.

- [ ] **Step 3: Implement the minimal ownership seam**

Create an internal helper with two explicit modes:

```csharp
internal enum OutputSampleAllocationMode
{
    CallerAllocated,
    MftAllocated
}
```

Resolve the mode from stream flags plus whether FrameRelay supplies a sample. Reject invalid combinations instead of guessing.

Cleanup must be ownership-driven:
- caller-owned: dispose only the caller-created sample;
- MFT-owned: dispose only the sample returned by `ProcessOutput`;
- events: always independent.

- [ ] **Step 4: Run focused tests and verify GREEN**

Run the same filtered test command. Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add tests/SonicDesktopRelay.Media.Windows.Tests/MediaFoundationH264DecoderTests.cs src/SonicDesktopRelay.Media.Windows/MediaFoundationOutputSampleLifetime.cs
git commit -m "test: cover Media Foundation output sample ownership"
```

### Task 2: Apply explicit ownership in DrainOutput

**Files:**
- Modify: `src/SonicDesktopRelay.Media.Windows/MediaFoundationH264Decoder.cs`

**Interfaces:**
- Consumes the ownership seam from Task 1.
- Preserves `Decode`, `DrainOutput`, and `ConvertOutput` public behavior.

- [ ] **Step 1: Select allocation from output stream flags**

For each `DrainOutput` attempt:
- read `GetOutputStreamInfo(0)`;
- detect `PROVIDES_SAMPLES` and `CAN_PROVIDE_SAMPLES`;
- choose whether FrameRelay supplies a sample;
- resolve the explicit ownership mode.

Policy for this decoder:
- `PROVIDES_SAMPLES`: do not supply a sample;
- otherwise, including `CAN_PROVIDE_SAMPLES`, keep the current caller-buffer policy and supply one.

- [ ] **Step 2: Remove wrapper identity based cleanup**

Delete `ReferenceEquals(output.Sample, allocated)`.

When caller-owned, convert using the caller-owned sample and dispose it exactly once. Treat `output.Sample` as a marshalling alias, not a second owned COM reference.

When MFT-owned, convert using and dispose the returned sample exactly once.

Dispose `output.Events` independently in all cases.

- [ ] **Step 3: Keep conversion lifetime correct**

Call `ConvertOutput` before ownership cleanup so `ConvertToContiguousBuffer` and the contiguous buffer lock complete while the selected sample is still alive.

- [ ] **Step 4: Run focused tests**

Run the Windows media tests. Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/SonicDesktopRelay.Media.Windows/MediaFoundationH264Decoder.cs
git commit -m "fix: correct Media Foundation H264 output sample lifetime"
```

### Task 3: Add focused diagnostics and documentation

**Files:**
- Modify: `src/SonicDesktopRelay.Media.Windows/MediaFoundationH264Decoder.cs`
- Modify: `docs/native-media-validation.md`
- Modify: `README.md`

**Interfaces:**
- Diagnostics must expose the chosen allocation model without per-frame Information noise.

- [ ] **Step 1: Log allocation selection when it changes**

At Debug level record:
- raw output stream flags;
- providesSamples;
- canProvideSamples;
- allocation mode;
- callerSuppliedSample.

- [ ] **Step 2: Log ProcessOutput outcomes selectively**

At Trace level record the first call and non-success/stream-change outcomes with:
- HRESULT;
- whether a caller sample was supplied;
- whether a sample was returned;
- allocation mode.

Do not add per-frame Information logs.

- [ ] **Step 3: Document the proven root cause and validation**

Document that SharpGen native-to-managed interface-field marshalling creates a managed wrapper from the returned native pointer without adding a COM reference; therefore a caller-provided sample can be represented by a second managed wrapper after `ProcessOutput`. The previous `ReferenceEquals` cleanup could then release the same COM reference twice.

Document the long-run reproduction gate (> previous ~46s / 396 decoded frames), and keep decode-null as a separate follow-up unless it reproduces from the same lifetime issue.

- [ ] **Step 4: Commit**

```bash
git add src/SonicDesktopRelay.Media.Windows/MediaFoundationH264Decoder.cs docs/native-media-validation.md README.md
git commit -m "docs: record decoder output ownership diagnostics"
```

### Task 4: Verification and review

**Files:** no intended production changes unless verification finds a real defect.

- [ ] **Step 1: Run full verification**

```powershell
dotnet restore SonicDesktopRelay.sln
dotnet build SonicDesktopRelay.sln --configuration Release --no-restore
dotnet test SonicDesktopRelay.sln --configuration Release --no-build --no-restore
```

No new warnings attributable to this change.

- [ ] **Step 2: Inspect the full diff**

Confirm only decoder ownership, tests, diagnostics, plan, and relevant docs changed.

- [ ] **Step 3: Request code review**

Review specifically for:
- COM reference-count ownership;
- CAN_PROVIDE behavior;
- cleanup on NeedMoreInput / StreamChange / failure;
- conversion before disposal;
- no exception swallowing or unrelated media/signaling changes.

- [ ] **Step 4: Manual runtime verification if available**

On Windows:
1. publish at 1920x1080;
2. join from another client;
3. reach WebRTC connected and media Receiving;
4. keep high-motion content running beyond the previous ~46 second failure point by a large margin;
5. verify access-unit and decoded-frame counters continue increasing;
6. verify no `SEHException`, no `Receiving -> Failed`, and no native lifetime warnings.

If this environment cannot run the desktop client, state that limitation in the PR.

- [ ] **Step 5: Open PR against main**

Title:

`fix: correct Media Foundation H.264 output sample lifetime`

The PR body must include Problem, Root cause, Fix, Tests, Runtime verification, Evidence, and Out of scope sections.
