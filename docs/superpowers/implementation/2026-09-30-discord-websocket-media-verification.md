# Discord WebSocket media implementation and verification

Approved design: [spec](../specs/2026-09-30-discord-websocket-media-design.md). Execution covered RelayControl, desktop publisher and Go repository Activity. Original dirty checkouts were preserved; changes are local and not deployed.

## Changes

| Repository | Main files | Result |
| --- | --- | --- |
| RelayControl | API Features, Session/PublicRoom/Activity endpoints, MediaRelayGrantService, capability cleanup, Program | Official Microsoft.FeatureManagement with existing-feature defaults true; optional media default false; scoped grants and fenced leases |
| RelayControl | services/SonicRelay.MediaRelay, src/SonicRelay.MediaProtocol, Dockerfile/Compose/CI, protocol fixtures | Separate single-instance forwarder; no transcode; 8 MiB messages and 64-message/16 MiB queues; lease renewal; persistent slow-viewer disconnect |
| Desktop | AppComposition, RtcVideoPublishHost, MediaRelayApiClient, Media/WebSocket | Process opt-in upload; shared H264/Opus samples; independent copied-frame H264 branch when RTC uses AV1 |
| Go/Activity | media-protocol/client/player/audio-playback, main, canvas, tests, CI | WebSocket first-message grant; WebCodecs capability checks; bounded canvas/PCM playback and disposal; retry network failures with fresh admission |

## Feature boundaries

`FeatureManagement__PublicRooms=true`, `FeatureManagement__DiscordActivity=true`, `FeatureManagement__DuplexAudio=true` and `FeatureManagement__ScreenShare=true` preserve existing behavior when missing. Explicit false overrides defaults. Public-room HTTP and background startup are gated. Activity authorization/admission and bot Activity-only launch routes return404 when disabled; desktop completion/watch fallback routes remain. Disabled duplex creation and screen-share creation/code join/ID join/Activity admission reject before mutation. Existing session end, device identity, auth, retention, TURN and generic signaling stay available.

The new API `FeatureManagement__DiscordWebSocketMedia=false` and publisher `FRAMERELAY_WEBSOCKET_MEDIA_ENABLED=false` require explicit opt-in. Desktop reads a process variable; it does not load .env. Docker env changes require container recreation. Separate `MediaRelay__ServiceToken` is shared only by API/forwarder, not bot/Activity bundle. `MediaRelay__PublicBaseUrl` configures desktop upload; Discord `/media` mapping serves Activity sockets while `/relay` remains the API mapping.

## Evidence

- API solution restore/build verified with zero warnings/errors; full suite: 339 pass (274 API integration, 12 protocol, 6 forwarder, 40 virtual publisher, 7 signaling).
- Desktop focused media tests:50 pass, including fragmented keyframe control and real ClientWebSocket upload, raw/encoded memory ownership, default off, bounds and existing pipeline regression tests. API client/session tests:9 pass.
- Activity:26 Node tests pass; TypeScript and Vite production build pass. Tests include shared binary vectors, invalid headers, support check, keyframe recovery, stale epochs, closed decoder replacement, cleanup and no RTC/TURN access.
- Protocol fixture SHA256 identical in all three repositories: `D91E1E99F4E20C296E7F48FF3328C5D9D0A7D98E993C599BB277909C9D6A237A`.
- Real headless Edge fixture, explicit Run button click: native generated H264/Opus through the actual forwarding registry and sockets,3 rendered video frames,9 decoded audio blocks, green canvas pixel `[0,255,1,255]`, nonzero PCM energy154.314, actual codec `avc1.42c00b`. Audio was measured as PCM; audible output was not claimed.
- Separate forwarder Release publish succeeds. Deployment and infra Compose configurations validate with nonsecret fixtures.
- Repeated release of a removed admission is idempotent; the regression test passes.
- Fresh independent review identified closed decoder recovery and persistent slow viewers; both reproduced and corrected with regression tests. Slow viewer disconnects after three queue overflows within ten seconds; another viewer continues receiving.

## Validation limits

Docker daemon unavailable on this machine: image build/run and production deployment were not verified. No live Discord Activity, real Windows capture opt-in, audible A/V synchronization, portal mapping or simultaneous two-machine RTC viewer was exercised. The browser fixture verifies native codec decoding and transport locally; it is not Discord runtime proof. Keep both new flags false until the documented live rollout checks pass. No dependency upgrades were made; only the requested official Microsoft.FeatureManagement4.7.0 package was added to the API.

## Local fixture

Run API repository `dotnet run --project tests/SonicRelay.MediaRelay.BrowserFixture` (localhost5174), start Activity Vite on5173, open `/tests/browser-media-fixture.html` and click Run. Fixture fake credentials are local test data; this project is not included in the production images. The optional dependency-free `activity/tests/run-browser-media-fixture.mjs` runs against an owned Chromium debugging instance on9227, opens/closes its fixture tab and supplies the button click.
