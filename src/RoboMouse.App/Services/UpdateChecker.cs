using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RoboMouse.App.Services;

/// <summary>A newer release than the running build.</summary>
public sealed record UpdateInfo(Version Version, string PageUrl);

/// <summary>
/// Asks GitHub for the latest RoboMouse release. Only for the direct-download builds: the Store keeps
/// its own copy up to date. It never downloads or runs anything; the user gets the release page.
/// </summary>
public sealed class UpdateChecker
{
    public const string LatestReleaseApi = "https://api.github.com/repos/timothydodd/RoboMouse/releases/latest";
    public const string ReleasesPage = "https://github.com/timothydodd/RoboMouse/releases/latest";

    /// <summary>How often the automatic check runs.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    private readonly HttpMessageHandler? _handler;

    public UpdateChecker(HttpMessageHandler? handler = null) => _handler = handler;

    /// <summary>The running build's version (major.minor.patch).</summary>
    public static Version CurrentVersion
    {
        get
        {
            var v = typeof(UpdateChecker).Assembly.GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }

    /// <summary>True when the last check is a day old or there has not been one.</summary>
    public static bool IsDue(DateTime? lastCheckUtc, DateTime nowUtc) =>
        lastCheckUtc is not { } last || nowUtc - last >= Interval || last > nowUtc;

    /// <summary>The newer release, or null when the running build is current. Throws on network or parse errors.</summary>
    public async Task<UpdateInfo?> CheckAsync(Version current, CancellationToken ct)
    {
        using var http = _handler == null ? new HttpClient() : new HttpClient(_handler, disposeHandler: false);
        http.Timeout = TimeSpan.FromSeconds(20);
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
        // GitHub refuses API requests without a User-Agent.
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("RoboMouse", current.ToString()));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsByteArrayAsync(ct);
        var release = JsonSerializer.Deserialize(json, UpdateJsonContext.Default.GitHubRelease);
        return release == null ? null : Evaluate(release, current);
    }

    /// <summary>
    /// Compares a release with the running version. Drafts and pre-releases are skipped, and the page
    /// opened is always on github.com whatever the response says.
    /// </summary>
    public static UpdateInfo? Evaluate(GitHubRelease release, Version current)
    {
        if (release.Draft || release.Prerelease || ParseTag(release.TagName) is not { } version || version <= current)
            return null;
        var page = release.HtmlUrl is { } url && url.StartsWith("https://github.com/timothydodd/RoboMouse/", StringComparison.OrdinalIgnoreCase)
            ? url
            : ReleasesPage;
        return new UpdateInfo(version, page);
    }

    /// <summary>"v1.2.3" or "1.2.3" to a three-part version; null for anything else.</summary>
    public static Version? ParseTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return null;
        var text = tag.Trim().TrimStart('v', 'V');
        var dash = text.IndexOf('-');
        if (dash >= 0)
            text = text[..dash];
        if (!Version.TryParse(text, out var v))
            return null;
        return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
    }
}

/// <summary>The fields of GitHub's release JSON that the check reads.</summary>
public sealed class GitHubRelease
{
    [JsonPropertyName("tag_name")] public string? TagName { get; set; }
    [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
    [JsonPropertyName("draft")] public bool Draft { get; set; }
    [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
}

[JsonSerializable(typeof(GitHubRelease))]
internal partial class UpdateJsonContext : JsonSerializerContext
{
}
