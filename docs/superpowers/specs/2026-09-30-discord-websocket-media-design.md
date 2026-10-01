# Discord WebSocket media and feature flags

Date: 2026-09-30
Status: design for review; product implementation has not started.

## Intent and approved approach

The user approved publisher-originated encoded media over WebSocket, with a separate forwarding service and the existing WebRTC path preserved. The user also requested a publisher feature flag and the four RelayControl flags described in the supplied requirements. The Activity will use WebCodecs for decoding. This is an architectural change spanning three repositories, not a replacement for generic signaling.

Repositories: `vitorhugo-dotnet/dotnet_FrameRelay`, `vitorhugo-dotnet/dotnet_RelayControl`, and `vitorhugo-dotnet/go_discord_FrameRelay`. The older `vitorhugo-java/dotnet_SonicRelay` URL redirects to RelayControl; implementation must use the current repository and fetched main. Preserve existing dirty checkouts and the current Activity diagnostic changes.

Success means a live publisher can deliver synchronized screen video and audio to an authorized Discord Activity without RTCPeerConnection, while existing desktop viewers retain their WebRTC behavior. Build/test success alone does not establish live Discord playback.

## Platform constraints

Discord Activities explicitly do not support WebRTC. WebSocket is supported through the Activity proxy and configured URL mappings. Source: https://docs.discord.com/developers/activities/development-guides/networking

WebCodecs exposes VideoDecoder and AudioDecoder, but codecs and configurations are implementation-dependent. Use isConfigSupported with the actual stream configuration; API presence alone is insufficient. Source: https://www.w3.org/TR/webcodecs/

Decoded video renders to canvas. Decoded audio feeds a bounded Web Audio playback path with an explicit user gesture when autoplay is blocked. WebCodecs does not itself provide media playback or audio/video synchronization. No fallback to RTCPeerConnection inside the Activity.

## Feature flags and defaults

RelayControl uses the official Microsoft.FeatureManagement package, AddFeatureManagement(), the FeatureManagement configuration section, and centralized RelayFeatures names. Use the current supported manager interface and runtime checks at feature boundaries. Preserve explicitly configured false values when supplying defaults.

| Flag | Environment variable | Default | Boundary |
| --- | --- | --- | --- |
| PublicRooms | FeatureManagement__PublicRooms | true | Public-room endpoints and public-room publisher/background work |
| DiscordActivity | FeatureManagement__DiscordActivity | true | Activity identity, grant, admission and Activity launch flows |
| DuplexAudio | FeatureManagement__DuplexAudio | true | Creating or transitioning into duplex sessions |
| ScreenShare | FeatureManagement__ScreenShare | true | Creating or joining screen_share sessions |
| DiscordWebSocketMedia | FeatureManagement__DiscordWebSocketMedia | false | New media-grant issuance and authorization/renewal for forwarding |

Desktop publisher opt-in: `FRAMERELAY_WEBSOCKET_MEDIA_ENABLED=true`, default false. Read once in AppComposition and inject an immutable option into the additional publishing component. This is a process environment variable; the desktop does not automatically load Docker .env files. Do not add a settings UI or Microsoft.FeatureManagement dependency to the desktop solely for this switch.

With publisher opt-in false, there is no media-grant request, WebSocket connection, additional encoder or media subscription. With it true, enable the additional path only for eligible screen_share publishing sessions. API opt-in is also required. Generic desktop WebRTC remains available with either new switch disabled.

The four existing API features remain enabled when their flags are missing. The new transport remains opt-in. PublicRooms preserves its existing configuration conditions in addition to the feature flag. DuplexAudio leaves broadcast audio unaffected. ScreenShare does not gate audio sessions or generic signaling. DiscordActivity does not indiscriminately disable unrelated desktop launch flows; inspect the existing shared launch endpoints and gate only the Discord feature behavior inside them.

Environment changes require container recreation or desktop process restart. Document this rather than implying Docker environment variables update dynamically. A service-side feature disable must reject new grants and close media authorization leases on their next validation; existing core session cleanup remains active.

## Publisher and codec isolation

RtcVideoPublishHost currently creates one ScreenPublishPipeline and one AudioPublishPipeline per publishing session. Both expose SampleEncoded events. New transport subscriptions must copy or retain sample memory under an explicit ownership contract before enqueueing; they must never await network I/O on capture/encode callbacks.

Reuse compatible H.264 samples and encoded Opus audio. The existing publisher can negotiate AV1, so an Activity must not force the shared RTC session to H.264. When the RTC encoder is AV1, the additional Activity output requires an independent H.264 encoding branch over ownership-safe captured frames. If that branch cannot start or becomes overloaded, fail only the Activity output and report why; do not silently downgrade or stop RTC viewers. No additional H.264 encoding occurs while the opt-in is false.

The same principle applies to codec transitions, capture resize and screen/window switches. Start a new media generation and supply fresh decoder configuration and a keyframe. WebSocket failures and reconnection must not propagate into the existing VideoPublisher failure path. Dispose subscriptions, queues, socket and any extra encoder independently at stop/session change.

## Forwarding service and authorization

Add a separate ASP.NET Core media forwarding project/container in the RelayControl repository. It accepts one authorized publisher upload per session and fans out encoded data to authorized Activity viewers. It does not terminate WebRTC or transcode. RelayControl remains the authority for session membership, roles, capacity, Activity presence and lifetime.

Add narrowly scoped media-grant endpoints. The source device obtains an upload grant using its existing authenticated API client and session ownership. An Activity obtains a view grant after existing identity/admission checks. Use expiring, single-use opaque grants scoped to session, participant, role and media transport; do not reuse consumed signaling tokens as media credentials.

The forwarding service validates/redeems grants through an internal authenticated RelayControl operation and holds short authorization leases, revalidated at most every 15 seconds. Session end, revoked membership, disabled feature or expired lease closes the corresponding media connections. An API outage cannot extend a lease indefinitely. No authentication bypass, trusting browser-supplied Discord IDs, or generic authorization feature flag.

Use HTTPS/WSS. Browser credentials travel via WebSocket subprotocol or an authenticated first-message handshake, never URL query strings. Internal service credentials stay server-side. Redact grants and tokens from logs. Bound unauthenticated handshake duration and message sizes. Reuse existing API error conventions; feature unavailability must be distinguishable from invalid identity.

A single forwarding instance owns all publisher/viewer sockets in this first version. Do not claim horizontal scale across instances: no Redis pub/sub, cross-instance routing or persistent media storage is introduced. Shared-memory fanout cannot span multiple containers.

## Media protocol and flow control

Use a versioned binary protocol, not JSON/base64 media. Each complete message carries type, generation, sequence, timestamp in microseconds, duration, keyframe status and payload length. Decoder configuration messages identify the precise codec, dimensions/profile and audio sample rate/channel count. Validate all lengths and values before allocation and decoding.

Video payloads are complete H.264 access units in Annex B format, with SPS/PPS available for decoder initialization and keyframe recovery. Audio payloads are complete Opus packets. Maintain one shared MediaSessionClock; use generation plus timestamps to reject stale messages and synchronize tracks. The implementation plan must freeze byte layout and cross-language protocol fixtures before product transport code.

Initial resource limits: maximum message 8 MiB; each connection queue bounded by both 64 messages and 16 MiB; no unbounded AudioDecoder/VideoDecoder backlog. Disconnect persistently slow viewers rather than accumulating delay. Dropped dependent video invalidates that viewer's video generation until a fresh keyframe. Keyframe requests are coalesced per publisher, including late join and reconnection. Audio underrun uses bounded silence/resynchronization, not an ever-growing queue. These are initial ceilings, not throughput promises.

The publisher keeps one upload connection per session. Reconnection requires a new grant and generation; canceled or previous-session operations cannot reconnect. The Activity obtains a new media grant on reconnect and starts from decoder configuration plus a fresh keyframe. No server-side decode, encode or MP4 muxing is required.

## Activity and deployment

Keep Discord SDK identity and admission. Replace the Activity's RTC/media setup with a media client, binary parser, decoder/rendering module and bounded audio playback. Check VideoDecoder, AudioDecoder and actual supported configurations before obtaining viewer admission wherever the configuration is available; if it arrives after admission, cleanly release the participant on failure. Provide a precise unsupported-client message and desktop-viewer alternative.

Map `/media` to the new forwarding service in the Discord Developer Portal; retain existing `/` frontend and `/relay` API mappings. RelayControl deploy files add the media container, resource/connection ceilings, lease-validation service credential and internal API address following existing infra_network conventions. Existing TURN and API deployment remain intact. The desktop receives the upload address from authenticated API responses rather than arbitrary browser input.

Supply .env examples with the four existing flags true and DiscordWebSocketMedia false. Publisher documentation shows its separate process environment opt-in. Production rollout enables both new switches only after deployed media service, mapping and supported-client playback validation.

## Verification and review boundaries

RelayControl tests: all four feature disables; explicit false vs missing defaults; broadcast unaffected by DuplexAudio disable; audio sessions unaffected by ScreenShare disable; generic signaling/TURN/device authorization unaffected; media grants role/session binding, replay, expiration, source ownership and feature disable. Follow the attached restore/build/full-solution-test request for RelayControl after implementation.

Publisher focused tests: opt-in false has zero additional side effects; enabled upload receives encoded samples; socket failure/slow sink leaves RTC working; memory ownership, bounded queues, generation/reconnect cancellation and stop cleanup; simultaneous AV1 RTC plus H.264 Activity output without shared-codec downgrade.

Forwarder tests: one publisher per session; multiple viewers; rejected grants; lease expiry/revocation; bounded message assembly and slow-viewer cleanup; keyframe recovery; same protocol fixtures as desktop and Activity.

Activity tests: binary parsing and invalid input, configuration capability checks, decoder lifecycle, audio/video timestamps, bounded backlog, generation changes and cleanup. Browser test with fixture media verifies actual canvas frames and audible decoded audio. Live Discord test separately verifies proxy/mapping, authorization, late join, reconnection, synchronized playback and concurrent existing RTC viewer.

No tests/build are claimed for this design-only artifact. Product implementation follows reviewed specification and implementation plan. Deliver repository-specific diffs, build/test evidence and explicit live-validation limitations.
