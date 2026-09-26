using System.Net.Http.Json;

namespace SonicDesktopRelay.ApiClient;

public sealed record ConsumeShareIntentRequest(string Token);
public sealed record ConsumeShareIntentResponse(Guid Id, DateTimeOffset ExpiresAt);
public sealed record CompleteShareIntentRequest(Guid SessionId);
public sealed record ResolveWatchIntentRequest(string Token);
public sealed record ResolveWatchIntentResponse(Guid SessionId);

public sealed class LaunchIntentApiClient(HttpClient http)
{
    public async Task<ConsumeShareIntentResponse> ConsumeShareAsync(string token, CancellationToken ct)
    {
        var response = await http.PostAsJsonAsync("/api/launch-intents/share/consume",
            new ConsumeShareIntentRequest(token), ct);
        return await ApiResponse.ReadAsync<ConsumeShareIntentResponse>(response, ct);
    }

    public async Task CompleteShareAsync(Guid intentId, Guid sessionId, CancellationToken ct)
    {
        var response = await http.PostAsJsonAsync($"/api/launch-intents/share/{intentId}/complete",
            new CompleteShareIntentRequest(sessionId), ct);
        await ApiResponse.EnsureSuccessAsync(response, ct);
    }

    public async Task<Guid> ResolveWatchAsync(string token, CancellationToken ct)
    {
        var response = await http.PostAsJsonAsync("/api/launch-intents/watch/resolve",
            new ResolveWatchIntentRequest(token), ct);
        return (await ApiResponse.ReadAsync<ResolveWatchIntentResponse>(response, ct)).SessionId;
    }
}
