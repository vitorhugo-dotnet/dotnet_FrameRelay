using System.Net;
using Xunit;

namespace SonicDesktopRelay.ApiClient.Tests;

public sealed class MediaRelayApiClientTests
{
    [Theory]
    [InlineData("/media/ws/media", "https://relay.example/media/ws/media")]
    [InlineData("wss://media.example/ws/media", "wss://media.example/ws/media")]
    public async Task Upload_grant_uses_authenticated_session_route_and_preserves_media_url(string url, string expected)
    {
        var sessionId = Guid.NewGuid();
        var handler = new StubHttpMessageHandler().Respond(HttpStatusCode.OK,
            System.Text.Json.JsonSerializer.Serialize(new { admissionId = Guid.NewGuid(), sessionId,
                participantId = Guid.NewGuid(), grant = "grant", expiresAt = DateTimeOffset.UtcNow.AddSeconds(30), mediaUrl = url }));
        using var http = new HttpClient(handler) { BaseAddress = new("https://relay.example/") };
        http.DefaultRequestHeaders.Authorization = new("Bearer", "device-test");
        var result = await new MediaRelayApiClient(http).CreateUploadGrantAsync(sessionId, CancellationToken.None);
        Assert.Equal($"/api/sessions/{sessionId}/media-grants", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal("device-test", handler.Requests[0].Headers.Authorization!.Parameter);
        Assert.Equal(expected, result.MediaUrl);
    }
}
