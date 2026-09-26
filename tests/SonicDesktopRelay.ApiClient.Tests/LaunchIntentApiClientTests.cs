using System.Net;
using Xunit;

namespace SonicDesktopRelay.ApiClient.Tests;

public sealed class LaunchIntentApiClientTests
{
    [Fact]
    public async Task Share_consume_and_complete_use_device_authenticated_launch_routes()
    {
        var handler = new StubHttpMessageHandler()
            .Respond(HttpStatusCode.OK, "{\"id\":\"6f9619ff-8b86-d011-b42d-00cf4fc964ff\",\"expiresAt\":\"2026-09-24T12:00:00Z\"}")
            .Respond(HttpStatusCode.NoContent, string.Empty);
        var client = new LaunchIntentApiClient(HttpClientFor(handler));
        var intentId = Guid.Parse("b06d9b89-4980-4163-92de-9ee77960c485");
        var sessionId = Guid.Parse("6f9619ff-8b86-d011-b42d-00cf4fc964ff");

        var consumed = await client.ConsumeShareAsync("opaque-token", CancellationToken.None);
        await client.CompleteShareAsync(intentId, sessionId, CancellationToken.None);

        Assert.Equal("/api/launch-intents/share/consume", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Contains("opaque-token", handler.RequestBodies[0]);
        Assert.Equal($"/api/launch-intents/share/{intentId}/complete", handler.Requests[1].RequestUri!.AbsolutePath);
        Assert.Contains(sessionId.ToString(), handler.RequestBodies[1]);
        Assert.Equal(sessionId, consumed.Id);
    }

    [Fact]
    public async Task Watch_resolution_returns_the_session_id_without_a_code()
    {
        var handler = new StubHttpMessageHandler().Respond(HttpStatusCode.OK,
            "{\"sessionId\":\"6f9619ff-8b86-d011-b42d-00cf4fc964ff\"}");
        var client = new LaunchIntentApiClient(HttpClientFor(handler));

        var sessionId = await client.ResolveWatchAsync("opaque-watch-token", CancellationToken.None);

        Assert.Equal("/api/launch-intents/watch/resolve", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Contains("opaque-watch-token", handler.RequestBodies[0]);
        Assert.Equal(Guid.Parse("6f9619ff-8b86-d011-b42d-00cf4fc964ff"), sessionId);
    }

    private static HttpClient HttpClientFor(StubHttpMessageHandler handler) =>
        new(handler) { BaseAddress = new Uri("https://relay.example.com") };
}
