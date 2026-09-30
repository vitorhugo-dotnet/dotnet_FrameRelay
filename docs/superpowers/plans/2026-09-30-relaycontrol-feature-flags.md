# RelayControl Feature Flags Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add independent kill switches while preserving every existing feature when configuration is absent.

**Architecture:** Register Microsoft's feature manager over existing .NET configuration and gate only optional feature entry points. Background public-room work checks the same feature before starting. Core authorization, cleanup, signaling and TURN remain operational.

**Tech Stack:** .NET 10, ASP.NET Core Minimal APIs, Microsoft.FeatureManagement 4.7.0, EF Core, xUnit, Docker Compose.

**Spec:** [Approved design](../specs/2026-09-30-discord-websocket-media-design.md). This plan delivers existing-feature flags independently; the companion media plan adds the transport-specific switch.

## Global Constraints

- Repository: `vitorhugo-dotnet/dotnet_RelayControl`; inspected main `8c8ad7a`. Fetch again before execution and preserve dirty original checkouts.
- Missing PublicRooms, DiscordActivity, DuplexAudio and ScreenShare flags mean true. Explicit false always wins.
- Use FeatureManagement, AddFeatureManagement(), IVariantFeatureManager and centralized RelayFeatures; no alternate flag framework or Azure configuration service.
- Do not flag device identity, authentication/authorization, retention, PostgreSQL, Redis, TURN or generic signaling.
- Honor existing PublicRoom:Enabled in addition to PublicRooms. Docker environment changes require container recreation.
- Before editing existing code, use targeted CodeGraph symbol/edit-context/impact/test queries; index only files needed. Run targeted tests per task; the user expressly requested full RelayControl solution verification at delivery.
- No unrelated refactoring or dependency updates. Add only the requested Microsoft package. Package source: https://www.nuget.org/packages/Microsoft.FeatureManagement/4.7.0 ; API reference: https://learn.microsoft.com/en-us/azure/azure-app-configuration/feature-management-dotnet-reference

## Review Focus

1. Explicit Docker false must override enabled defaults; Task 1 configuration tests.
2. Disabled public-room work must not seed a session or connect to signaling; Task 2 background tests.
3. Shared Discord launch routes must retain unrelated desktop recovery/completion behavior; Task 3 branch-specific tests.
4. Both code-based and paired screen-share joins must reject new admission, while existing sessions can end; Task 4 tests.
5. A disabled duplex feature must leave normal broadcast and its existing permission behavior intact; Task 4 tests.

## File Map

Create `services/SonicRelay.Api/Features/RelayFeatures.cs` (names/defaults registration) and `FeatureEndpointFilter.cs` (HTTP boundary using the official manager, not a custom evaluator). Modify API `Program.cs`, `SonicRelay.Api.csproj`, feature endpoints and `PublicRoomPublisherService.cs`. Create `tests/SonicRelay.Api.IntegrationTests/FeatureFlagConfigurationTests.cs` and `FeatureFlagEndpointsTests.cs`; extend existing public-room publisher tests. Update `infra/.env.example`, `infra/.env.prod.example`, `infra/compose.yml`, `infra/compose.prod.yml`, `deploy/docker-compose.prod.yml` and README deployment guidance only where each Compose path requires propagation. No root `.env.example` exists in the inspected API revision.

### Task 1: Official manager and backwards-compatible defaults

**Files:** `RelayFeatures.cs`, `FeatureEndpointFilter.cs`, `Program.cs`, API csproj and `FeatureFlagConfigurationTests.cs`.
**Interfaces:** `RelayFeatures.PublicRooms/DiscordActivity/DuplexAudio/ScreenShare` string constants; `RelayFeatures.AddDefaults(ConfigurationManager configuration)`; `FeatureEndpointFilter(string featureName) : IEndpointFilter`.

- [ ] Write tests `Missing_flags_are_enabled`, `Explicit_false_overrides_defaults` and `Authentication_is_not_disabled_by_feature_flags`. Resolve IVariantFeatureManager from SonicRelayApiFactory; assert `await manager.IsEnabledAsync(RelayFeatures.ScreenShare)` is true without configuration and false with `FeatureManagement:ScreenShare=false`. Anonymous protected requests still return 401.
- [ ] Run `dotnet test tests/SonicRelay.Api.IntegrationTests/SonicRelay.Api.IntegrationTests.csproj --filter FullyQualifiedName~FeatureFlagConfigurationTests`; expect missing manager/names before implementation.
- [ ] Add Microsoft.FeatureManagement 4.7.0. Supply an in-memory configuration source at the lowest precedence with four true defaults; do not append true defaults above env/test providers. Register `builder.Services.AddFeatureManagement();`. Implement the filter using `IsEnabledAsync`; disabled feature endpoints return 404 and do not invoke their handler.
- [ ] Re-run the focused test command; expect all configuration assertions passing. Commit only these files: `feat: register RelayControl feature flags with compatible defaults`.

### Task 2: Public room endpoint and background boundary

**Files:** `Endpoints/PublicRoomEndpoints.cs`, `Services/PublicRoomPublisherService.cs`, `FeatureFlagEndpointsTests.cs`, `PublicRoomPublisherServiceTests.cs`.
**Interfaces:** Task 1 manager/filter; preserve `PublicRoomSeeder.EnsureSeededAsync(AppDbContext, TimeProvider, CancellationToken)`.

- [ ] Write `PublicRooms_false_hides_endpoint_without_seeding` (authenticated GET `/api/public-room` is 404, no public session created) and `PublicRooms_false_starts_no_publisher_work` (PublicRoom:Enabled=true but no seeding, signaling connect or playlist reads). Also assert missing feature flag preserves existing PublicRoom:Enabled=false response.
- [ ] Run API integration tests filtered to `FeatureFlagEndpointsTests|PublicRoomPublisherServiceTests`; verify new disabled tests fail.
- [ ] Add the feature filter to public-room HTTP routes. Check the official feature manager before public-room background work and each new RunOnce attempt. Preserve existing options and cancellation/error recovery. Do not change PublicRoomSeeder's general signature or gate cleanup.
- [ ] Re-run that filter; expect new and existing public-room tests pass. Commit: `feat: gate public room HTTP and publisher work`.

### Task 3: Discord Activity boundaries, including shared launch flows

**Files:** `Endpoints/DiscordActivityEndpoints.cs`, `Endpoints/DiscordActivityLaunchIntentEndpoints.cs`, `Endpoints/LaunchIntentEndpoints.cs` only at shared Discord branches, `FeatureFlagEndpointsTests.cs`, `LaunchIntentActivityTests.cs` and `LaunchIntentTests.cs`.
**Interfaces:** Task 1 filter on Activity endpoints; manager injection for mixed handlers, retaining their existing public request types.

- [ ] Write `DiscordActivity_false_disables_authorization_and_activity_launch` asserting 404 for `/api/discord/activity/authorize`, viewer-grants/redeem and bot Activity launch routes with their proper authentication. Write `Discord_disable_preserves_existing_session_signaling_and_desktop_completion` and a test for desktop watch fallback from a ready announcement.
- [ ] Run `dotnet test tests/SonicRelay.Api.IntegrationTests/SonicRelay.Api.IntegrationTests.csproj --filter "FullyQualifiedName~FeatureFlagEndpointsTests|FullyQualifiedName~LaunchIntentActivityTests|FullyQualifiedName~LaunchIntentTests"`; expect the new disable assertions to fail.
- [ ] Apply the official-manager filter to Activity-only routes. For `/redeem`, `/bind`, `/watch` and generic launch-intent routes, identify actual Discord-only behavior and gate that branch rather than removing unrelated desktop handlers. Retain auth/rate limiting, revoked-capability cleanup and session-end operations.
- [ ] Re-run the same focused tests; expect existing desktop launch tests and new disabled Activity tests pass. Commit: `feat: gate Discord Activity launch and admission boundaries`.

### Task 4: Session mode boundaries and operational integration

**Files:** `Endpoints/SessionEndpoints.cs`, `Endpoints/DiscordActivityEndpoints.cs` screen-share admission branch, `FeatureFlagEndpointsTests.cs`, existing `DuplexAudioTests.cs` and `ScreenShareSessionTests.cs`; configuration/deployment files in File Map and README.
**Interfaces:** Keep existing session request/response signatures; inject IVariantFeatureManager into CreateAsync and shared join/admission handlers. Error convention: 409 with `{ error: "The requested feature is disabled.", code: "feature_disabled", feature: RelayFeatures.<name> }` for disabled session behavior, distinct from hidden optional HTTP endpoints.

- [ ] Write `Duplex_false_rejects_duplex_and_preserves_broadcast`: authenticated session create `mode=duplex` returns 409/feature_disabled while `mode=broadcast` returns 200. Assert no duplex row or participant is inserted for rejection.
- [ ] Write `ScreenShare_false_rejects_create_and_both_join_paths`: disabled create returns 409; pre-existing screen_share code join and paired ID join reject without consuming capacity. Activity admission rejects too. Broadcast/audio create/join and ending existing screen-share sessions still work.
- [ ] Inspect StreamSession.Mode: it is immutable and the API has no mode transition endpoint. Do not invent one or gate ordinary broadcast permission behavior as though it were a duplex transition. Test existing audio-permission and TURN/signaling routes remain available for permitted normal sessions.
- [ ] Run `dotnet test tests/SonicRelay.Api.IntegrationTests/SonicRelay.Api.IntegrationTests.csproj --filter "FullyQualifiedName~FeatureFlag|FullyQualifiedName~DuplexAudioTests|FullyQualifiedName~ScreenShareSessionTests|FullyQualifiedName~WebRtcEndpointsTests|FullyQualifiedName~SignalingGrantEndpointsTests"`; verify new disabled assertions fail, then add the guards before create/admission mutations and re-run to pass.
- [ ] Add four true env examples and ensure each supported Compose invocation passes explicit false into API configuration (env_file on deployment path; `${FeatureManagement__<name>:-true}` environment entries where the infra path needs them). Document Docker recreation, defaults and disabled responses. Validate with `docker compose ... config --quiet` using a temporary nonsecret test env; do not dump resolved secret values.
- [ ] Run the user's requested API delivery checks: `dotnet restore SonicRelay.sln`, `dotnet build SonicRelay.sln --no-restore`, `dotnet test SonicRelay.sln --no-build --logger "console;verbosity=minimal"`. Capture actual failures/hangs with bounded waits; no success claim before completion.
- [ ] Commit: `feat: gate duplex and screen share with documented Docker switches`. Review diff for generic infrastructure changes. Report modified files, all gated boundaries, test results and compatibility limits. Do not deploy without the authorized delivery scope.

## Handoff

Review this plan together with the companion media plan before implementation. Execution may be native in this chat or subagent-driven under the user's model whitelist. No delegation is authorized merely by this document's standard header. Prefer native execution because transport/security changes share contracts across repositories.
