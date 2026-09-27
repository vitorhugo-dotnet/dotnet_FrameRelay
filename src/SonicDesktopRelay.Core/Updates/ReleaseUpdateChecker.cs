using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SonicDesktopRelay.Core.Updates;

public enum UpdateCheckStatus
{
    Checking,
    UpToDate,
    UpdateAvailable,
    Offline,
    RateLimited,
    InvalidMetadata
}

public sealed record UpdateCheckResult(
    UpdateCheckStatus Status,
    string InstalledVersion,
    string? AvailableVersion = null,
    Uri? ReleaseUri = null,
    Uri? DownloadUri = null,
    string? Message = null);

/// <summary>Checks the latest stable FrameRelay release using its immutable GitHub repository ID.</summary>
public sealed class ReleaseUpdateChecker : IAsyncDisposable
{
    private const long RepositoryId = 1344024708;
    private const long OwnerId = 65777252;
    private const string FallbackFullName = "vitorhugo-dotnet/dotnet_FrameRelay";
    private static readonly Uri ApiRoot = new("https://api.github.com/");
    private static readonly Regex SemVer = new(@"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-([0-9A-Za-z.-]+))?(?:\+[0-9A-Za-z.-]+)?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _interval;
    private readonly SemaphoreSlim _checkLock = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly string _installedVersion;
    private Task? _worker;
    private UpdateCheckResult _result;

    public ReleaseUpdateChecker(HttpClient httpClient, string installedVersion,
        TimeProvider? timeProvider = null, TimeSpan? interval = null)
        : this(httpClient, installedVersion, timeProvider, interval, ownsClient: false) { }

    public ReleaseUpdateChecker(string installedVersion, TimeProvider? timeProvider = null,
        TimeSpan? interval = null)
        : this(CreateHttpClient(), installedVersion, timeProvider, interval, ownsClient: true) { }

    private ReleaseUpdateChecker(HttpClient httpClient, string installedVersion,
        TimeProvider? timeProvider, TimeSpan? interval, bool ownsClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _installedVersion = installedVersion ?? "0.0.0";
        _timeProvider = timeProvider ?? TimeProvider.System;
        _interval = interval ?? TimeSpan.FromHours(1);
        if (_interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
        _ownsClient = ownsClient;
        _result = new(UpdateCheckStatus.Checking, _installedVersion);
    }

    public event EventHandler<UpdateCheckResult>? ResultChanged;

    public UpdateCheckResult Result => _result;

    /// <summary>Starts an immediate asynchronous check followed by one check per interval.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_lifetime.IsCancellationRequested, this);
        _worker ??= RunAsync(_lifetime.Token);
    }

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_lifetime.IsCancellationRequested, this);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var ct = linkedCancellation.Token;
        await _checkLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Publish(new(UpdateCheckStatus.Checking, _installedVersion));
            var repositoryName = await ResolveRepositoryNameAsync(ct).ConfigureAwait(false)
                                 ?? FallbackFullName;
            using var response = await SendJsonAsync(
                new Uri(ApiRoot, $"repos/{repositoryName}/releases/latest"), ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return Publish(Failure(response.StatusCode, response));

            using var document = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false),
                cancellationToken: ct).ConfigureAwait(false);
            var root = document.RootElement;
            var tag = GetString(root, "tag_name");
            var releaseText = GetString(root, "html_url");
            if (root.GetPropertyOrDefault("draft") is not { ValueKind: JsonValueKind.False }
                || root.GetPropertyOrDefault("prerelease") is not { ValueKind: JsonValueKind.False }
                || !TryParseVersion(tag, out var available)
                || !TryParseVersion(_installedVersion, out var installed)
                || !Uri.TryCreate(releaseText, UriKind.Absolute, out var releaseUri)
                || releaseUri.Scheme != Uri.UriSchemeHttps)
                return Publish(new(UpdateCheckStatus.InvalidMetadata, _installedVersion,
                    Message: "GitHub returned unexpected release metadata."));

            var downloadUri = FindInstaller(root);
            if (Compare(available, installed) <= 0)
                return Publish(new(UpdateCheckStatus.UpToDate, _installedVersion, available.Text, releaseUri, downloadUri));
            return Publish(new(UpdateCheckStatus.UpdateAvailable, _installedVersion,
                available.Text, releaseUri, downloadUri));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Publish(new(UpdateCheckStatus.Offline, _installedVersion,
                Message: "GitHub did not respond in time. Try again later."));
        }
        catch (JsonException)
        {
            return Publish(new(UpdateCheckStatus.InvalidMetadata, _installedVersion,
                Message: "GitHub returned invalid release metadata."));
        }
        catch (HttpRequestException)
        {
            return Publish(new(UpdateCheckStatus.Offline, _installedVersion,
                Message: "Could not reach GitHub. Check your internet connection and try again."));
        }
        finally
        {
            _checkLock.Release();
        }
    }

    private async Task<string?> ResolveRepositoryNameAsync(CancellationToken ct)
    {
        try
        {
            using var response = await SendJsonAsync(new Uri(ApiRoot, $"repositories/{RepositoryId}"), ct)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            using var document = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct)
                .ConfigureAwait(false);
            var root = document.RootElement;
            var owner = root.GetPropertyOrDefault("owner");
            if (GetLong(root, "id") != RepositoryId || GetLong(owner, "id") != OwnerId) return null;
            var name = GetString(root, "full_name");
            return !string.IsNullOrWhiteSpace(name) && !name.Contains("..", StringComparison.Ordinal)
                ? name : null;
        }
        catch (HttpRequestException) { return null; }
        catch (JsonException) { return null; }
    }

    private async Task<HttpResponseMessage> SendJsonAsync(Uri uri, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("FrameRelay-UpdateChecker/1.0");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
    }

    private UpdateCheckResult Failure(HttpStatusCode status, HttpResponseMessage response) =>
        status == HttpStatusCode.TooManyRequests
        || (status == HttpStatusCode.Forbidden
            && ((response.Headers.TryGetValues("X-RateLimit-Remaining", out var values)
                 && values.Contains("0", StringComparer.Ordinal))
                || response.Headers.Contains("Retry-After")))
            ? new(UpdateCheckStatus.RateLimited, _installedVersion,
                Message: "GitHub is temporarily rate limiting update checks. Try again later.")
            : new(UpdateCheckStatus.Offline, _installedVersion,
                Message: "GitHub could not complete the update check. Try again later.");

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await CheckAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            try { await Task.Delay(_interval, _timeProvider, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        }
    }

    private UpdateCheckResult Publish(UpdateCheckResult result)
    {
        _result = result;
        ResultChanged?.Invoke(this, result);
        return result;
    }

    private static Uri? FindInstaller(JsonElement release)
    {
        if (release.GetPropertyOrDefault("assets") is not { ValueKind: JsonValueKind.Array } assets) return null;
        foreach (var asset in assets.EnumerateArray())
        {
            var name = GetString(asset, "name");
            var url = GetString(asset, "browser_download_url");
            if (name?.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) == true
                && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
                return uri;
        }
        return null;
    }

    private static string? GetString(JsonElement? element, string name) =>
        element.GetPropertyOrDefault(name) is { ValueKind: JsonValueKind.String } value
            ? value.GetString() : null;

    private static long GetLong(JsonElement? element, string name) =>
        element.GetPropertyOrDefault(name) is { ValueKind: JsonValueKind.Number } value
            && value.TryGetInt64(out var result) ? result : 0;

    private static bool TryParseVersion(string? text, out ParsedVersion version)
    {
        text = text?.Trim();
        if (text?.StartsWith('v') == true) text = text[1..];
        var match = text is null ? Match.Empty : SemVer.Match(text);
        if (!match.Success || !ValidPreRelease(match.Groups[4].Value)
            || !int.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out var major)
            || !int.TryParse(match.Groups[2].Value, CultureInfo.InvariantCulture, out var minor)
            || !int.TryParse(match.Groups[3].Value, CultureInfo.InvariantCulture, out var patch))
        {
            version = default;
            return false;
        }
        version = new(major, minor, patch, match.Groups[4].Value, text!);
        return true;
    }

    private static int Compare(ParsedVersion left, ParsedVersion right)
    {
        var core = left.Major.CompareTo(right.Major);
        if (core == 0) core = left.Minor.CompareTo(right.Minor);
        if (core == 0) core = left.Patch.CompareTo(right.Patch);
        if (core != 0) return core;
        if (left.PreRelease.Length == 0) return right.PreRelease.Length == 0 ? 0 : 1;
        if (right.PreRelease.Length == 0) return -1;
        var leftParts = left.PreRelease.Split('.');
        var rightParts = right.PreRelease.Split('.');
        for (var index = 0; index < Math.Min(leftParts.Length, rightParts.Length); index++)
        {
            var leftNumeric = leftParts[index].All(char.IsAsciiDigit);
            var rightNumeric = rightParts[index].All(char.IsAsciiDigit);
            int comparison;
            if (leftNumeric && rightNumeric)
                comparison = leftParts[index].Length != rightParts[index].Length
                    ? leftParts[index].Length.CompareTo(rightParts[index].Length)
                    : StringComparer.Ordinal.Compare(leftParts[index], rightParts[index]);
            else if (leftNumeric != rightNumeric)
                comparison = leftNumeric ? -1 : 1;
            else
                comparison = StringComparer.Ordinal.Compare(leftParts[index], rightParts[index]);
            if (comparison != 0) return comparison;
        }
        return leftParts.Length.CompareTo(rightParts.Length);
    }

    private static bool ValidPreRelease(string value) =>
        value.Length == 0 || value.Split('.').All(identifier => identifier.Length > 0
            && !(identifier.Length > 1 && identifier[0] == '0'
                 && identifier.All(char.IsAsciiDigit)));

    private static HttpClient CreateHttpClient() => new() { Timeout = TimeSpan.FromSeconds(20) };

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        if (_worker is not null)
        {
            try { await _worker.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        await _checkLock.WaitAsync().ConfigureAwait(false);
        _checkLock.Release();
        if (_ownsClient) _httpClient.Dispose();
        _lifetime.Dispose();
        _checkLock.Dispose();
    }

    private readonly record struct ParsedVersion(int Major, int Minor, int Patch, string PreRelease, string Text);
}

internal static class JsonElementExtensions
{
    public static JsonElement? GetPropertyOrDefault(this JsonElement element, string name) =>
        ((JsonElement?)element).GetPropertyOrDefault(name);

    public static JsonElement? GetPropertyOrDefault(this JsonElement? element, string name) =>
        element is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(name, out var property)
            ? property : null;
}
