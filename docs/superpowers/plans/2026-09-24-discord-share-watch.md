# Discord Share and Watch Integration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement the issue #3 Discord share/watch flow across FrameRelay, RelayControl, and a separate local Go bot repository.

**Architecture:** RelayControl persists opaque launch capabilities and enforces ownership, expiry, replay, pairing, and viewer limits. FrameRelay activates through a registered custom URI and forwards activations to one running process before calling its existing authenticated session runtime. A separate Go/DisGo service provides Discord commands and recovers pending work through the backend API.

**Tech Stack:** .NET 10, Avalonia, ASP.NET Core, EF Core/PostgreSQL, Go 1.27, `github.com/disgoorg/disgo`, Docker.

**Spec:** `docs/superpowers/specs/2026-09-24-discord-share-watch-design.md`; requirements originate in GitHub issue #3.

## Global Constraints

- The Go bot is a separate project/repository at `H:\Script\SonicRelay\go_discord_FrameRelay`, not a subfolder of either .NET repository.
- Use Go 1.27+ and pin a tagged `github.com/disgoorg/disgo` version.
- Do not request Message Content, presence, or other privileged Gateway intents; do not use voice APIs or self-bot behavior.
- Discord and RelayControl credentials come from environment/secret storage and are never logged or committed.
- Bot state is recovered from RelayControl; do not add a bot database.
- Share launch is short-lived and single-use; watch capabilities are session-scoped and cannot bypass pairing or viewer limits.
- Continue using the existing desktop authenticated identity, screen capture, session, WebRTC, signaling, and TURN paths.
- Public launch URLs use HTTPS and opaque tokens; never put session codes or reusable credentials in URLs.
- Normal automated tests use fakes/`httptest.Server`, never live Discord or RelayControl.

## Review Focus

1. Two concurrent requests consume the same publisher token: exactly one succeeds and at most one desktop session can be associated.
2. A different device completes an intent it did not consume: reject it without disclosing session or device details.
3. Expired/replayed share or ended/full/unpaired watch target: return a safe error and preserve existing authorization/capacity rules.
4. A second app process receives a launch URI while the first is starting or shutting down: forward once or fail cleanly without launching a competing media session.
5. Discord or RelayControl disconnect/restart during a pending share: readiness reflects the dependency and a restarted bot recovers without duplicate public messages.

## Component map

- **RelayControl:** `services/SonicRelay.Api/Authorization/*` for the launch service auth scheme/policies; `Endpoints/LaunchIntentEndpoints.cs`, `Contracts/LaunchIntentContracts.cs`, `Services/LaunchIntentService.cs`, and options for API behavior; `src/SonicRelay.Domain/...` for persistent entities; `src/SonicRelay.Infrastructure/Persistence/AppDbContext.cs` and a migration for storage; `Program.cs` for registration; API integration tests for persistence, authorization, lifecycle, and races; deploy config/docs for secret provisioning.
- **FrameRelay:** `src/SonicDesktopRelay.App/Program.cs`, `App.axaml.cs`, `Views/MainWindow.axaml.cs`, and focused protocol/single-instance/activation classes; `src/SonicDesktopRelay.ApiClient` for typed authenticated launch calls; `Shell.cs`/`AppComposition.cs` for dispatch into existing Share/Watch flows; app and API-client tests; `app.manifest` or installer/protocol registration artifact for the `framerelay` scheme; README for installation/activation behavior.
- **Go bot:** module root `H:\Script\SonicRelay\go_discord_FrameRelay`; `cmd/framerelay-bot/main.go`; `internal/config`, `internal/discord`, `internal/relaycontrol`, `internal/launch`, `internal/health`, and `internal/observability`; Dockerfile and CI workflow; package-local unit and HTTP tests.

## Tasks

### Task 1: RelayControl launch-intent persistence and contracts

**Files:**
- Create domain entity and constants under `src/SonicRelay.Domain/LaunchIntents/`.
- Modify `src/SonicRelay.Infrastructure/Persistence/AppDbContext.cs` and add the EF migration/snapshot update.
- Create `services/SonicRelay.Api/Contracts/LaunchIntentContracts.cs`, `Services/LaunchIntentOptions.cs`, and `Services/LaunchIntentService.cs`.
- Test in `tests/SonicRelay.Api.IntegrationTests/LaunchIntentTests.cs`.

**Interfaces:** Service operations create share, consume share, complete share, query/recover bot intents, create watch capability, and resolve watch capability. Share tokens are random opaque values; only a cryptographic hash is persisted. All state transitions use conditional database updates/transactions so single-use consumption is race-safe.

- [ ] Test that share intent creation stores correlation metadata and only a token hash, defaults expiry to 10 minutes, rejects invalid TTLs, and returns a token once.
- [ ] Test that concurrent consume attempts have exactly one winner and that replay, expiry, and completion by a different source device fail.
- [ ] Test that recovery returns only this integration's pending/ready intents and completion associates only a real session owned by the consuming device.
- [ ] Test watch token scope, expiry, multiple valid resolutions, normalization, and existing pairing/capacity enforcement through the normal join path.
- [ ] Implement entity, migration, service, and typed HTTP contracts to satisfy these tests.
- [ ] Add the bot-only scoped service authentication/policy and device-authenticated desktop endpoints; test missing, invalid, and insufficiently scoped credentials.
- [ ] Register endpoints and services in `Program.cs`, then update API/deployment documentation with secret setup and routes.

### Task 2: FrameRelay API client and activation model

**Files:**
- Create typed launch request/response/client contracts in `src/SonicDesktopRelay.ApiClient/`.
- Create protocol parsing, URI validation, activation forwarding, and single-instance coordination classes in `src/SonicDesktopRelay.App/`.
- Modify `Program.cs`, `App.axaml.cs`, `Views/MainWindow.axaml.cs`, `Shell.cs`, and `AppComposition.cs`.
- Add focused tests under `tests/SonicDesktopRelay.ApiClient.Tests/` and `tests/SonicDesktopRelay.Presentation.Tests/` (or a Windows-gated App test project if the existing test targets cannot load the app assembly).
- Modify `src/SonicDesktopRelay.App/app.manifest` or the existing packaging/installer files to register `framerelay://`.

**Interfaces:** Parse only `framerelay://open/share/{opaque-token}` and `framerelay://open/watch/{opaque-token}`. Activation dispatch calls authenticated typed API operations, foregrounds the current main window, then calls the existing Shell share/watch methods. A named per-user mutex and local named pipe serialize process ownership and forward URI messages.

- [ ] Test valid share/watch URIs, malformed URI rejection, empty/oversized token rejection, and unrelated command-line argument preservation.
- [ ] Test activation forwarding protocol framing, cancellation, duplicate delivery suppression, and receiver lifecycle without starting capture or WebRTC.
- [ ] Add API-client tests for consume, complete, resolve-watch routes, bearer auth, and safe upstream error mapping.
- [ ] Connect share activation to the existing `ShareAsync` flow and complete the consumed intent with the actual session ID after successful creation.
- [ ] Connect watch activation to the existing authenticated join flow; navigate to Share/Watch and foreground UI before dispatch.
- [ ] Register protocol activation and ensure capture begins only as a result of the user's local launch click; when source selection is missing, show Share without starting capture.
- [ ] Ensure a second process forwards activation to the existing instance and exits; test races during startup/shutdown.
- [ ] Document per-user protocol installation and fallback behavior.

### Task 3: Public HTTPS launch landing behavior

**Files:**
- Add `/open/share/{token}` and `/open/watch/{token}` public routes in RelayControl (or its existing public web host if source is found during implementation).
- Add route/response integration tests and short user-facing install guidance.

**Interfaces:** The landing page validates the capability without consuming it, attempts `framerelay://open/{mode}/{token}`, and presents install guidance if the protocol handler is unavailable. It never renders or redirects to session codes, bearer tokens, or service credentials.

- [ ] Test share/watch path handling, HTML escaping, malformed token handling, no-store/referrer policy headers, and install fallback content.
- [ ] Implement the minimal landing route using the project's hosting conventions and document the public HTTPS origin contract.

### Task 4: Go bot core and RelayControl client

**Files:**
- Initialize the standalone module at `H:\Script\SonicRelay\go_discord_FrameRelay` on local branch `codex/issue-3-discord-bot`.
- Create `go.mod`, `internal/config/config.go`, `internal/relaycontrol/{client,models}.go`, `internal/launch/watcher.go`, `internal/health/server.go`, and `internal/observability/logging.go` with package tests.

**Interfaces:** Config validates Discord token/application ID, RelayControl URL/service token, public base URL, address, log level, TTLs, and polling interval. The RelayControl client uses context-aware `net/http`, maps public error codes, and redacts authorization and capability tokens.

- [ ] Test fail-fast configuration, duration/default parsing, URL validation, and secret-safe errors.
- [ ] Test create/list/status/watch RelayControl calls with `httptest.Server`, including 401/403, 429/5xx, timeout, cancellation, malformed JSON, and URL escaping.
- [ ] Implement health/readiness handlers and context-aware pending-intent polling/recovery; test readiness transitions and cancellation.
- [ ] Implement JSON `slog` setup and tests that verify credential/token redaction.

### Task 5: DisGo adapter and command flows

**Files:**
- Create `cmd/framerelay-bot/main.go` and `internal/discord/{bot,commands,responses}.go` plus tests.
- Pin the chosen DisGo tag in `go.mod`/`go.sum`.

**Interfaces:** A small adapter owns DisGo types, registers/upserts `/framerelay share` and `/framerelay watch code`, and handles only the interactions needed. Share replies are ephemeral; successful readiness posts exactly one public watch button; watch command normalizes code and returns an ephemeral link or safe error.

- [ ] Test command definitions, normalization, ephemeral/public response shapes, link buttons, and failure mapping without a Gateway connection.
- [ ] Implement DisGo adapter with only minimum required intents and Gateway reconnect/resume lifecycle.
- [ ] Implement share create/respond and pending status publication/recovery with duplicate-message suppression based on persisted intent state.
- [ ] Implement watch command and clear ephemeral mapping for invalid/expired/full/unavailable cases.
- [ ] Implement startup validation/order, signal-driven graceful shutdown, and readiness only after Gateway plus RelayControl are usable.

### Task 6: Container, CI, documentation, and independent pull requests

**Files:**
- Create bot `Dockerfile`, CI workflow, `.dockerignore`, and README/config example with placeholders only.
- Update RelayControl deployment docs/config templates and FrameRelay user docs.

- [ ] Add multi-stage Go 1.27 Docker build with non-root runtime, healthcheck, read-only-compatible filesystem, and graceful SIGTERM.
- [ ] Add CI for `gofmt`, `go vet`, unit tests, race tests, build, and `govulncheck`; ensure no secrets or live dependencies are needed.
- [ ] Document manual Discord guild smoke test and public-domain/protocol installation setup.
- [ ] Review each repository's complete diff and prepare separate PRs for FrameRelay and RelayControl; leave the Go repo local until its remote is supplied.

## Self-review

- Spec coverage: intents, auth scopes, persistence/recovery, desktop single-instance activation, consent, watch flow, bot commands/Gateway, logging/health, deployment, CI, and manual smoke test are assigned above.
- Interface consistency: bot API client consumes the RelayControl create/list/status/watch routes; the desktop API client consumes authenticated share-consume/share-complete/watch-resolve routes; RelayControl binds a share completion to the consuming device and created source session.
- Remaining external dependency: the public domain/HTTPS host's deployment target must be confirmed in the relevant hosting repository during Task 3; the launch page itself can be served by RelayControl if no existing public web host is present.
