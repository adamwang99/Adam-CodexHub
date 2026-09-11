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
}
