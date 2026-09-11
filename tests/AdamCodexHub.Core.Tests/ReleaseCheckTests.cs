using System;
using AdamCodexHub.Core.Maintenance;
using Xunit;

namespace AdamCodexHub.Core.Tests;

/// <summary>
/// Someone running an older build had no way to learn a fix existed — every release had to be
/// hand-delivered. These tests pin the comparison, because the two sides are written differently: a
/// tag is <c>v1.5.4</c> while the running app reports <c>1.5.4+945e6d2…</c>, and a local build must
/// never read as newer or older than the release it was cut from.
/// </summary>
public sealed class ReleaseCheckTests
{
    private static string Feed(string tag) =>
        $"{{\"tag_name\":\"{tag}\",\"html_url\":\"https://github.com/adamwang99/Adam-CodexHub/releases/tag/{tag}\"}}";

    [Fact]
    public void ANewerTagIsReportedAsAnUpdate()
    {
        var result = ReleaseCheck.Evaluate("1.5.4", Feed("v1.5.5"));

        Assert.True(result.Succeeded);
        Assert.True(result.IsNewer);
        Assert.Equal("1.5.5", result.LatestVersion);
        Assert.Contains("releases/tag/v1.5.5", result.ReleaseUrl);
        Assert.Null(result.Failure);
    }

    [Fact]
    public void TheSameVersionIsNotAnUpdate()
    {
        var result = ReleaseCheck.Evaluate("1.5.4", Feed("v1.5.4"));

        Assert.True(result.Succeeded);
        Assert.False(result.IsNewer);
        Assert.Equal("1.5.4", result.LatestVersion);
    }

    [Fact]
    public void BuildMetadataOnTheRunningVersionIsIgnored()
    {
        // What the shipped assembly actually reports: 1.5.4+945e6d2c7c35907c0cdfa09c060aaf7a2cddbe6b.
        var running = "1.5.4+945e6d2c7c35907c0cdfa09c060aaf7a2cddbe6b";

        Assert.False(ReleaseCheck.Evaluate(running, Feed("v1.5.4")).IsNewer);
        Assert.True(ReleaseCheck.Evaluate(running, Feed("v1.5.5")).IsNewer);
        Assert.False(ReleaseCheck.Evaluate(running, Feed("v1.5.3")).IsNewer);
    }

    [Fact]
    public void ADevelopmentBuildAheadOfTheReleaseIsNotAnUpdate()
    {
        // Adam runs the next version locally most of the time; that must not nag him to "update"
        // to the older public release.
        var result = ReleaseCheck.Evaluate("1.6.0", Feed("v1.5.4"));

        Assert.True(result.Succeeded);
        Assert.False(result.IsNewer);
    }

    [Fact]
    public void AMinorNumberIsComparedNumericallyNotAlphabetically()
    {
        // "1.5.10" sorts before "1.5.9" as text; as a version it is newer.
        Assert.True(ReleaseCheck.Evaluate("1.5.9", Feed("v1.5.10")).IsNewer);
        Assert.False(ReleaseCheck.Evaluate("1.5.10", Feed("v1.5.9")).IsNewer);
    }

    [Fact]
    public void AnUnreadableFeedFailsWithoutClaimingAnUpdate()
    {
        // An unreachable or changed GitHub must never look like a broken hub, and must never invent
        // an update either.
        foreach (var payload in new[] { "not json at all", "{}", "{\"tag_name\":\"\"}", "{\"tag_name\":\"latest\"}" })
        {
            var result = ReleaseCheck.Evaluate("1.5.4", payload);
            Assert.False(result.IsNewer);
            Assert.False(result.Succeeded);
            Assert.False(string.IsNullOrWhiteSpace(result.Failure));
        }
    }

    [Fact]
    public void TheEndpointIsThePublicReleasesApiAndCarriesNoToken()
    {
        Assert.Equal(
            "https://api.github.com/repos/adamwang99/Adam-CodexHub/releases/latest",
            ReleaseCheck.LatestReleaseUrl);
        Assert.DoesNotContain("token", ReleaseCheck.LatestReleaseUrl, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("?", ReleaseCheck.LatestReleaseUrl);
    }

    private const string ReleaseWithInstaller = """
        {
          "tag_name": "v1.5.6",
          "html_url": "https://github.com/adamwang99/Adam-CodexHub/releases/tag/v1.5.6",
          "assets": [
            { "name": "AdamCodexHub-v1.5.6-win-x64.zip", "browser_download_url": "https://example/zip", "size": 138936320 },
            { "name": "AdamCodexHub-v1.5.6-win-x64.zip.sha256", "browser_download_url": "https://example/zipsha", "size": 96 },
            { "name": "AdamCodexHub-Setup-v1.5.6-win-x64.exe", "browser_download_url": "https://example/setup", "size": 99934336 },
            { "name": "AdamCodexHub-Setup-v1.5.6-win-x64.exe.sha256", "browser_download_url": "https://example/setupsha", "size": 100 }
          ]
        }
        """;

    [Fact]
    public void FindsTheInstallerAssetAndTheChecksumPublishedBesideIt()
    {
        // The exact shape v1.5.4 shipped (4 assets), so the URL pair the downloader uses is pinned.
        var setup = ReleaseCheck.FindSetupDownload(ReleaseWithInstaller);

        Assert.NotNull(setup);
        Assert.Equal("AdamCodexHub-Setup-v1.5.6-win-x64.exe", setup!.FileName);
        Assert.Equal("https://example/setup", setup.DownloadUrl);
        Assert.Equal("https://example/setupsha", setup.ChecksumUrl);
        Assert.Equal(99934336, setup.SizeBytes);
    }

    [Fact]
    public void ThePortableZipIsNeverMistakenForTheInstaller()
    {
        // Both assets start with "AdamCodexHub-" and end with an extension; only one is runnable.
        var setup = ReleaseCheck.FindSetupDownload(ReleaseWithInstaller);

        Assert.NotNull(setup);
        Assert.EndsWith(".exe", setup!.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".zip", setup.FileName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AZipOnlyReleaseOffersNothingToDownload()
    {
        // A release packaged on a runner without Inno Setup has no installer: the honest answer is
        // "no download button", not a button that fails.
        const string zipOnly = """
            {
              "tag_name": "v1.0.0",
              "assets": [
                { "name": "AdamCodexHub-v1.0.0-win-x64.zip", "browser_download_url": "https://example/zip", "size": 1 },
                { "name": "AdamCodexHub-v1.0.0-win-x64.zip.sha256", "browser_download_url": "https://example/zipsha", "size": 1 }
              ]
            }
            """;

        Assert.Null(ReleaseCheck.FindSetupDownload(zipOnly));
        Assert.Null(ReleaseCheck.FindSetupDownload("not json"));
        Assert.Null(ReleaseCheck.FindSetupDownload("{\"tag_name\":\"v1.0.0\"}"));
    }

    [Fact]
    public void AnInstallerWithoutAPublishedChecksumIsStillOffered()
    {
        // The download must not refuse to happen just because verification is impossible — but the
        // caller has to be able to tell, hence a null ChecksumUrl rather than a fabricated one.
        const string noChecksum = """
            {
              "tag_name": "v1.5.6",
              "assets": [
                { "name": "AdamCodexHub-Setup-v1.5.6-win-x64.exe", "browser_download_url": "https://example/setup", "size": 10 }
              ]
            }
            """;

        var setup = ReleaseCheck.FindSetupDownload(noChecksum);

        Assert.NotNull(setup);
        Assert.Null(setup!.ChecksumUrl);
    }

    [Theory]
    // Get-FileHash / the packaging script write "<hex> *<name>" and "<hex>  <name>"; both must parse.
    [InlineData("abc123 *AdamCodexHub-Setup.exe", "abc123", true)]
    [InlineData("abc123  AdamCodexHub-Setup.exe", "abc123", true)]
    [InlineData("ABC123 *AdamCodexHub-Setup.exe", "abc123", true)]
    [InlineData("abc123 *AdamCodexHub-Setup.exe\n", "abc123", true)]
    [InlineData("abc123 *AdamCodexHub-Setup.exe", "deadbeef", false)]
    public void PublishedChecksumsAreComparedByTheirHexToken(
        string published,
        string computed,
        bool expected)
    {
        Assert.Equal(expected, ReleaseCheck.ChecksumMatches(published, computed));
    }

    [Fact]
    public void AnEmptyOrMissingChecksumNeverCountsAsAMatch()
    {
        // Failing open here would make the verification decorative.
        Assert.False(ReleaseCheck.ChecksumMatches(null, "abc"));
        Assert.False(ReleaseCheck.ChecksumMatches("", "abc"));
        Assert.False(ReleaseCheck.ChecksumMatches("abc", null));
        Assert.False(ReleaseCheck.ChecksumMatches("   ", "abc"));
    }
}
