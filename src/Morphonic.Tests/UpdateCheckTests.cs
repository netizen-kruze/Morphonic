using Xunit;

namespace Morphonic.Tests;

// The update check's reading of GitHub's release document: which version
// counts as newer, which asset is offered, and what is refused.
public class UpdateCheckTests
{
    private static string Release(string tag, string assets = "", string url = "https://github.com/x/Morphonic/releases/tag/v9") =>
        "{\"tag_name\":\"" + tag + "\",\"html_url\":\"" + url + "\",\"body\":\"notes here\",\"published_at\":\"2026-10-09T12:00:00Z\",\"assets\":[" + assets + "]}";

    private const string Assets =
        "{\"name\":\"Morphonic-1.0.1-win-x64-offline.zip\",\"size\":700000000}," +
        "{\"name\":\"Morphonic-1.0.1-win-x64.zip\",\"size\":38000000}," +
        "{\"name\":\"Morphonic-1.0.1-linux-x64-offline\",\"size\":900000000}," +
        "{\"name\":\"Morphonic-1.0.1-linux-x64\",\"size\":48000000}," +
        "{\"name\":\"SHA256SUMS.txt\",\"size\":400}";

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
}
