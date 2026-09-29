using System.Net;
using System.Text;
using SonicDesktopRelay.Core.Updates;

namespace SonicDesktopRelay.Core.Tests;

public sealed class ReleaseUpdateCheckerTests
{
    [Fact]
    public async Task Resolves_repository_by_numeric_id_and_uses_current_name_for_release_lookup()
    {
        var handler = new QueueHandler(
            Json(HttpStatusCode.OK, """{"id":1344024708,"node_id":"R_kgDOUBwwhA","name":"FrameRelay","full_name":"new-owner/new-name","owner":{"id":65777252,"node_id":"MDQ6VXNlcjY1Nzc3MjUy","login":"new-owner"}}"""),
            Json(HttpStatusCode.OK, ReleaseListJson("""{"tag_name":"v1.2.0","draft":false,"prerelease":false,"html_url":"https://github.com/new-owner/new-name/releases/tag/v1.2.0","assets":[{"name":"FrameRelay-win-x64-v1.2.0.msi","browser_download_url":"https://example.test/installer.msi"}]}""")));
        await using var checker = new ReleaseUpdateChecker(new HttpClient(handler), "1.0.0");

        var result = await checker.CheckAsync();

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.Equal("1.2.0", result.AvailableVersion);
        Assert.Equal("https://example.test/installer.msi", result.DownloadUri?.ToString());
        Assert.Equal("https://api.github.com/repos/new-owner/new-name/releases?per_page=100", handler.Requests[1].RequestUri!.ToString());
    }

    [Fact]
    public async Task Development_marker_is_skipped_in_favor_of_latest_stable_semver_release()
    {
        var releases = ReleaseListJson(
            """{"tag_name":"dev-285","draft":false,"prerelease":false,"html_url":"https://github.com/releases/tag/dev-285"}""",
            """{"tag_name":"v1.2.0","draft":false,"prerelease":false,"html_url":"https://github.com/releases/tag/v1.2.0"}""");
        await using var checker = CreateChecker("1.0.0", releases);

        var result = await checker.CheckAsync();

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.Equal("1.2.0", result.AvailableVersion);
    }

    [Fact]
    public async Task Stable_latest_release_is_compared_as_semver_and_current_version_is_reported()
    {
        await using var checker = CreateChecker("2.0.0", ReleaseJson("v2.0.0"));
        Assert.Equal(UpdateCheckStatus.UpToDate, (await checker.CheckAsync()).Status);
    }

    [Fact]
    public async Task Stable_release_is_newer_than_an_installed_prerelease_with_the_same_core_version()
    {
        await using var checker = CreateChecker("1.0.0-alpha.10", ReleaseJson("v1.0.0"));

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, (await checker.CheckAsync()).Status);
    }

    [Fact]
    public async Task Malformed_release_tag_is_reported_as_invalid()
    {
        await using var checker = CreateChecker("1.0.0", ReleaseJson("v1.0"));

        Assert.Equal(UpdateCheckStatus.InvalidMetadata, (await checker.CheckAsync()).Status);
    }

    [Fact]
    public async Task Malformed_release_metadata_is_reported_as_invalid()
    {
        await using var checker = CreateChecker("1.0.0", "not json");
        Assert.Equal(UpdateCheckStatus.InvalidMetadata, (await checker.CheckAsync()).Status);
    }

    [Fact]
    public async Task Rate_limit_response_has_a_distinct_status()
    {
        var handler = new QueueHandler(
            Json(HttpStatusCode.OK, """{"id":1344024708,"full_name":"owner/repo","owner":{"id":65777252,"login":"owner"}}"""),
            new HttpResponseMessage(HttpStatusCode.Forbidden)
            { Headers = { { "X-RateLimit-Remaining", "0" } } });
        await using var checker = new ReleaseUpdateChecker(new HttpClient(handler), "1.0.0");

        Assert.Equal(UpdateCheckStatus.RateLimited, (await checker.CheckAsync()).Status);
    }

    [Fact]
    public async Task Network_failure_is_reported_without_throwing()
    {
        await using var checker = new ReleaseUpdateChecker(new HttpClient(new ThrowingHandler()), "1.0.0");

        Assert.Equal(UpdateCheckStatus.Offline, (await checker.CheckAsync()).Status);
    }

    [Fact]
    public async Task Start_checks_immediately_and_repeats_on_its_configured_interval()
    {
        var handler = new RepeatingHandler();
        await using var checker = new ReleaseUpdateChecker(new HttpClient(handler), "1.0.0",
            interval: TimeSpan.FromMilliseconds(40));

        checker.Start();
        var limit = DateTime.UtcNow.AddSeconds(2);
        while (handler.ReleaseRequests < 2 && DateTime.UtcNow < limit)
            await Task.Delay(10);

        Assert.True(handler.ReleaseRequests >= 2);
        Assert.Equal(UpdateCheckStatus.UpToDate, checker.Result.Status);
    }

    [Fact]
    public async Task Simultaneous_manual_checks_are_serialized()
    {
        var handler = new ConcurrencyHandler();
        await using var checker = new ReleaseUpdateChecker(new HttpClient(handler), "1.0.0");

        await Task.WhenAll(checker.CheckAsync(), checker.CheckAsync());

        Assert.Equal(1, handler.MaximumConcurrentRequests);
        Assert.Equal(4, handler.TotalRequests);
    }

    private static ReleaseUpdateChecker CreateChecker(string installed, string release)
    {
        var handler = new QueueHandler(Json(HttpStatusCode.OK,
            """{"id":1344024708,"node_id":"R_kgDOUBwwhA","full_name":"vitorhugo-dotnet/dotnet_FrameRelay","owner":{"id":65777252,"node_id":"MDQ6VXNlcjY1Nzc3MjUy","login":"vitorhugo-dotnet"}}"""),
            Json(HttpStatusCode.OK, release));
        return new ReleaseUpdateChecker(new HttpClient(handler), installed);
    }

    private static string ReleaseJson(string tag) =>
        ReleaseListJson($$"""{"tag_name":"{{tag}}","draft":false,"prerelease":false,"html_url":"https://github.com/releases"}""");

    private static string ReleaseListJson(params string[] releases) => $"[{string.Join(',', releases)}]";

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class QueueHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);
        public List<HttpRequestMessage> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(_responses.Dequeue());
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("offline");
    }

    private sealed class RepeatingHandler : HttpMessageHandler
    {
        private int _releaseRequests;
        public int ReleaseRequests => Volatile.Read(ref _releaseRequests);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.RequestUri!.AbsolutePath.Contains("/repositories/", StringComparison.Ordinal)
                ? """{"id":1344024708,"full_name":"owner/repo","owner":{"id":65777252,"login":"owner"}}"""
                : ReleaseJson("v1.0.0");
            if (!request.RequestUri.AbsolutePath.Contains("/repositories/", StringComparison.Ordinal))
                Interlocked.Increment(ref _releaseRequests);
            return Task.FromResult(Json(HttpStatusCode.OK, body));
        }
    }

    private sealed class ConcurrencyHandler : HttpMessageHandler
    {
        private int _active;
        private int _maximum;
        private int _total;
        public int MaximumConcurrentRequests => Volatile.Read(ref _maximum);
        public int TotalRequests => Volatile.Read(ref _total);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var active = Interlocked.Increment(ref _active);
            Interlocked.Increment(ref _total);
            int currentMaximum;
            while (active > (currentMaximum = Volatile.Read(ref _maximum)))
                if (Interlocked.CompareExchange(ref _maximum, active, currentMaximum) == currentMaximum) break;
            try
            {
                await Task.Delay(20, cancellationToken);
                var isRepository = request.RequestUri!.AbsolutePath.Contains("/repositories/", StringComparison.Ordinal);
                var body = isRepository
                    ? """{"id":1344024708,"full_name":"owner/repo","owner":{"id":65777252,"login":"owner"}}"""
                    : ReleaseJson("v1.0.0");
                return Json(HttpStatusCode.OK, body);
            }
            finally { Interlocked.Decrement(ref _active); }
        }
    }
}
