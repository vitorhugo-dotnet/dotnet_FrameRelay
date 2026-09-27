using System.Net.Http.Json;

namespace SonicDesktopRelay.ApiClient;

public sealed class SessionApiClient(HttpClient http)
{
    public async Task<SessionResponse> CreateScreenShareAsync(int maxViewers, CancellationToken ct)
    {
        var response = await http.PostAsJsonAsync("/api/sessions",
            new CreateSessionRequest(maxViewers, SessionModes.ScreenShare), ct);
        return await ApiResponse.ReadAsync<SessionResponse>(response, ct);
    }

    /// <summary>
    /// The backend trims and uppercases the code itself, but doing it here too means the app
    /// never sends what the user's keyboard happened to produce, and the request logged on a
    /// failure matches what was actually evaluated.
    /// </summary>
    public async Task<SessionResponse> JoinAsync(string code, CancellationToken ct)
    {
        var response = await http.PostAsJsonAsync("/api/sessions/join",
            new JoinSessionRequest(code.Trim().ToUpperInvariant()), ct);
        return await ApiResponse.ReadAsync<SessionResponse>(response, ct);
    }

    public async Task<ParticipantsResponse> GetParticipantsAsync(Guid sessionId, CancellationToken ct)
    {
        var response = await http.GetAsync($"/api/sessions/{sessionId}/participants", ct);
        return await ApiResponse.ReadAsync<ParticipantsResponse>(response, ct);
    }

    public async Task<SessionResponse> JoinByIdAsync(Guid sessionId, CancellationToken ct)
    {
        using var response = await http.PostAsync($"/api/sessions/{sessionId}/join", content: null, ct);
        return await ApiResponse.ReadAsync<SessionResponse>(response, ct);
    }

    public async Task EndAsync(Guid sessionId, CancellationToken ct)
    {
        var response = await http.PostAsync($"/api/sessions/{sessionId}/end", content: null, ct);
        await ApiResponse.EnsureSuccessAsync(response, ct);
    }
}
