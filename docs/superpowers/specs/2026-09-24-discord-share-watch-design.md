# Discord Share and Watch Integration Design

**Issue:** [#3 — Go + DisGo bot for one-click FrameRelay share/watch](https://github.com/vitorhugo-dotnet/dotnet_FrameRelay/issues/3)

## Goal

Let Discord users launch the existing FrameRelay desktop share and watch flows using short-lived HTTPS links, while keeping media, session ownership, identity, pairing, and viewer limits in the existing desktop and RelayControl systems.

## Components and boundaries

1. **RelayControl (`dotnet_SonicRelay`)** is the source of truth for persistent launch intents and watch capabilities. A dedicated bot credential can create intents, inspect/recover its pending share intents, and issue watch links. Desktop operations use the existing device bearer. A share intent is single-use and can be completed only by the device that consumed it, after that device creates its ordinary `screen_share` session. A watch capability is opaque, short-lived, session-scoped, and does not bypass the existing pairing, authorization, or viewer-capacity checks.
2. **FrameRelay (`dotnet_FrameRelay`)** accepts `framerelay://open/share/<token>` and `framerelay://open/watch/<token>` activation. It resolves intents through its authenticated device API, then uses the existing share/watch runtime. A local activation click is required before capture. A single-instance forwarder sends subsequent activations to the running app.
3. **Discord bot (`go_discord_FrameRelay`)** is a separate Go module and local Git repository in the parent folder. It uses Go 1.27+, a pinned DisGo release, no privileged Gateway intents, and no database. It registers `/framerelay share` and `/framerelay watch <code>`, polls/reconciles pending share intents through RelayControl, serves health/readiness endpoints, and runs in a non-root container.

## HTTP contract

- Bot service authentication is a dedicated rotatable secret with only launch-intent permissions. It is never logged or returned. Desktop endpoints require the existing device bearer.
- `POST /api/launch-intents/share` creates an expiring intent and returns an opaque public HTTPS launch URL. Correlation data is limited to the Discord guild, channel, and requesting user IDs.
- Desktop consume is atomic and single-use. Completion accepts the resulting session ID only when the authenticated device is that session's source device. Replay and cross-device completion fail.
- Bot list/status operations let a restarted bot recover unresolved intents and discover ready sessions. Ready state yields an opaque watch launch URL.
- `POST /api/launch-intents/watch` normalizes a supplied code using trim + uppercase, validates the active session and creates an opaque capability. Desktop resolution returns only the data needed to join through its normal authenticated session API; pairing and max-viewer enforcement remain authoritative.
- HTTPS `/open/share/{token}` and `/open/watch/{token}` pages attempt the corresponding custom URI and show installation guidance if activation is unavailable. Public URLs contain no device credentials, session codes, or reusable service credentials.

## Desktop activation

The executable parses protocol activation at startup. If another instance owns the per-user activation endpoint, the new process forwards the URI and exits. The active instance foregrounds its window and dispatches activation on the UI thread. Share activation consumes the intent before navigating to Share and invokes the current capture/session pipeline only after the user's local activation; watch activation resolves the capability and runs the existing join/WebRTC flow. Missing capture selection leaves the user on Share for a local choice. No parallel media or signaling stack is introduced.

## Safety and recovery

- Share intents have a short default lifetime (10 minutes), are consumed once, and cannot create a session by themselves.
- Watch capabilities are bounded by their session lifetime and existing join authorization/capacity rules.
- Tokens are stored hashed server-side and redacted from logs. The bot never receives device credentials or access JWTs.
- The backend retains enough bot correlation state to recover unresolved work after process restart; the bot uses no database.
- Discord publication is public only after the publisher session is ready. Initial launch responses and errors are ephemeral.
- Bot readiness requires Discord Gateway and RelayControl availability. Shutdown cancels watchers, closes Gateway, and stops HTTP cleanly.

## Delivery boundaries

Changes are maintained on independent branches and pull requests for FrameRelay and RelayControl. The Go module is developed as a separate local repository until its remote repository is specified. Existing WebRTC, signaling, TURN, capture, and device identity behavior remains the implementation used by these flows.
