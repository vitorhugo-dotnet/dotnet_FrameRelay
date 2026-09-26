using System.Net.Http.Json;
using System.Text.Json;

namespace SonicDesktopRelay.ApiClient;

public sealed record RedeemedLaunchIntent(Guid Id, string Kind, string? Code, Guid? SessionId)
{
    public string WatchTarget => !string.IsNullOrWhiteSpace(Code) ? Code
        : SessionId is { } id && id != Guid.Empty ? id.ToString()
        : throw new InvalidOperationException("The watch link has no session target.");
}

/// <summary>Uses the composition's DeviceBearer HTTP client, never a bot/service credential.</summary>
public sealed class LaunchIntentApiClient(HttpClient http)
{
    public async Task<RedeemedLaunchIntent> RedeemAsync(string token, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync("/api/launch-intents/redeem", new { token }, ct);
        RedeemedLaunchIntent intent;
        try { intent = await ApiResponse.ReadAsync<RedeemedLaunchIntent>(response, ct); }
        catch (JsonException)
        {
            // Do not preserve response details in an exception that might reach startup logging.
            throw new InvalidOperationException("The backend returned an invalid launch response.");
        }
        if (intent.Id == Guid.Empty || intent.Kind is not ("share" or "watch"))
            throw new InvalidOperationException("The backend returned an invalid launch intent.");
        if (intent.Kind == "watch") _ = intent.WatchTarget;
        return intent;
    }

    public async Task BindAsync(Guid intentId, Guid sessionId, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync($"/api/launch-intents/{intentId}/bind", new { sessionId }, ct);
        await ApiResponse.EnsureSuccessAsync(response, ct);
    }
}
