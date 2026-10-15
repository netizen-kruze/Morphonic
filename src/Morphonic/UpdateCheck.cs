using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Morphonic;

// Settings > "Check for updates" (the first row): one request to GitHub's
// "latest release" endpoint for the project's repository, made only when
// the user presses the button — never on a timer, never at start-up. A
// newer release is offered as "Update now" (Updater), which fetches this
// platform's file and the release's SHA256SUMS.txt from the same release.
public static class UpdateCheck
{
    // The GitHub repository the releases are published from.
    public const string Repo = "netizen-kruze/Morphonic";

    // Where the check goes. --update-feed <url> (a test hook) points it at
    // a local server serving a release document.
    public static string FeedUrl { get; set; } =
        Repo.Length > 0 ? $"https://api.github.com/repos/{Repo}/releases/latest" : "";

    public static bool Configured => FeedUrl.Length > 0;

    public sealed record Result(string Current, string Latest, bool Available, string Url,
        string Notes, string AssetName, long AssetSize, string Published,
        string AssetUrl = "", string SumsUrl = "");

    // The release asset for this platform: the plain build, or the offline
    // one when that is what is running (it carries the model files inside).
    public static string AssetSuffix => OperatingSystem.IsWindows() ? "-win-x64.zip" : "-linux-x64";
    public static bool RunningOffline => Rvc.OfflinePayload.Ids.Count > 0;

    public static async Task<Result> CheckAsync(Version current, CancellationToken ct)
    {
        if (!Configured) throw new InvalidOperationException("update checks are not configured in this build");
        using var req = new HttpRequestMessage(HttpMethod.Get, FeedUrl);
        req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        req.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var resp = await Rvc.Download.Http.SendAsync(req, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotFound)
            throw new HttpRequestException("no release has been published yet");
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"github.com answered HTTP {(int)resp.StatusCode}");
        var json = await resp.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        return Parse(json, current, AssetSuffix, RunningOffline, AllowedDownloadHosts());
    }

    // Pure: the release document against the running version.
    // Downloads come from github.com (which redirects to its own storage);
    // a test feed (--update-feed) may also serve them from its own host.
    public static string[] AllowedDownloadHosts()
    {
        var hosts = new List<string> { "github.com" };
        if (Uri.TryCreate(FeedUrl, UriKind.Absolute, out var f) && f.Host != "api.github.com") hosts.Add(f.Host);
        return hosts.ToArray();
    }

    public static Result Parse(string json, Version current, string assetSuffix, bool offline = false, string[]? downloadHosts = null)
    {
        downloadHosts ??= new[] { "github.com" };
        string Checked(string? link) =>
            Uri.TryCreate(link, UriKind.Absolute, out var l) && downloadHosts.Contains(l.Host) &&
            (l.Scheme == "https" || (l.Scheme == "http" && l.IsLoopback)) ? l.AbsoluteUri : "";
        JObject doc;
        try { doc = JObject.Parse(json); }
        catch (Exception ex) { throw new FormatException("the release document is not valid JSON: " + ex.Message); }

        var tag = doc["tag_name"]?.ToString() ?? "";
        var latest = ParseVersion(tag) ?? throw new FormatException($"the release tag \"{tag}\" is not a version number");
        var cur = Normalize(current);
        var url = doc["html_url"]?.ToString() ?? "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != "https" || u.Host != "github.com") url = "";

        string assetName = "", assetUrl = "", sumsUrl = "";
        long assetSize = 0;
        var suffix = offline ? assetSuffix.Replace(".zip", "") + "-offline" + (assetSuffix.EndsWith(".zip") ? ".zip" : "") : assetSuffix;
        if (doc["assets"] is JArray assets)
        {
            var asset = assets.FirstOrDefault(a =>
            {
                var n = a["name"]?.ToString() ?? "";
                return n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && (offline || !n.Contains("-offline", StringComparison.OrdinalIgnoreCase));
            });
            if (asset != null)
            {
                assetName = asset["name"]?.ToString() ?? "";
                assetSize = asset["size"]?.Value<long>() ?? 0;
                assetUrl = Checked(asset["browser_download_url"]?.ToString());
            }
            var sums = assets.FirstOrDefault(a => string.Equals(a["name"]?.ToString(), "SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase));
            if (sums != null) sumsUrl = Checked(sums["browser_download_url"]?.ToString());
        }

        string published = "";
        if (DateTime.TryParse(doc["published_at"]?.ToString(), null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var when))
            published = when.ToString("yyyy-MM-dd");

        return new Result(cur.ToString(3), latest.ToString(3), latest > cur, url,
            doc["body"]?.ToString() ?? "", assetName, assetSize, published, assetUrl, sumsUrl);
    }

    // "v1.2.3", "1.2.3" or "1.2" -> 1.2.3 / 1.2.0; anything else -> null.
    public static Version? ParseVersion(string tag)
    {
        var s = tag.Trim();
        if (s.StartsWith('v') || s.StartsWith('V')) s = s[1..];
        if (!Version.TryParse(s, out var v)) return null;
        return Normalize(v);
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));
}
