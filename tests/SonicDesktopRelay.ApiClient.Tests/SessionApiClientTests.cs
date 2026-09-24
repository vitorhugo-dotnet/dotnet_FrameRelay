using System.Net;
using Xunit;

namespace SonicDesktopRelay.ApiClient.Tests;

public sealed class SessionApiClientTests
{
    private const string CreatedBody = """
        {"id":"6f9619ff-8b86-d011-b42d-00cf4fc964ff","sourceDeviceId":"6f9619ff-8b86-d011-b42d-00cf4fc964fe",
         "status":"waiting","mode":"screen_share","maxViewers":3,
         "codeExpiresAt":"2026-08-23T13:00:00Z","code":"AB12CD"}
        """;

    [Fact]
    public async Task Create_requests_the_screen_share_mode_and_returns_the_code()
    {
        var handler = new StubHttpMessageHandler().Respond(HttpStatusCode.Created, CreatedBody);
        var client = new SessionApiClient(HttpClientFor(handler));

        var session = await client.CreateScreenShareAsync(3, CancellationToken.None);

        Assert.Equal("/api/sessions", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Contains("\"mode\":\"screen_share\"", handler.RequestBodies[0]);
        Assert.Contains("\"maxViewers\":3", handler.RequestBodies[0]);
        Assert.Equal("AB12CD", session.Code);
        Assert.Equal("screen_share", session.Mode);
    }

    [Fact]
    public async Task Join_uppercases_and_trims_the_code_before_sending_it()
    {
        var handler = new StubHttpMessageHandler().Respond(HttpStatusCode.OK, CreatedBody);
        var client = new SessionApiClient(HttpClientFor(handler));

        await client.JoinAsync("  ab12cd  ", CancellationToken.None);

        Assert.Equal("/api/sessions/join", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Contains("\"code\":\"AB12CD\"", handler.RequestBodies[0]);
    }

    [Fact]
    public async Task Join_by_id_uses_existing_authenticated_session_join_route()
    {
        var handler = new StubHttpMessageHandler().Respond(HttpStatusCode.OK, CreatedBody);
        var client = new SessionApiClient(HttpClientFor(handler));
        var sessionId = Guid.Parse("6f9619ff-8b86-d011-b42d-00cf4fc964ff");

        await client.JoinByIdAsync(sessionId, CancellationToken.None);

        Assert.Equal($"/api/sessions/{sessionId}/join", handler.Requests[0].RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task Join_surfaces_device_type_not_allowed_as_the_error_code()
    {
        var handler = new StubHttpMessageHandler().Respond(HttpStatusCode.Forbidden,
            """{"error":"This session type is not available for this device.","code":"device_type_not_allowed"}""");
        var client = new SessionApiClient(HttpClientFor(handler));

        var exception = await Assert.ThrowsAsync<ApiException>(
            () => client.JoinAsync("AB12CD", CancellationToken.None));

        Assert.Equal("device_type_not_allowed", exception.ErrorCode);
    }

    [Fact]
    public async Task Join_surfaces_invalid_code_as_the_error_code()
    {
        var handler = new StubHttpMessageHandler().Respond(HttpStatusCode.NotFound,
            """{"error":"Invalid or expired session code.","code":"invalid_code"}""");
        var client = new SessionApiClient(HttpClientFor(handler));

        var exception = await Assert.ThrowsAsync<ApiException>(
            () => client.JoinAsync("AB12CD", CancellationToken.None));

        Assert.Equal("invalid_code", exception.ErrorCode);
    }

    [Fact]
    public async Task Participants_are_returned_with_their_roles()
    {
        var handler = new StubHttpMessageHandler().Respond(HttpStatusCode.OK,
            """
            {"sessionId":"6f9619ff-8b86-d011-b42d-00cf4fc964ff","mode":"screen_share",
             "participants":[
               {"participantId":"6f9619ff-8b86-d011-b42d-00cf4fc96401","role":"publisher","status":"connected","isSelf":true},
               {"participantId":"6f9619ff-8b86-d011-b42d-00cf4fc96402","role":"viewer","status":"connected","isSelf":false}]}
            """);
        var client = new SessionApiClient(HttpClientFor(handler));

        var participants = await client.GetParticipantsAsync(
            Guid.Parse("6f9619ff-8b86-d011-b42d-00cf4fc964ff"), CancellationToken.None);

        Assert.Equal(2, participants.Participants.Length);
        Assert.Single(participants.Participants, p => p.Role == "publisher" && p.IsSelf);
    }

    [Fact]
    public async Task Ending_a_session_posts_to_the_end_route()
    {
        var handler = new StubHttpMessageHandler().Respond(HttpStatusCode.OK, CreatedBody);
        var client = new SessionApiClient(HttpClientFor(handler));
        var sessionId = Guid.Parse("6f9619ff-8b86-d011-b42d-00cf4fc964ff");

        await client.EndAsync(sessionId, CancellationToken.None);

        Assert.Equal($"/api/sessions/{sessionId}/end", handler.Requests[0].RequestUri!.AbsolutePath);
    }

    private static HttpClient HttpClientFor(StubHttpMessageHandler handler) =>
        new(handler) { BaseAddress = new Uri("https://relay.example.com") };
}
