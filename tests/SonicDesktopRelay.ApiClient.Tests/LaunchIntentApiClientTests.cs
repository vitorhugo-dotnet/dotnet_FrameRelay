using System.Net;
using Xunit;

namespace SonicDesktopRelay.ApiClient.Tests;

public sealed class LaunchIntentApiClientTests
{
    [Fact]
    public async Task Redeems_and_binds_with_the_documented_device_contract()
    {
        var id = Guid.NewGuid();
        var session = Guid.NewGuid();
        var handler = new StubHttpMessageHandler()
            .Respond(HttpStatusCode.OK, $$"""{"id":"{{id}}","kind":"share","code":null,"sessionId":null}""")
            .Respond(HttpStatusCode.NoContent, "");
        var client = new LaunchIntentApiClient(new HttpClient(handler) { BaseAddress = new Uri("https://relay.test") });
        var intent = await client.RedeemAsync(new string('a', 64), CancellationToken.None);
        await client.BindAsync(intent.Id, session, CancellationToken.None);
        Assert.Equal("share", intent.Kind);
        Assert.Equal("/api/launch-intents/redeem", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Contains("\"token\":", handler.RequestBodies[0]);
        Assert.Equal($"/api/launch-intents/{id}/bind", handler.Requests[1].RequestUri!.AbsolutePath);
        Assert.Contains(session.ToString(), handler.RequestBodies[1]);
        Assert.All(handler.Requests, request => Assert.Null(request.Headers.Authorization));
    }

    [Fact]
    public void Watch_prefers_code_and_supports_session_id_fallback()
    {
        var id = Guid.NewGuid();
        var session = Guid.NewGuid();
        Assert.Equal("AB12CD", new RedeemedLaunchIntent(id, "watch", "AB12CD", session).WatchTarget);
        Assert.Equal(session.ToString(), new RedeemedLaunchIntent(id, "watch", null, session).WatchTarget);
        Assert.Throws<InvalidOperationException>(() => new RedeemedLaunchIntent(id, "watch", null, null).WatchTarget);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("{\"id\":\"6f9619ff-8b86-d011-b42d-00cf4fc964ff\",\"kind\":\"host\"}")]
    public async Task Rejects_invalid_intent_before_starting_a_session(string body)
    {
        var handler = new StubHttpMessageHandler().Respond(HttpStatusCode.OK, body);
        var client = new LaunchIntentApiClient(new HttpClient(handler) { BaseAddress = new Uri("https://relay.test") });
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.RedeemAsync("unused", CancellationToken.None));
    }

    [Fact]
    public async Task Session_id_watch_joins_existing_authenticated_route()
    {
        var id = Guid.NewGuid();
        var handler = new StubHttpMessageHandler().Respond(HttpStatusCode.OK, $$"""{"id":"{{id}}"}""");
        var client = new SessionApiClient(new HttpClient(handler) { BaseAddress = new Uri("https://relay.test") });
        Assert.Equal(id, (await client.JoinByIdAsync(id, CancellationToken.None)).Id);
        Assert.Equal($"/api/sessions/{id}/join", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Empty(handler.RequestBodies[0]);
    }
}
