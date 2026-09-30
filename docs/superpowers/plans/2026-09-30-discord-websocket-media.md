# Discord WebSocket Media Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deliver publisher-originated encoded video/audio to Discord through an isolated WebSocket forwarder, with opt-in flags and WebCodecs playback.

**Architecture:** Desktop keeps its RTC publisher and adds an independent encoded-media upload. RelayControl issues scoped grants and authorizes short leases; a separate forwarding process fans out binary frames. Activity retains Discord identity/session admission and renders decoded media without RTCPeerConnection.

**Tech Stack:** .NET 10, ClientWebSocket, ASP.NET Core, existing H.264/Opus encoders, TypeScript/Vite, WebCodecs, canvas, Web Audio, Docker.

**Spec:** [Approved design](../specs/2026-09-30-discord-websocket-media-design.md). Dependency: [RelayControl flags plan](2026-09-30-relaycontrol-feature-flags.md).

## Global Constraints

- Use fresh isolated branches from fetched main for RelayControl and Go repo; retain the current desktop worktree/spec commit. Preserve original dirty files and the existing Activity error-summary changes. No forced checkout/pull.
- Confirmed baselines: API main `8c8ad7a`, Go main `4ec5dfd`. Desktop includes the approved spec at `2f56b13`; compare against fetched main before execution and resolve drift without discarding user changes.
- API `FeatureManagement__DiscordWebSocketMedia=false` by default; desktop `FRAMERELAY_WEBSOCKET_MEDIA_ENABLED=false` by default. Four existing API feature flags remain true by default.
- No RTC/TURN/signaling transport replacement. No API-side transcode, persistent media store or cross-instance forwarding. One forwarder instance owns each upload/fanout.
- Maximum whole WebSocket message 8 MiB, per-connection queue maximum 64 messages and 16 MiB. Authorization leases revalidate within 15 seconds and fail closed on expiry.
- Reuse H.264/Opus samples when compatible. When RTC uses AV1, Activity receives an independent H.264 branch; failure of that branch does not downgrade or stop RTC.
- Use CodeGraph before existing-code edits. No full desktop test suite; run the relevant projects/classes. RelayControl full solution check is expressly requested by the user.
- Product dependencies stay unchanged except Microsoft.FeatureManagement in the preceding plan. Use existing test frameworks, browser tooling and native TypeScript DOM declarations; validate any needed WebCodecs type additions locally rather than adding packages automatically.

## Review Focus

1. Expired upload grants must never disconnect a publisher's RTC participant; Task 2 cleanup tests.
2. Reconnect/release races must not revoke a newly active viewer lease or leak capacity; Tasks 2 and 3 fencing tests.
3. Native capture buffers may be reused after event callbacks; Task 4 copied-memory tests.
4. Codec changes or oversize/backlogged frames cannot allow undecodable delta frames through; Tasks 3–5 recovery tests.
5. Supported WebCodecs in an ordinary browser is not proof of support inside Discord; Tasks 5 and 7 explicit live checks.

## File Map

RelayControl creates `src/SonicRelay.MediaProtocol/{MediaMessage,MediaWireCodec}.cs`, its csproj and tests; API `Endpoints/MediaRelayEndpoints.cs`, `Contracts/MediaRelayContracts.cs`, `Services/MediaRelayGrantService.cs`, `Authorization/MediaRelayServiceAuthenticationHandler.cs`; and a separate `services/SonicRelay.MediaRelay/` web project with endpoints, registry, queue and lease client. API modifies Program, RelayFeatures, Activity admission and capability cleanup only at their defined new boundaries. Add tests and project references to SonicRelay.sln.

Desktop creates `src/SonicDesktopRelay.Media/WebSocket/` protocol/options/queue/publisher/optional-H264-branch files and `src/SonicDesktopRelay.ApiClient/MediaRelayApiClient.cs`; modifies AppComposition/RtcVideoPublishHost and only the minimal capture handoff required by the isolated branch. Add focused Media, ApiClient and App tests. Do not change preferences/UI just to add the process switch.

Activity creates `activity/src/{media-protocol,media-client,media-player,audio-playback}.ts` and corresponding `.test.mjs` files; modifies main.ts, index.html/style.css for canvas, package scripts, README and existing deployment workflow as necessary. Keep safeErrorSummary from current main.

Each repository carries `docs/protocol/discord-media-v1.md` and identical `docs/protocol/discord-media-v1-vectors.json` fixtures. Production deploy adds a separate media image/container and `/media` mapping guidance.

### Task 1: Freeze and test the cross-language wire protocol

**Files:** API protocol project/tests; desktop WebSocket/MediaMessage.cs and MediaWireCodec.cs; Activity media-protocol.ts/tests; protocol documents/fixtures in all three repos.
**Interfaces:** C# `MediaMessage(byte Type, ushort Flags, uint Generation, uint Sequence, long TimestampUs, long DurationUs, ReadOnlyMemory<byte> Payload)`; `MediaWireCodec.Encode(MediaMessage) -> byte[]`, `Decode(ReadOnlyMemory<byte>) -> MediaMessage`. TypeScript exports corresponding `MediaMessage`, `encodeMediaMessage(message): Uint8Array`, `decodeMediaMessage(bytes: Uint8Array): MediaMessage`, and `MediaConfiguration` matching the config payload below. API protocol test project lives at `tests/SonicRelay.MediaProtocol.Tests/SonicRelay.MediaProtocol.Tests.csproj`; desktop tests at `tests/SonicDesktopRelay.Media.Tests/MediaWireCodecTests.cs`.

Wire header is 40 bytes, little endian: bytes 0–3 ASCII FRM1; byte 4 version=1; byte 5 type (1=config, 2=video, 3=audio, 4=keyframe-request); bytes 6–7 flags (bit0=keyframe); generation u32 at 8; sequence u32 at 12; payload length u32 at 16; reserved zero u32 at 20; timestamp i64 at 24; duration i64 at 32. Reject unknown type/version/flags, nonzero reserved, generation zero, negative times, unsafe JavaScript integers and lengths not exactly matching the bounded whole message. Config is UTF-8 JSON metadata, never JSON/base64 media. Keyframe-request has an empty payload.

Config payload: `{video:{codec,width,height,format:"annexb"},audio:{codec:"opus",sampleRate,channels}|null}`. Derive H.264 codec string from actual SPS rather than hardcoding encoder profile. Audio format must match actual Opus output. A generation begins with configuration and a keyframe containing necessary parameter sets; sequence restarts at zero and strictly increases within a generation. On reconnect or codec/size change, increment generation; do not wrap.

- [ ] Write C#/TS tests `Roundtrip_matches_shared_vectors`, `Rejects_truncated_or_oversized_message`, `Rejects_invalid_header_and_unsafe_timestamp`. Assert Decode(Encode(vector)) fields and payload equality; changing payloadLength or version rejects. Use one keyframe vector at timestamp 1,000,000 us and duration 16,667 us, plus audio/config/control and invalid vectors.
- [ ] Run `dotnet test tests/SonicRelay.MediaProtocol.Tests/SonicRelay.MediaProtocol.Tests.csproj`, `dotnet test tests/SonicDesktopRelay.Media.Tests/SonicDesktopRelay.Media.Tests.csproj --filter FullyQualifiedName~MediaWireCodecTests`, and `node --experimental-strip-types --test tests/media-protocol.test.mjs` in Activity; confirm failures precede implementations.
- [ ] Implement codecs without external protocol dependencies. Check SHA256 of fixture files agrees across repos; copied fixtures, not cross-repo build references. Re-run all three focused tests to pass. Commit per repository: `feat: define versioned Discord media wire protocol`.

### Task 2: API grants, feature opt-in and media participant lifecycle

**Files:** API MediaRelayContracts/Endpoints/GrantService/ServiceAuthenticationHandler; Program, RelayFeatures, DiscordActivityEndpoints, LaunchCapabilityCleanupService; API `MediaRelayGrantTests.cs`, `MediaRelayPresenceTests.cs` and existing Activity tests.
**Interfaces:**
- `POST /api/sessions/{sessionId}/media-grants` with DeviceBearer creates upload grant only for current source device and live screen_share session.
- Extend `/api/discord/activity/viewer-grants/redeem` request with optional `transport="websocket"`. Preserve absent-transport behavior. New branch returns MediaAdmission and creates a media viewer capability instead of an unused signaling capability/TURN credentials.
- `MediaAdmission(Guid AdmissionId, Guid SessionId, Guid ParticipantId, string Grant, DateTimeOffset ExpiresAt, string MediaUrl)`; AdmissionId identifies the issued capability, and media URL contains no credential. Upload uses the existing source participant ID in the response while its cleanup capability leaves ParticipantId null as specified below.
- `POST /api/discord/activity/media-admissions/{admissionId}/release` accepts the existing Activity identity bearer and releases only that user's/instance's current media-view capability. A replaced/expired admission returns success without disconnecting a newer lease. This supports cleanup before the socket has redeemed its grant.
- Internal authenticated `POST /api/internal/media-relay/redeem {grant,connectionId}`, `/renew {leaseId,connectionId}`, `/release {leaseId,connectionId}`. `MediaLease(Guid LeaseId, Guid SessionId, Guid? ParticipantId, string Role, DateTimeOffset ExpiresAt)`; role exactly upload or view. Service policy reads `MediaRelay:ServiceToken`, never accepts DeviceBearer or bot launch credentials as service auth.
- `MediaRelayGrantService.IssueUploadAsync(Guid sessionId, ClaimsPrincipal user, CancellationToken ct) -> Task<MediaAdmission?>`, `RedeemAsync(string grant, Guid connectionId, CancellationToken ct) -> Task<MediaLease?>`, `RenewAsync(Guid leaseId, Guid connectionId, CancellationToken ct) -> Task<MediaLease?>`, `ReleaseAsync(Guid leaseId, Guid connectionId, CancellationToken ct) -> Task<bool>` and `ReleaseAdmissionAsync(Guid admissionId, LaunchCapability identity, CancellationToken ct) -> Task<bool>`; use existing API-style IResult at endpoints.

- [ ] Write `Missing_media_flag_rejects_upload_without_mutation`, `Wrong_source_and_non_screen_share_rejected`, `Grant_is_single_use_and_role_bound`, `Unknown_or_expired_lease_rejected`, `Flag_disable_revokes_renewal`. Use fake time and real authorization fixtures; replay returns 401 and no second lease.
- [ ] Write `Upload_expiry_leaves_rtc_publisher_connected`, `Media_viewer_disconnect_releases_capacity`, `Old_connection_release_does_not_revoke_reconnected_viewer`, `Unredeemed_admission_can_be_released_by_its_owner`, `Identity_or_activity_revocation_ends_view_lease`, `Admission_without_transport_preserves_existing_contract`. Assert participant statuses and capacity after each race, not just response codes. A different Activity identity cannot release another user's admission.
- [ ] Run API tests filtered to MediaRelay and LaunchIntentActivity before implementation; verify failure for absent media routes/contracts.
- [ ] Add DiscordWebSocketMedia false default to the official feature manager. Require it plus ScreenShare for uploads and additionally DiscordActivity for viewers. Extend existing LaunchCapability kinds with media-upload/media-view; store only token hashes and use existing ConsumedAt concurrency guard for redemption. Grant lifetime 30 seconds; redeemed lease lifetime 30 seconds, renewal every 10 seconds. Original session/identity/Activity authorization remains authoritative at every renewal.
- [ ] Set upload capability ParticipantId=null so cleanup cannot mark the RTC source disconnected. Media-view capability owns only its Activity participant. Fence lease operations with connectionId persisted in the existing MessageId context field for these new kinds; use admission locks/concurrency checks. Admission reconnect reuses the matching Activity media participant and atomically revokes prior leases instead of allocating duplicate capacity. Existing Activity caps/cleanup retain their semantics; no flag tables or schema change solely for flags.
- [ ] Re-run focused API tests to pass; verify expired unreclaimed grants cannot leak viewer slots. Commit: `feat: authorize scoped Discord media grants and leases`.

### Task 3: Isolated forwarder with bounded fanout

**Files:** new MediaRelay project: Program.cs, `MediaRelayOptions.cs`, `MediaSocketEndpoint.cs`, `MediaRelayRegistry.cs`, `MediaConnectionQueue.cs`, `RelayControlLeaseClient.cs`; `tests/SonicRelay.MediaRelay.Tests/`, solution/project references.
**Interfaces:** `RelayControlLeaseClient.RedeemAsync(string grant, Guid connectionId, CancellationToken ct) -> Task<MediaLease?>`, `RenewAsync(...)`, `ReleaseAsync(...)`; `MediaRelayRegistry.AttachAsync(MediaLease lease, Guid connectionId, WebSocket socket, CancellationToken ct) -> Task`; `MediaConnectionQueue.TryEnqueue(MediaMessage message) -> bool` with messages/bytes counters.

- [ ] Write `Only_one_upload_per_session`, `Viewers_receive_identical_complete_samples`, `Unknown_grant_cannot_subscribe`, `Lease_expiry_closes_socket`, `Slow_viewer_does_not_block_upload_or_other_viewer`, `Partial_frame_cannot_allocate_above_8MiB`, `Generation_gap_requires_keyframe`, `Stale_lease_release_is_fenced`. Use bounded real loopback WebSocket tests with completion signals, cancellation and fake API responses.
- [ ] Run `dotnet test tests/SonicRelay.MediaRelay.Tests/SonicRelay.MediaRelay.Tests.csproj`; expect new implementation absent.
- [ ] Implement `/ws/media` with subprotocol `framerelay-media-v1`; authenticate first text message `{grant}` within 5 seconds, then use binary protocol. Upload and viewer roles come only from the redeemed lease. Never echo/log grant. Single receive/send owner per socket; maximum one upload per session, configured global defaults 64 upload sessions and 256 viewers.
- [ ] Apply 8 MiB whole-message and 64-message/16-MiB queue limits. Disconnect queue-overflow viewers and send timeouts after 5 seconds; do not block other viewers. Publisher upload congestion breaks only media connection. New viewer gets current config, requests a keyframe and skips delta/audio until stream recovery. Coalesce keyframe requests to at most one per second per upload. Reject viewer-originated media/config and upload-originated role changes.
- [ ] Renew every 10 seconds; close no later than lease expiry if API unavailable, with no indefinite grace. Release on close with connection fencing. Bound assembly, heartbeat and shutdown. Configuration: `RelayControl__BaseUrl`, `RelayControl__ServiceToken`; default process memory ceiling declared in deployment is separate from queue ceilings.
- [ ] Re-run forwarder tests to pass, then API lease regression tests. Commit: `feat: add isolated WebSocket media forwarding service`.

### Task 4: Opt-in desktop upload and codec-isolated H.264 output

**Files:** desktop MediaRelayApiClient.cs/tests; Media/WebSocket `WebSocketMediaOptions.cs`, `WebSocketMediaPublisher.cs`, `OwnedMediaQueue.cs`, `ActivityH264Output.cs`; AppComposition/RtcVideoPublishHost; capture handoff/ScreenPublishPipeline only if needed; focused Media/App tests.
**Interfaces:** `WebSocketMediaOptions.FromEnvironment(Func<string,string?> getEnvironment) -> WebSocketMediaOptions` with Enabled=false unless value parses true. `MediaRelayApiClient.CreateUploadGrantAsync(Guid sessionId, CancellationToken ct) -> Task<MediaAdmission>`. `WebSocketMediaPublisher.StartAsync(Guid sessionId, CancellationToken ct)`, `TryPublishVideo(EncodedVideoSample)`, `TryPublishAudio(EncodedAudioSample)`, `StopAsync()`, `DisposeAsync()`; injected media API client and socket factory. `ActivityH264Output.TryCapture(VideoFrame frame)` copies into owned bounded encode queue; independent RequestKeyFrame and disposal.

- [ ] Write `Disabled_has_zero_api_socket_encoder_side_effects`, `Upload_owns_samples_after_callback`, `Socket_failure_does_not_fail_rtc`, `Av1_rtc_stays_av1_with_h264_activity_output`, `Stop_cancels_grant_and_prevents_late_reconnect`, `Resize_emits_new_config_then_keyframe`. Assert call counters, mutated source memory independence, unchanged RTC codec and no post-stop sends.
- [ ] Run desktop tests filtered to WebSocketMedia/ActivityH264Output and existing RtcVideoPublishHostCodecFallbackTests before code. Confirm new behavior is absent.
- [ ] Read flag in AppComposition and inject options plus existing authenticated session HTTP client. Subscribe to encoded H.264 and Opus events with nonblocking owned-memory enqueue. Reuse MediaSessionClock. The send loop emits config/generation and requests keyframes; implement reconnect backoff 1, 2, 4, 8 seconds, always with new grants and cancellation generation guards. No upload work for flag false.
- [ ] For AV1, attach an ownership-safe raw-frame tap to the current capture source with a distinct bounded H.264 encoder queue; do not start another capture or change the shared encoder. Set its quality ceiling from the active publishing profile. Copy BGRA during the callback before native buffers can be reused. If no H.264 branch can start, log/display a media diagnostic while RTC continues. Keep new diagnostics separate from StartFailure/VideoPipelineFailure that stop RTC.
- [ ] Dispose extra subscriptions/branch/upload before shared capture disposal. Run new tests plus existing ScreenPublishPipelineTests, VideoFrameEncodeQueueTests, VideoSampleSendQueueTests, RTC fanout tests and ApiClient grant tests using targeted class filters. Commit: `feat: add flag-controlled Activity media upload without RTC regression`.

### Task 5: WebCodecs player and timestamp-based synchronization

**Files:** Activity media-player.ts, audio-playback.ts and `.test.mjs` tests; index.html/style.css canvas changes.
**Interfaces:** `checkDecoderSupport(config: MediaConfiguration): Promise<void>`; `MediaPlayer.configure(config, generation): Promise<void>`, `accept(message: MediaMessage): void`, `resumeAudio(): Promise<void>`, `close(): void`. Inject clock/decoder/output adapters for unit tests. `AudioPlayback.enqueue(data: AudioData, timestampUs: number)`, `resume()`, `reset()`, `close()` owns a bounded Web Audio schedule.

- [ ] Write `Unsupported_configuration_is_reported_before_decode`, `Delta_before_keyframe_is_ignored`, `Old_generation_is_discarded`, `Decoder_backlog_resets_and_requests_keyframe`, `Audio_video_use_shared_media_clock`, `Close_releases_frames_audio_and_context`, `Blocked_autoplay_requires_user_gesture`. Assert decoder calls and closed resources using fake adapters; no fake-only playback success claim.
- [ ] Run `node --experimental-strip-types --test tests/media-player.test.mjs tests/audio-playback.test.mjs`; expect new modules absent.
- [ ] Implement actual VideoDecoder/AudioDecoder.isConfigSupported checks. Submit EncodedVideoChunk/EncodedAudioChunk with microsecond timestamps. Decode H.264 Annex B only after config+keyframe. Render/close VideoFrames on canvas; copy decoded PCM into bounded AudioBuffers and close AudioData. Use AudioContext time as presentation clock once audio runs, performance.now when video-only; establish 100 ms initial buffering, discard video over 150 ms late, keep decoder backlog <=8 video and <=32 audio chunks and scheduled audio <=500 ms.
- [ ] Audio gap/overflow resets the schedule; decoder error or invalid video dependency requests a coalesced keyframe and clears stale generations. Avoid unbounded Promise chains/arrays. A resize reconfigures only after releasing old frames/decoders. No requirement for SharedArrayBuffer or cross-origin isolation.
- [ ] Run player tests and `npm run build`; expect actual TypeScript checks pass. Commit: `feat: decode and synchronize Activity media with WebCodecs`.

### Task 6: Activity integration and capacity-safe reconnect

**Files:** Activity media-client.ts/tests, main.ts, package.json test script, existing error-summary tests; API Activity/media integration tests as needed.
**Interfaces:** `MediaClient.connect(admission: MediaAdmission, player: MediaPlayer, signal: AbortSignal): Promise<void>`, `close(): Promise<void>`; existing authorize/viewer-grant requests retained, redemption sends transport websocket.

- [ ] Write `No_rtc_constructor_or_ice_request_in_media_flow`, `Reconnect_uses_new_grant_and_generation`, `Stop_releases_admission_and_prevents_retry`, `Capability_failure_cleans_up_allocated_viewer`, `Session_end_closes_player`, `Feature_disabled_is_clear_to_user`. Assert zero RTCPeerConnection/RTCRtpReceiver accesses, participant release and no leaked decoder/socket after stop.
- [ ] Run `node --experimental-strip-types --test tests/media-client.test.mjs tests/media-player.test.mjs tests/error-summary.test.mjs`; expect flow still RTC before implementation.
- [ ] Wire canvas/player to existing Discord SDK identity flow and websocket admission. Check decoder API presence before admission and actual config on arrival; configuration failure and client stop call the admission-release endpoint from Task 2 with the identity bearer and AdmissionId. Derive viewer WSS URL using `/media/ws/media` through Discord mapping, not the direct upload origin returned for desktop. Authenticate first-message grant, parse binary with bounded assembly, forward keyframe controls.
- [ ] Replace Activity-only RTC setup, offer/ICE handling and video-track rendering. Keep error sanitization and status stages. Handle manual retry, stop, closed session and reconnect with fresh admission/media lease under cancellation generation guard. Do not change bot command authentication or generic desktop launch semantics.
- [ ] Re-run new tests, existing routing/error-summary tests and `npm run build`. Commit: `feat: connect Discord Activity using WebSocket media admission`.

### Task 7: Deployment integration, fixtures and live validation

**Files:** API Dockerfile or new `services/SonicRelay.MediaRelay/Dockerfile`; `.github/workflows/vps-ci-cd.yml`, deploy/deploy.sh, deploy/docker-compose.prod.yml, appropriate infra Compose/env templates, API README; desktop README and Activity README/workflow when required. Add API/forwarder end-to-end test and Activity browser fixture page/test.

- [ ] Write `Protocol_fixture_plays_decoded_video_and_audio` with a known H.264/Opus fixture, browser canvas pixel/frame evidence and nonzero decoded PCM/RMS audio assertion. Write an API+forwarder test proving disabled flags prevent connections, authorized upload fans out to a real client, and API session-end closes leases without affecting a concurrent signaling client. Fixtures use generated content, not third-party copyrighted media.
- [ ] Run those tests to expose integration gaps, then configure fixtures/host endpoints to pass using available browser tooling; do not install product/test dependencies without a concrete need and authorization. Browser autoplay test uses an explicit gesture.
- [ ] Build/publish a separate media image and deploy service on infra_network with 512 MiB memory ceiling, global connection ceilings from Task 3, internal API/service credential and exposed WSS proxy path. Preserve existing API image/SHA selection, TURN container and generic signaling routes. Verify env_file passes FeatureManagement__DiscordWebSocketMedia=false by default and MediaRelay__PublicBaseUrl/ServiceToken only to appropriate services. Never dump resolved secrets.
- [ ] Document `/media` Discord URL Mapping, desktop process variable/restart, API container recreation, both opt-ins and no WebRTC fallback in Activity. Example env: existing four flags true, new API media false, publisher false. Changing defaults must be an explicit rollout action, not an incidental deploy side effect.
- [ ] Run `dotnet restore SonicRelay.sln`, `dotnet build SonicRelay.sln --no-restore`, `dotnet test SonicRelay.sln --no-build --logger "console;verbosity=minimal"` in RelayControl. Desktop: build solution and run only affected projects/classes. Activity: `npm run build` plus all directly affected Node tests. Compose: config --quiet with nonsecret fixture env. Record actual outputs and unresolved runtime requirements.
- [ ] Commit per repository: `chore: deploy and document isolated Discord media forwarding`. Review diffs for unrelated API/RTC changes; push/PR only under the agreed delivery scope.
- [ ] Live verification is a separate check: deploy media/API/Activity builds and desktop opt-in, configure mapping, run `/framerelay watch`, verify decoded video, audible synchronized audio, late viewer/keyframe recovery, reconnect, session end, flag-off behavior and simultaneous desktop RTC viewer. Do not claim this passed without real evidence. If server access, portal access or a live publisher is missing, report exactly that boundary and keep flags opt-in.

## Handoff

Review both plans before execution. Recommend native implementation in this chat because grants, byte layout, memory ownership and decoder lifecycle depend closely on each other. If delegation is selected, obey the user's allowed GPT-6 Luna/Sol low-or-medium configurations and prohibit recursive subagents; the standard header does not override those limits.
