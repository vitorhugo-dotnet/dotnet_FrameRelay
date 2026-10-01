namespace SonicDesktopRelay.ApiClient;

public sealed record MediaAdmission(Guid AdmissionId, Guid SessionId, Guid ParticipantId, string Grant, DateTimeOffset ExpiresAt, string MediaUrl);
public sealed class MediaRelayApiClient(HttpClient http)
{
    public async Task<MediaAdmission> CreateUploadGrantAsync(Guid sessionId, CancellationToken ct)
    {
        using var response = await http.PostAsync($"/api/sessions/{sessionId}/media-grants", null, ct);
        var result = await ApiResponse.ReadAsync<MediaAdmission>(response, ct);
        var url = new Uri(http.BaseAddress!, result.MediaUrl);
        return result with { MediaUrl = url.AbsoluteUri };
    }
}
