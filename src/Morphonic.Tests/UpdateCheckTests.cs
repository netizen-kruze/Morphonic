using Xunit;

namespace Morphonic.Tests;

// The update check's reading of GitHub's release document: which version
// counts as newer, which asset is offered, and what is refused.
public class UpdateCheckTests
{
    private static string Release(string tag, string assets = "", string url = "https://github.com/x/Morphonic/releases/tag/v9") =>
        "{\"tag_name\":\"" + tag + "\",\"html_url\":\"" + url + "\",\"body\":\"notes here\",\"published_at\":\"2026-10-09T12:00:00Z\",\"assets\":[" + assets + "]}";

    private static string A(string name, long size, string host = "https://github.com") =>
        "{\"name\":\"" + name + "\",\"size\":" + size + ",\"browser_download_url\":\"" + host + "/x/Morphonic/releases/download/v1.0.1/" + name + "\"}";

    private static readonly string Assets = string.Join(",",
        A("Morphonic-1.0.1-win-x64-offline.zip", 700000000), A("Morphonic-1.0.1-win-x64.zip", 38000000),
        A("Morphonic-1.0.1-linux-x64-offline", 900000000), A("Morphonic-1.0.1-linux-x64", 48000000), A("SHA256SUMS.txt", 400));

    [Fact]
    public void NewerReleaseIsOfferedWithThePlainAssetForThePlatform()
    {
        var r = UpdateCheck.Parse(Release("v1.0.1", Assets), new Version(1, 0, 0, 0), "-win-x64.zip");
        Assert.True(r.Available);
        Assert.Equal("1.0.0", r.Current);
        Assert.Equal("1.0.1", r.Latest);
        Assert.Equal("Morphonic-1.0.1-win-x64.zip", r.AssetName);
        Assert.Equal(38000000, r.AssetSize);
        Assert.Equal("2026-10-09", r.Published);
        Assert.Equal("notes here", r.Notes);
        Assert.StartsWith("https://github.com/", r.Url);

        var linux = UpdateCheck.Parse(Release("v1.0.1", Assets), new Version(1, 0, 0), "-linux-x64");
        Assert.Equal("Morphonic-1.0.1-linux-x64", linux.AssetName);
    }

    [Fact]
    public void SameOrOlderReleaseIsNotAnUpdate()
    {
        Assert.False(UpdateCheck.Parse(Release("v1.0.0"), new Version(1, 0, 0), "-win-x64.zip").Available);
        Assert.False(UpdateCheck.Parse(Release("0.9.9"), new Version(1, 0, 0), "-win-x64.zip").Available);
        Assert.True(UpdateCheck.Parse(Release("1.1"), new Version(1, 0, 7), "-win-x64.zip").Available);
    }

    [Fact]
    public void TagsWithAndWithoutThePrefixAreVersions()
    {
        Assert.Equal(new Version(1, 2, 3), UpdateCheck.ParseVersion("v1.2.3"));
        Assert.Equal(new Version(1, 2, 0), UpdateCheck.ParseVersion("1.2"));
        Assert.Null(UpdateCheck.ParseVersion("latest"));
        Assert.Null(UpdateCheck.ParseVersion(""));
    }

    [Fact]
    public void BadDocumentsAreRefused()
    {
        Assert.Throws<FormatException>(() => UpdateCheck.Parse("not json", new Version(1, 0, 0), "-win-x64.zip"));
        Assert.Throws<FormatException>(() => UpdateCheck.Parse(Release("nightly"), new Version(1, 0, 0), "-win-x64.zip"));
    }

    [Fact]
    public void OnlyGitHubReleasePagesAreOfferedAsLinks()
    {
        var r = UpdateCheck.Parse(Release("v2.0.0", "", "https://example.com/evil"), new Version(1, 0, 0), "-win-x64.zip");
        Assert.True(r.Available);
        Assert.Equal("", r.Url);
        Assert.Equal("", r.AssetName);
    }

    [Fact]
    public void TheReleaseGivesTheFileAndItsChecksumsToUpdateFrom()
    {
        var r = UpdateCheck.Parse(Release("v1.0.1", Assets), new Version(1, 0, 0), "-win-x64.zip");
        Assert.Equal("https://github.com/x/Morphonic/releases/download/v1.0.1/Morphonic-1.0.1-win-x64.zip", r.AssetUrl);
        Assert.EndsWith("/SHA256SUMS.txt", r.SumsUrl);
        // the offline build updates to the offline build
        Assert.Equal("Morphonic-1.0.1-win-x64-offline.zip", UpdateCheck.Parse(Release("v1.0.1", Assets), new Version(1, 0, 0), "-win-x64.zip", offline: true).AssetName);
        Assert.Equal("Morphonic-1.0.1-linux-x64-offline", UpdateCheck.Parse(Release("v1.0.1", Assets), new Version(1, 0, 0), "-linux-x64", offline: true).AssetName);
        Assert.Equal("Morphonic-1.0.1-linux-x64", UpdateCheck.Parse(Release("v1.0.1", Assets), new Version(1, 0, 0), "-linux-x64").AssetName);
    }

    [Fact]
    public void DownloadsFromAnywhereButGitHubAreNotUsed()
    {
        var elsewhere = string.Join(",", A("Morphonic-1.0.1-win-x64.zip", 38000000, "https://example.com"), A("SHA256SUMS.txt", 400, "http://github.com"));
        var r = UpdateCheck.Parse(Release("v1.0.1", elsewhere), new Version(1, 0, 0), "-win-x64.zip");
        Assert.Equal("Morphonic-1.0.1-win-x64.zip", r.AssetName);
        Assert.Equal("", r.AssetUrl);
        Assert.Equal("", r.SumsUrl);
        // a local test feed may serve its own files over plain http
        var local = string.Join(",", A("Morphonic-1.0.1-win-x64.zip", 38000000, "http://127.0.0.1:8123"), A("SHA256SUMS.txt", 400, "http://127.0.0.1:8123"));
        var t = UpdateCheck.Parse(Release("v1.0.1", local), new Version(1, 0, 0), "-win-x64.zip", false, new[] { "github.com", "127.0.0.1" });
        Assert.StartsWith("http://127.0.0.1:8123/", t.AssetUrl);
    }

    [Fact]
    public void TheChecksumListIsReadAsSha256sumWritesIt()
    {
        var h1 = new string('a', 64); var h2 = new string('B', 64);
        var sums = h1 + "  Morphonic-1.0.4-win-x64.zip\r\n" + h2 + " *Morphonic-1.0.4-linux-x64\n" + new string('c', 64) + "  Morphonic-1.0.4-linux-x64-offline\n";
        Assert.Equal(h1, Updater.ExpectedHash(sums, "Morphonic-1.0.4-win-x64.zip"));
        Assert.Equal(h2.ToLowerInvariant(), Updater.ExpectedHash(sums, "Morphonic-1.0.4-linux-x64"));
        Assert.Null(Updater.ExpectedHash(sums, "Morphonic-1.0.4-win-x64-offline.zip"));
        Assert.Null(Updater.ExpectedHash(new string('z', 64) + "  x", "x"));
    }

    [Fact]
    public void TheSwapLeavesTheNewFileInPlace()
    {
        var dir = Directory.CreateTempSubdirectory("morphonic-swap").FullName;
        try
        {
            var exe = Path.Combine(dir, "Morphonic.exe"); var fresh = Path.Combine(dir, ".Morphonic.exe.new");
            File.WriteAllText(exe, "old"); File.WriteAllText(fresh, "new");
            Updater.Swap(exe, fresh);
            Assert.Equal("new", File.ReadAllText(exe));
            Assert.False(File.Exists(fresh));
            if (OperatingSystem.IsWindows()) Assert.Equal("old", File.ReadAllText(Updater.OldPath(exe)));
            // a failed move puts the old file back
            if (OperatingSystem.IsWindows())
            {
                Assert.ThrowsAny<IOException>(() => Updater.Swap(exe, Path.Combine(dir, "missing")));
                Assert.Equal("new", File.ReadAllText(exe));
            }
        }
        finally { Directory.Delete(dir, true); }
    }
}
