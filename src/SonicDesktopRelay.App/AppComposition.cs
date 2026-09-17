using System.Net;
using System.Runtime.Versioning;
using SonicDesktopRelay.ApiClient;
using SonicDesktopRelay.Core;
using SonicDesktopRelay.Core.Identity;
using SonicDesktopRelay.Media;
using SonicDesktopRelay.Media.Windows;
using SonicDesktopRelay.Presentation;
using SonicDesktopRelay.Signaling;

namespace SonicDesktopRelay.App;

/// <summary>
/// The one place that knows about concrete implementations. Everything below this file talks
/// to interfaces, which is what lets the whole app be tested without Windows or a network.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class AppComposition
{
    /// <param name="deviceName">
    /// The name this machine registers under. It reaches the backend only at bootstrap: the
    /// device API has no rename route, so on every later run the stored identity already
    /// carries whatever name the first run sent.
    /// </param>
    public AppComposition(BackendSettings settings, string deviceName)
    {
        Settings = settings;
        var store = new FileDeviceCredentialStore(FileDeviceCredentialStore.DefaultPath);
        var deviceHttp = new HttpClient { BaseAddress = settings.BaseAddress };
        var deviceApi = new DeviceApiClient(deviceHttp);
        Identity = new DeviceIdentityService(store, deviceApi, TimeProvider.System);

        var sessionHttp = new HttpClient(new BearerTokenHandler(Identity, deviceName))
        {
            BaseAddress = settings.BaseAddress
        };

        // Both media roles need the very connection the runtime is using — offers, answers and
        // ICE all travel over the same socket — so the factory hands out the decorated current
        // connection and every outbound frame is observed at one boundary.
        var signalingDiagnostics = new SignalingDiagnosticBuffer();
        SessionRuntime? runtime = null;
        ISignalingConnection? current = null;
        var iceApi = new IceApiClient(sessionHttp);
        PublishHost = new RtcVideoPublishHost(iceApi, () => current);
        WatchHost = new RtcVideoWatchHost(iceApi, () => current);

        Runtime = new SessionRuntime(
            new SessionApiAdapter(new SessionApiClient(sessionHttp)),
            () =>
            {
                var connection = new SignalingConnection(
                    new ClientWebSocketAdapter(),
                    settings,
                    ct => Identity.GetAccessTokenAsync(deviceName, ct));
                current = new DiagnosticSignalingConnection(
                    connection,
                    signalingDiagnostics,
                    () => runtime?.Snapshot.Phase ?? SessionPhase.Idle);
                return current;
            },
            PublishHost,
            WatchHost,
            signalingDiagnostics);
        runtime = Runtime;
    }

    public BackendSettings Settings { get; }

    public DeviceIdentityService Identity { get; }

    public SessionRuntime Runtime { get; }

    public RtcVideoPublishHost PublishHost { get; }

    public RtcVideoWatchHost WatchHost { get; }

    public IMonitorEnumerator Monitors { get; } = new MonitorEnumerator();
}

/// <summary>Attaches the DeviceBearer token to every call, refreshing it before it lapses.</summary>
internal sealed class BearerTokenHandler(DeviceIdentityService identity, string deviceName)
    : DelegatingHandler(new HttpClientHandler())
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await identity.GetAccessTokenAsync(deviceName, cancellationToken);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        var response = await base.SendAsync(request, cancellationToken);
        if (response.StatusCode != HttpStatusCode.Unauthorized) return response;

        // The proactive refresh missed — a clock skew or a credential rotation elsewhere.
        // One retry with a fresh token, then the failure is real.
        identity.Invalidate();
        var retryToken = await identity.GetAccessTokenAsync(deviceName, cancellationToken);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", retryToken);
        return await base.SendAsync(request, cancellationToken);
    }
}

/// <summary>Maps HTTP failures onto the presentation layer's own failure type.</summary>
internal sealed class SessionApiAdapter(SessionApiClient client) : ISessionApi
{
    public async Task<CreatedSession> CreateScreenShareAsync(int maxViewers, CancellationToken ct)
    {
        try
        {
            var session = await client.CreateScreenShareAsync(maxViewers, ct);
            return new CreatedSession(session.Id, session.Code
                ?? throw new SessionApiFailure("no_code", "The backend created a session without a code."));
        }
        catch (ApiException e)
        {
            throw Translate(e);
        }
    }

    public async Task<Guid> JoinAsync(string code, CancellationToken ct)
    {
        try
        {
            return (await client.JoinAsync(code, ct)).Id;
        }
        catch (ApiException e)
        {
            throw Translate(e);
        }
    }

    public async Task EndAsync(Guid sessionId, CancellationToken ct)
    {
        try
        {
            await client.EndAsync(sessionId, ct);
        }
        catch (ApiException e)
        {
            throw Translate(e);
        }
    }

    // The viewer-limit refusal is the one failure the API answers without a machine-readable
    // code, so the status is what names it.
    private static SessionApiFailure Translate(ApiException e) =>
        new(e.ErrorCode ?? (e.StatusCode == HttpStatusCode.Conflict ? "session_full" : "unknown"), e.Message);
}
