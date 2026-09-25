# Viewer TURN Fallback Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Recover a viewer whose initial direct WebRTC connection fails by renegotiating that viewer's connection through TURN once.

**Architecture:** Keep direct ICE as the first attempt. When the viewer's peer connection reaches terminal `failed` before it has ever reached `connected`, and the selected path is `Direct`, send a routed `webrtc.renegotiate` request. Replace only that viewer's peer connection on both endpoints with relay-only connections; tag every offer, answer, and ICE candidate with a `negotiationId` so stale messages cannot cross generations.

**Tech Stack:** .NET 10, C#, SIPSorcery 10.0.16, existing `TimeProvider`/`FakeTimeProvider` test support, xUnit.

**Spec:** `docs/superpowers/specs/2026-09-24-viewer-turn-fallback-design.md`

## Global Constraints

- A direct connection that works must continue without TURN.
- Start at most one relay fallback per participant and session.
- Wait no more than 15 seconds for a relay offer and 30 seconds after applying it for WebRTC `connected`.
- Use the existing `webrtc.renegotiate` routed message; do not change RelayControl or its HTTP contract.
- Do not log SDP, ICE candidates, TURN credentials, addresses, ports, or media bytes.
- Keep capture, encoding, session state, and other viewers active while replacing one viewer's peers.

## Review Focus

- A direct connection that already reached `connected` later fails: it must not start this pre-connect fallback.
- A connection fails before a nominated transport is known: it must retain existing failure behavior and avoid guessing that it was direct.
- An old offer, answer, or ICE candidate arrives after fallback starts: it must not be applied to the relay peer.
- A malformed or duplicate renegotiation request arrives: it must not replace a peer more than once or let an unrelated participant trigger replacement.
- The viewer leaves or the session is disposed during either timeout: all peer connections, candidate queues, send queues, and timers must be cleaned up.

---

### Task 1: Add relay-policy peer factories and viewer connection-state events

**Files:**
- Modify: `src/SonicDesktopRelay.Rtc/IPeerConnection.cs`
- Modify: `src/SonicDesktopRelay.Rtc/IViewerPeerConnection.cs`
- Modify: `src/SonicDesktopRelay.Rtc/SipSorceryPeerConnection.cs`
- Modify: `src/SonicDesktopRelay.Rtc/SipSorceryViewerPeerConnection.cs`
- Modify: fake factory/peer implementations in `tests/SonicDesktopRelay.Rtc.Tests/VideoPublisherTests.cs`, `tests/SonicDesktopRelay.Rtc.Tests/VideoSubscriberTests.cs`, `tests/SonicDesktopRelay.Rtc.Tests/VideoPublisherPacketLossTests.cs`, and `tests/SonicDesktopRelay.Rtc.Tests/ViewerPeerAudioContractTests.cs`
- Test: `tests/SonicDesktopRelay.Rtc.Tests/SipSorceryPeerConnectionTests.cs`
- Test: `tests/SonicDesktopRelay.Rtc.Tests/SipSorceryViewerPeerConnectionTests.cs`

**Interfaces:**
- Change `IPeerConnectionFactory.Create(Guid participantId)` to `Create(Guid participantId, bool? forceRelay)`.
- Change `IViewerPeerConnectionFactory.Create()` to `Create(bool? forceRelay)`.
- A null policy preserves the injected ICE settings; fallback requests explicitly pass `true`.
- Add `event Action<bool>? ConnectionStateChanged` to `IViewerPeerConnection`; `true` means the peer reached `connected`, `false` means its terminal state is `failed`. Do not publish transient `disconnected` as terminal failure.

- [ ] **Step 1: Add the failing factory-policy tests**

Add this test to `SipSorceryViewerPeerConnectionTests`; add the equivalent offer test to `SipSorceryPeerConnectionTests`:

```csharp
[Fact]
public async Task Relay_only_viewer_does_not_gather_host_candidates()
{
    var factory = new SipSorceryViewerPeerConnectionFactory(Ice);
    await using var peer = factory.Create(forceRelay: true);
    var candidates = new List<string>();
    peer.IceCandidateGathered += (candidate, _, _) => candidates.Add(candidate);

    await peer.CreateAnswerAsync(PublisherOfferSdp, CancellationToken.None);

    Assert.DoesNotContain(candidates, candidate => candidate.Contains(" typ host", StringComparison.Ordinal));
}
```

- [ ] **Step 2: Run the targeted tests and confirm they fail to compile**

Run: `dotnet test tests/SonicDesktopRelay.Rtc.Tests/SonicDesktopRelay.Rtc.Tests.csproj --filter "FullyQualifiedName~Relay_only_viewer_does_not_gather_host_candidates"`

Expected: FAIL because the viewer factory has no `forceRelay` argument yet.

- [ ] **Step 3: Add explicit relay-policy factory overrides and the terminal-state event**

Implement both SIPSorcery factories by copying each factory's injected `IceServerSettings` with the requested policy:

```csharp
public IPeerConnection Create(Guid participantId, bool? forceRelay = null) =>
    new SipSorceryPeerConnection(participantId,
        forceRelay is { } relay ? ice with { ForceRelay = relay } : ice);
```

Wire `SipSorceryViewerPeerConnection.onconnectionstatechange` to raise `ConnectionStateChanged(true)` only for `connected` and `ConnectionStateChanged(false)` only for `failed`. Update all fake implementations with explicit `Create(..., bool forceRelay)` and event accessors.

- [ ] **Step 4: Re-run peer-connection tests**

Run: `dotnet test tests/SonicDesktopRelay.Rtc.Tests/SonicDesktopRelay.Rtc.Tests.csproj --filter "FullyQualifiedName~SipSorceryPeerConnectionTests|FullyQualifiedName~SipSorceryViewerPeerConnectionTests"`

Expected: PASS; relay-only factories gather no host candidates, and existing offer/answer behavior stays intact.

- [ ] **Step 5: Commit the factory and event contract**

```powershell
git add src/SonicDesktopRelay.Rtc/IPeerConnection.cs src/SonicDesktopRelay.Rtc/IViewerPeerConnection.cs src/SonicDesktopRelay.Rtc/SipSorceryPeerConnection.cs src/SonicDesktopRelay.Rtc/SipSorceryViewerPeerConnection.cs tests/SonicDesktopRelay.Rtc.Tests/SipSorceryPeerConnectionTests.cs tests/SonicDesktopRelay.Rtc.Tests/SipSorceryViewerPeerConnectionTests.cs tests/SonicDesktopRelay.Rtc.Tests/VideoPublisherTests.cs tests/SonicDesktopRelay.Rtc.Tests/VideoSubscriberTests.cs tests/SonicDesktopRelay.Rtc.Tests/VideoPublisherPacketLossTests.cs tests/SonicDesktopRelay.Rtc.Tests/ViewerPeerAudioContractTests.cs
git commit -m "feat(rtc): support relay-only peer connection factories"
```

### Task 2: Tag every negotiation generation

**Files:**
- Modify: `src/SonicDesktopRelay.Rtc/VideoPublisher.cs`
- Modify: `src/SonicDesktopRelay.Rtc/VideoSubscriber.cs`
- Test: `tests/SonicDesktopRelay.Rtc.Tests/VideoPublisherTests.cs`
- Test: `tests/SonicDesktopRelay.Rtc.Tests/VideoSubscriberTests.cs`
- Modify: `src/SonicDesktopRelay.Media/ScreenWatchPipeline.cs`

**Interfaces:**
- Publisher state stores one active `Guid negotiationId` per viewer.
- Initial publisher offers and publisher ICE candidates carry that ID in their payload.
- Viewer answers and viewer ICE candidates echo the active offer's ID.
- A legacy initial offer/candidate without an ID remains accepted only before any fallback generation is active.

- [ ] **Step 1: Add failing tests for offer, answer, candidate generation tags**

In `VideoPublisherTests`, assert the first offer includes a parseable `negotiationId` and the initial candidate uses the same value. In `VideoSubscriberTests`, use a publisher offer with an ID and assert the outgoing answer and gathered viewer candidate echo it. Add a test that an offer without an ID still answers on a fresh subscriber.

```csharp
Assert.True(Guid.TryParse(
    sentOffer.Payload.GetProperty("negotiationId").GetString(), out var generation));
Assert.Equal(generation.ToString(), sentCandidate.Payload.GetProperty("negotiationId").GetString());
```

- [ ] **Step 2: Run the two targeted test files and confirm the new assertions fail**

Run: `dotnet test tests/SonicDesktopRelay.Rtc.Tests/SonicDesktopRelay.Rtc.Tests.csproj --filter "FullyQualifiedName~VideoPublisherTests|FullyQualifiedName~VideoSubscriberTests"`

Expected: FAIL because no negotiation ID is carried in signaling payloads.

- [ ] **Step 3: Generate and propagate one ID per initial peer negotiation**

Create the ID before attaching publisher callbacks. Capture it in each peer's candidate callback, and include it in offer/candidate payloads. On the viewer, parse the ID with the offer, store it with the remote-description generation, and include it in answer/candidate payloads. Preserve null-ID handling for a legacy initial offer.

```csharp
var negotiationId = Guid.NewGuid();
await signaling.SendAsync(SignalingMessageTypes.WebRtcOffer, participantId,
    new { type = "offer", sdp = offer, negotiationId }, ct);
```

- [ ] **Step 4: Re-run publisher and subscriber tests**

Run: `dotnet test tests/SonicDesktopRelay.Rtc.Tests/SonicDesktopRelay.Rtc.Tests.csproj --filter "FullyQualifiedName~VideoPublisherTests|FullyQualifiedName~VideoSubscriberTests"`

Expected: PASS; offer, answer, and candidates use one generation, and legacy initial offers still work.

- [ ] **Step 5: Commit the generation envelope change**

```powershell
git add src/SonicDesktopRelay.Rtc/VideoPublisher.cs src/SonicDesktopRelay.Rtc/VideoSubscriber.cs tests/SonicDesktopRelay.Rtc.Tests/VideoPublisherTests.cs tests/SonicDesktopRelay.Rtc.Tests/VideoSubscriberTests.cs
git commit -m "feat(rtc): correlate peer signaling by negotiation generation"
```

### Task 3: Replace only the requesting publisher peer with a relay peer

**Files:**
- Modify: `src/SonicDesktopRelay.Rtc/VideoPublisher.cs`
- Modify: `src/SonicDesktopRelay.Rtc/VideoSampleSendQueue.cs` only if queue replacement needs an atomic stop/drain hook
- Test: `tests/SonicDesktopRelay.Rtc.Tests/VideoPublisherTests.cs`

**Interfaces:**
- `VideoPublisher.HandleAsync` handles `webrtc.renegotiate` from an existing viewer only.
- Valid payload requires `reason == "direct_connection_failed"`, a UUID `negotiationId`, and `iceTransportPolicy == "relay"`.
- The viewer peer factory call for the replacement is `peers.Create(participantId, forceRelay: true)`.
- Repeating the same request ID is idempotent; a second, different fallback ID for that participant is ignored.

- [ ] **Step 1: Add failing tests for validation, deduplication, and peer isolation**

Add tests asserting that a valid request replaces and disposes the viewer's direct peer, creates exactly one relay-only peer, and sends a fresh offer tagged with the request ID. Add tests that malformed payloads, unknown participants, duplicate IDs, and a second fallback ID do not replace peers. Keep a second viewer connected and assert its peer is not disposed.

```csharp
await publisher.HandleAsync(RenegotiateEnvelope(viewerA, retryId), CancellationToken.None);
Assert.Equal([false, true], factory.CreatedForceRelay);
Assert.True(factory.CreatedPeers[0].Disposed);
Assert.False(factory.CreatedPeers[1].Disposed);
Assert.Equal(retryId, ReadNegotiationId(signaling.Last(SignalingMessageTypes.WebRtcOffer)));
```

- [ ] **Step 2: Run publisher tests and confirm the renegotiation cases fail**

Run: `dotnet test tests/SonicDesktopRelay.Rtc.Tests/SonicDesktopRelay.Rtc.Tests.csproj --filter "FullyQualifiedName~VideoPublisherTests"`

Expected: FAIL because `VideoPublisher` ignores `webrtc.renegotiate` and creates peers without policy overrides.

- [ ] **Step 3: Add validated, one-shot replacement per viewer**

Validate the authenticated envelope `From` against the active peer map and validate all three payload fields. Under the publisher's peer lifecycle lock, atomically detach the old peer and send queue, record the fallback request ID, create/configure the relay peer and its queue, then dispose the old pair. Keep the shared capture/encode subscriptions unchanged. Send the new offer and tag all its candidates with the request ID.

- [ ] **Step 4: Re-run publisher tests**

Run: `dotnet test tests/SonicDesktopRelay.Rtc.Tests/SonicDesktopRelay.Rtc.Tests.csproj --filter "FullyQualifiedName~VideoPublisherTests"`

Expected: PASS; only the requesting viewer is replaced, and retries are idempotent and bounded.

- [ ] **Step 5: Commit publisher fallback negotiation**

```powershell
git add src/SonicDesktopRelay.Rtc/VideoPublisher.cs src/SonicDesktopRelay.Rtc/VideoSampleSendQueue.cs tests/SonicDesktopRelay.Rtc.Tests/VideoPublisherTests.cs
git commit -m "feat(rtc): renegotiate failed viewers through TURN"
```

### Task 4: Add the viewer's one-shot fallback state machine and deadlines

**Files:**
- Modify: `src/SonicDesktopRelay.Rtc/VideoSubscriber.cs`
- Modify: `src/SonicDesktopRelay.Rtc/SipSorceryViewerPeerConnection.cs`
- Test: `tests/SonicDesktopRelay.Rtc.Tests/VideoSubscriberTests.cs`

**Interfaces:**
- Retry only when the active viewer peer emits terminal `failed`, has never emitted `connected`, and `TransportDiagnostics.Path == "Direct"`.
- On retry, create a fresh receiver `negotiationId`, replace the peer with `peers.Create(forceRelay: true)`, clear candidates from the prior generation, then send the `webrtc.renegotiate` request to `PublisherId`.
- Use the injected `TimeProvider`: 15 seconds from request until a matching relay offer; 30 seconds from applying that offer until `connected`.
- Raise `NegotiationFailed` with the timed-out stage if either deadline expires or the relay peer fails. Never retry again for this subscriber.
- Mark `ScreenWatchPipeline` as `Failed` when negotiation failure becomes terminal.

- [ ] **Step 1: Add failing tests for eligibility and one-shot behavior**

Use the existing fake peer in `VideoSubscriberTests` and expose methods to raise `ConnectionStateChanged` and set transport diagnostics. Assert that Direct + failed-before-connected creates a relay peer and sends one request; Connected-then-failed, TURN + failed, no selected transport + failed, and duplicate failure events do not send a request.

```csharp
firstPeer.ReportTransport(new RtcTransportDiagnostics("Direct", "UDP", "host", "host"));
firstPeer.ReportConnectionState(connected: false);
Assert.Equal([false, true], factory.CreatedForceRelay);
Assert.Single(signaling.Sent.Where(x => x.Type == SignalingMessageTypes.WebRtcRenegotiate));
```

- [ ] **Step 2: Run the targeted subscriber tests and confirm they fail**

Run: `dotnet test tests/SonicDesktopRelay.Rtc.Tests/SonicDesktopRelay.Rtc.Tests.csproj --filter "FullyQualifiedName~VideoSubscriberTests"`

Expected: FAIL because the peer connection state event and relay retry state machine do not exist.

- [ ] **Step 3: Implement peer replacement, generation filtering, and deadlines**

Serialize peer replacement and message handling under the subscriber gate. Reject candidates and offers whose ID does not match the current generation. Use `FakeTimeProvider`-compatible timers. Emit metadata-only diagnostics `viewer.relay_fallback.started`, `viewer.relay_fallback.offer_timeout`, `viewer.relay_fallback.connection_timeout`, `viewer.relay_fallback.completed`, and `viewer.relay_fallback.failed`; never add addresses, SDP, candidate text, or credentials.

- [ ] **Step 4: Add and run timeout and stale-generation tests**

Add tests advancing `FakeTimeProvider` by 15 seconds without an offer and by 30 seconds after applying an offer without `connected`; assert each raises one failure with the correct stage. Add a test delivering an old direct offer/candidate after retry and assert the relay fake peer receives neither.

Run: `dotnet test tests/SonicDesktopRelay.Rtc.Tests/SonicDesktopRelay.Rtc.Tests.csproj --filter "FullyQualifiedName~VideoSubscriberTests"`

Expected: PASS; eligibility, bounds, stale-message rejection, and both deadlines are deterministic.

- [ ] **Step 5: Commit viewer retry state machine**

```powershell
git add src/SonicDesktopRelay.Rtc/VideoSubscriber.cs src/SonicDesktopRelay.Rtc/SipSorceryViewerPeerConnection.cs tests/SonicDesktopRelay.Rtc.Tests/VideoSubscriberTests.cs
git commit -m "feat(rtc): retry failed direct viewers through TURN"
```

### Task 5: Route renegotiation and verify session-level cleanup

**Files:**
- Modify: `src/SonicDesktopRelay.Presentation/SessionRuntime.cs`
- Modify: `src/SonicDesktopRelay.App/RtcVideoPublishHost.cs`
- Modify: `src/SonicDesktopRelay.App/RtcVideoWatchHost.cs`
- Test: `tests/SonicDesktopRelay.Presentation.Tests/SessionRuntimeTests.cs`
- Test: `tests/SonicDesktopRelay.Rtc.Tests/VideoPublisherTests.cs`
- Test: `tests/SonicDesktopRelay.Rtc.Tests/VideoSubscriberTests.cs`

**Interfaces:**
- Route inbound `webrtc.renegotiate` only while sharing to `IVideoPublishHost.HandleSignalingAsync`.
- Keep the watch host's ICE server credentials and construct initial direct / fallback relay peer factories from the same loaded server set.
- Session end, viewer removal, peer disposal, and timeouts must cancel fallback timers and clear generation-specific candidates/queues.

- [ ] **Step 1: Add failing session routing and teardown tests**

Extend the `SessionRuntimeTests` fake signaling flow: during `Sharing`, a renegotiate frame from a participant must reach the publish host; during `Watching`, it must not be treated as a request to the local publish host. Add cleanup tests that remove the viewer or dispose the subscriber during each timeout and assert no late offer, timer callback, or media send occurs.

```csharp
connection.Emit(SignalingMessageTypes.WebRtcRenegotiate, payload: RenegotiatePayload);
Assert.Contains(SignalingMessageTypes.WebRtcRenegotiate, publishHost.Signalled);
Assert.DoesNotContain(SignalingMessageTypes.WebRtcRenegotiate, watchHost.Signalled);
```

- [ ] **Step 2: Run the targeted session and RTC tests and confirm routing assertions fail**

Run: `dotnet test tests/SonicDesktopRelay.Presentation.Tests/SonicDesktopRelay.Presentation.Tests.csproj --filter "FullyQualifiedName~SessionRuntimeTests"`

Expected: FAIL because `SessionRuntime` does not route `webrtc.renegotiate` to the publisher host.

- [ ] **Step 3: Route the message and preserve lifecycle ownership**

Add `WebRtcRenegotiate` to the sharing switch and `IsHandled` map. Ensure host-created factories keep using the exact ICE server list loaded for the session, changing only `ForceRelay` for the replacement peer. On host stop/dispose, unsubscribe and dispose the subscriber/publisher timers and queues as part of the existing stack cleanup.

- [ ] **Step 4: Run presentation, publisher, and subscriber tests**

Run: `dotnet test tests/SonicDesktopRelay.Presentation.Tests/SonicDesktopRelay.Presentation.Tests.csproj --filter "FullyQualifiedName~SessionRuntimeTests"` and `dotnet test tests/SonicDesktopRelay.Rtc.Tests/SonicDesktopRelay.Rtc.Tests.csproj --filter "FullyQualifiedName~VideoPublisherTests|FullyQualifiedName~VideoSubscriberTests"`

Expected: PASS; the control-plane message reaches only the publisher path and teardown prevents late work.

- [ ] **Step 5: Commit signaling route and lifecycle integration**

```powershell
git add src/SonicDesktopRelay.Presentation/SessionRuntime.cs src/SonicDesktopRelay.App/RtcVideoPublishHost.cs src/SonicDesktopRelay.App/RtcVideoWatchHost.cs tests/SonicDesktopRelay.Presentation.Tests/SessionRuntimeTests.cs tests/SonicDesktopRelay.Rtc.Tests/VideoPublisherTests.cs tests/SonicDesktopRelay.Rtc.Tests/VideoSubscriberTests.cs
git commit -m "feat(rtc): route relay fallback through session signaling"
```

### Task 6: Verify the complete branch and prepare the pull request

**Files:**
- Review: all files changed by Tasks 1–5
- Verify: `SonicDesktopRelay.sln`
- Publish: the `codex/webrtc-turn-retry` branch and its pull request

- [ ] **Step 1: Run all RTC tests**

Run: `dotnet test tests/SonicDesktopRelay.Rtc.Tests/SonicDesktopRelay.Rtc.Tests.csproj`

Expected: PASS with no regressions in offer/answer, RTP integrity, and sender queue behavior.

- [ ] **Step 2: Run all Presentation tests**

Run: `dotnet test tests/SonicDesktopRelay.Presentation.Tests/SonicDesktopRelay.Presentation.Tests.csproj`

Expected: PASS with session lifecycle and signaling routing behavior intact.

- [ ] **Step 3: Build the full solution**

Run: `dotnet build SonicDesktopRelay.sln`

Expected: PASS with no compile warnings introduced by the new peer factory contracts.

- [ ] **Step 4: Review the final diff and commit only intended files**

Run: `git diff origin/main...HEAD --check` and `git diff --stat origin/main...HEAD`.

Expected: the diff contains the spec, plan, RTC/session implementation, and focused tests; no secrets, generated outputs, or unrelated files.

- [ ] **Step 5: Push and open the pull request**

Run: `git push -u origin codex/webrtc-turn-retry`, then create a pull request from `codex/webrtc-turn-retry` into `main` titled `fix: retry failed direct viewers through TURN`.

Expected: GitHub returns a pull request URL and the remote branch head matches the verified local head.
