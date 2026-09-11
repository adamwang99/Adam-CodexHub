using System.Collections.Generic;
using AdamCodexHub.Core.Maintenance;
using Xunit;

namespace AdamCodexHub.Core.Tests;

/// <summary>
/// The small update package replaces files inside the install directory, so the manifest is a
/// security boundary as much as a build artifact: whoever can shape it decides what gets written
/// where. These tests exist because "it worked on a clean manifest" proves nothing about the ones
/// that matter.
/// </summary>
public sealed class UpdatePackageTests
{
    private const string ValidManifest = """
        {
          "version": "1.5.6",
          "runtimeIdentifier": "win-x64",
          "files": [
            { "path": "AdamCodexHub.App.dll", "sha256": "AA11", "size": 120000 },
            { "path": "AdamCodexHub.Core.dll", "sha256": "BB22", "size": 45000 },
            { "path": "Assets/adam-codexhub.ico", "sha256": "CC33", "size": 12000 }
          ]
        }
        """;

    [Fact]
    public void AWellFormedManifestIsRead()
    {
        var manifest = UpdatePackage.Parse(ValidManifest, out var rejection);

        Assert.Null(rejection);
        Assert.NotNull(manifest);
        Assert.Equal("1.5.6", manifest!.Version);
        Assert.Equal("win-x64", manifest.RuntimeIdentifier);
        Assert.Equal(3, manifest.Files.Count);
        // Digests are normalised on the way in so comparisons never depend on case.
        Assert.Equal("aa11", manifest.Files[0].Sha256);
        Assert.Equal("Assets/adam-codexhub.ico", manifest.Files[2].RelativePath);
    }

    [Fact]
    public void AByteOrderMarkDoesNotCountAsAMalformedManifest()
    {
        // PowerShell's Set-Content -Encoding utf8 writes a BOM on 5.1, and this file travels through a
        // zip and a network before anything reads it. A byte-order detail must not read as a corrupt
        // package — measured 2026-09-11, the first manifest the packaging script produced carried one,
        // and a strict JSON reader refused the whole file over it.
        var manifest = UpdatePackage.Parse("\uFEFF" + ValidManifest, out var rejection);

        Assert.Null(rejection);
        Assert.NotNull(manifest);
        Assert.Equal("1.5.6", manifest!.Version);
        Assert.Equal(3, manifest.Files.Count);
    }

    [Theory]
    // Escaping the install directory, however it is spelled.
    [InlineData("..\\..\\Windows\\System32\\evil.dll")]
    [InlineData("../../evil.dll")]
    [InlineData("Assets\\..\\..\\evil.dll")]
    [InlineData("sub\\..\\..\\..\\evil.dll")]
    // Absolute paths.
    [InlineData("C:\\Windows\\System32\\evil.dll")]
    [InlineData("\\\\server\\share\\evil.dll")]
    [InlineData("\\Windows\\evil.dll")]
    public void APathThatCouldLeaveTheInstallDirectoryRejectsTheWholeManifest(string hostilePath)
    {
        // Not the entry — the manifest. Accepting a package minus the one bad line is how you end up
        // with a half-applied update somebody believes was verified.
        //
        // Built with the serializer rather than by pasting the path into a string: a raw backslash in
        // hand-written JSON is malformed JSON, and then the manifest would be refused for the wrong
        // reason and this test would pass while proving nothing.
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            version = "1.5.6",
            runtimeIdentifier = "win-x64",
            files = new object[]
            {
                new { path = "AdamCodexHub.App.dll", sha256 = "AA11", size = 1 },
                new { path = hostilePath, sha256 = "DEAD", size = 1 },
            },
        });

        var manifest = UpdatePackage.Parse(json, out var rejection);

        Assert.Null(manifest);
        Assert.NotNull(rejection);
        Assert.Contains("unsafe path", rejection!.Reason);
    }

    [Theory]
    [InlineData("AdamCodexHub.App.dll")]
    [InlineData("Assets\\icon.ico")]
    [InlineData("Assets/icon.ico")]
    [InlineData("cli\\AdamCodexHub.Cli.exe")]
    [InlineData("a\\b\\c\\d.dll")]
    public void OrdinaryRelativePathsAreAccepted(string path)
    {
        Assert.True(UpdatePackage.IsSafeRelativePath(path));
    }

    [Fact]
    public void AManifestWithNoUsableEntriesIsRefused()
    {
        foreach (var json in new[]
        {
            "not json at all",
            "{}",
            """{ "version": "1.5.6", "runtimeIdentifier": "win-x64" }""",
            """{ "version": "", "runtimeIdentifier": "win-x64", "files": [] }""",
            """{ "version": "1.5.6", "runtimeIdentifier": "win-x64", "files": [] }""",
            """{ "version": "1.5.6", "runtimeIdentifier": "win-x64", "files": [ { "path": "a.dll", "sha256": "AA" } ] }""",
            """{ "version": "1.5.6", "runtimeIdentifier": "win-x64", "files": [ { "path": "a.dll", "sha256": "AA", "size": -1 } ] }""",
        })
        {
            Assert.Null(UpdatePackage.Parse(json, out var rejection));
            Assert.NotNull(rejection);
        }
    }

    [Fact]
    public void ARepeatedPathIsRefusedRatherThanLastOneWins()
    {
        // Two entries for one file means the manifest disagrees with itself about what the file should
        // be; whichever order the applier happened to use would decide the outcome.
        const string json = """
            {
              "version": "1.5.6",
              "runtimeIdentifier": "win-x64",
              "files": [
                { "path": "AdamCodexHub.App.dll", "sha256": "AA11", "size": 1 },
                { "path": "adamcodexhub.app.dll", "sha256": "BB22", "size": 1 }
              ]
            }
            """;

        Assert.Null(UpdatePackage.Parse(json, out var rejection));
        Assert.Contains("twice", rejection!.Reason);
    }

    [Fact]
    public void ThePlanIsOnlyTheFilesThatDiffer()
    {
        var manifest = UpdatePackage.Parse(ValidManifest, out _)!;
        var installed = new Dictionary<string, string>
        {
            // Already correct — nothing to do.
            ["AdamCodexHub.App.dll"] = "aa11",
            // Present but different — replace.
            ["AdamCodexHub.Core.dll"] = "OLD",
            // "Assets/adam-codexhub.ico" is absent entirely — install.
        };

        var plan = UpdatePackage.Plan(manifest, installed);

        Assert.Equal(2, plan.Count);
        Assert.Contains(plan, entry => entry.RelativePath == "AdamCodexHub.Core.dll");
        Assert.Contains(plan, entry => entry.RelativePath == "Assets/adam-codexhub.ico");
        Assert.DoesNotContain(plan, entry => entry.RelativePath == "AdamCodexHub.App.dll");
    }

    [Fact]
    public void AnInstallThatAlreadyMatchesNeedsNoWork()
    {
        var manifest = UpdatePackage.Parse(ValidManifest, out _)!;
        var installed = new Dictionary<string, string>
        {
            ["AdamCodexHub.App.dll"] = "AA11",
            ["AdamCodexHub.Core.dll"] = "bb22",
            ["Assets/adam-codexhub.ico"] = "cc33",
        };

        Assert.Empty(UpdatePackage.Plan(manifest, installed));
    }

    [Fact]
    public void VerificationComparesByDigestAndNeverAcceptsAMissingOne()
    {
        var entry = new UpdatePackage.FileEntry("AdamCodexHub.App.dll", "aa11", 1);

        Assert.True(UpdatePackage.Matches(entry, "aa11"));
        Assert.True(UpdatePackage.Matches(entry, "AA11"));
        Assert.True(UpdatePackage.Matches(entry, " aa11 "));
        Assert.False(UpdatePackage.Matches(entry, "bb22"));
        Assert.False(UpdatePackage.Matches(entry, null));
        Assert.False(UpdatePackage.Matches(entry, ""));
    }
}
