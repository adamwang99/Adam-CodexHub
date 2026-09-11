using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using AdamCodexHub.Core.Maintenance;
using Xunit;

namespace AdamCodexHub.Core.Tests;

/// <summary>
/// A real upgrade, at file level: an "installed" 1.5.5 folder, a staged 1.5.6 package, and the applier
/// run against both. This is the part that decides whether an update leaves a working installation or a
/// mixed-version one, so the tests are about the failure paths at least as much as the happy one.
/// </summary>
public sealed class UpdateApplierTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "codexhub-updater-" + Guid.NewGuid().ToString("N"));

    private readonly string _install;
    private readonly string _staging;

    public UpdateApplierTests()
    {
        _install = Path.Combine(_root, "install");
        _staging = Path.Combine(_root, "staging");
        Directory.CreateDirectory(_install);
        Directory.CreateDirectory(_staging);
    }

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_install, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static string Hash(string content) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    /// <summary>Writes a file and returns the digest a manifest would have to promise for it.</summary>
    private static string Put(string folder, string relativePath, string content)
    {
        var path = Path.Combine(folder, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return Hash(content);
    }

    private static UpdatePackage.Manifest ManifestFor(params (string Path, string Hash)[] files) =>
        new("1.5.6", "win-x64", files.Select(f => new UpdatePackage.FileEntry(f.Path, f.Hash, 0)).ToList());

    [Fact]
    public void AnUpgradeReplacesWhatChangedAndLeavesTheRestAlone()
    {
        // Installed 1.5.5: one file old, one already identical to 1.5.6.
        Put(_install, "AdamCodexHub.App.dll", "old app");
        var coreHash = Put(_install, "AdamCodexHub.Core.dll", "core unchanged");

        // Staged 1.5.6.
        var appHash = Put(_staging, "AdamCodexHub.App.dll", "new app");
        Put(_staging, "AdamCodexHub.Core.dll", "core unchanged");

        var manifest = ManifestFor(
            ("AdamCodexHub.App.dll", appHash),
            ("AdamCodexHub.Core.dll", coreHash));

        var result = UpdateApplier.Apply(_staging, _install, manifest);

        Assert.True(result.Succeeded, result.Failure);
        Assert.Equal(1, result.Replaced);
        Assert.Equal(1, result.AlreadyCurrent);
        Assert.Equal("new app", File.ReadAllText(Path.Combine(_install, "AdamCodexHub.App.dll")));

        // The replaced file was backed up first, so the swap is undoable by hand.
        Assert.NotNull(result.BackupDirectory);
        Assert.Equal(
            "old app",
            File.ReadAllText(Path.Combine(result.BackupDirectory!, "AdamCodexHub.App.dll")));
    }

    [Fact]
    public void APackageThatDoesNotMatchItsOwnManifestIsRefusedBeforeAnythingIsWritten()
    {
        Put(_install, "AdamCodexHub.App.dll", "old app");

        // Staged bytes are not what the manifest promises — a truncated or rewritten package.
        Put(_staging, "AdamCodexHub.App.dll", "tampered");

        var manifest = ManifestFor(("AdamCodexHub.App.dll", Hash("new app")));

        var result = UpdateApplier.Apply(_staging, _install, manifest);

        Assert.False(result.Succeeded);
        Assert.Contains("does not match the manifest", result.Failure);
        Assert.Equal(0, result.Replaced);
        // Nothing was touched: the installation still holds the old, working file.
        Assert.Equal("old app", File.ReadAllText(Path.Combine(_install, "AdamCodexHub.App.dll")));
    }

    [Fact]
    public void AFailurePartWayThroughRollsTheWholeSetBack()
    {
        // Two files to replace; the second one is read-only, so its copy fails after the first has
        // already landed. That is exactly the state a naive updater leaves behind.
        Put(_install, "AdamCodexHub.App.dll", "old app");
        var coreTarget = Path.Combine(_install, "AdamCodexHub.Core.dll");
        File.WriteAllText(coreTarget, "old core");
        File.SetAttributes(coreTarget, FileAttributes.ReadOnly);

        var appHash = Put(_staging, "AdamCodexHub.App.dll", "new app");
        var coreHash = Put(_staging, "AdamCodexHub.Core.dll", "new core");

        try
        {
            var manifest = ManifestFor(
                ("AdamCodexHub.App.dll", appHash),
                ("AdamCodexHub.Core.dll", coreHash));

            var result = UpdateApplier.Apply(_staging, _install, manifest);

            Assert.False(result.Succeeded);
            Assert.Contains("rolled back", result.Failure);

            // The file that had already been replaced is back to what it was: no mixed version.
            Assert.Equal("old app", File.ReadAllText(Path.Combine(_install, "AdamCodexHub.App.dll")));
            Assert.Equal("old core", File.ReadAllText(coreTarget));
        }
        finally
        {
            File.SetAttributes(coreTarget, FileAttributes.Normal);
        }
    }

    [Fact]
    public void APathThatCouldEscapeTheInstallFolderIsRefusedAtTheLastMoment()
    {
        // The manifest is built by hand here, bypassing Parse — because this check has to hold even if
        // a caller forgets to validate first. This is the last point before a path becomes a write.
        var outside = Path.Combine(_root, "outside.dll");
        Put(_staging, "evil.dll", "payload");

        var manifest = new UpdatePackage.Manifest(
            "1.5.6",
            "win-x64",
            new List<UpdatePackage.FileEntry>
            {
                new("..\\outside.dll", Hash("payload"), 0),
            });

        var result = UpdateApplier.Apply(_staging, _install, manifest);

        Assert.False(result.Succeeded);
        Assert.Contains("unsafe path", result.Failure);
        Assert.False(File.Exists(outside));
    }

    [Fact]
    public void ReRunningAnUpgradeThatAlreadyLandedDoesNothing()
    {
        // Interrupted updates get retried; a second run must be a no-op, not a second backup.
        var appHash = Put(_install, "AdamCodexHub.App.dll", "new app");
        Put(_staging, "AdamCodexHub.App.dll", "new app");

        var manifest = ManifestFor(("AdamCodexHub.App.dll", appHash));

        var result = UpdateApplier.Apply(_staging, _install, manifest);

        Assert.True(result.Succeeded, result.Failure);
        Assert.Equal(0, result.Replaced);
        Assert.Equal(1, result.AlreadyCurrent);
        Assert.Null(result.BackupDirectory);
    }

    [Fact]
    public void AMissingStagedFileFailsWithoutTouchingTheInstall()
    {
        Put(_install, "AdamCodexHub.App.dll", "old app");

        var manifest = ManifestFor(("AdamCodexHub.App.dll", Hash("new app")));

        var result = UpdateApplier.Apply(_staging, _install, manifest);

        Assert.False(result.Succeeded);
        Assert.Contains("missing", result.Failure);
        Assert.Equal("old app", File.ReadAllText(Path.Combine(_install, "AdamCodexHub.App.dll")));
    }

    [Fact]
    public void AMissingInstallationFolderIsReportedRatherThanCreated()
    {
        Put(_staging, "AdamCodexHub.App.dll", "new app");
        var manifest = ManifestFor(("AdamCodexHub.App.dll", Hash("new app")));

        var result = UpdateApplier.Apply(_staging, Path.Combine(_root, "not-installed"), manifest);

        Assert.False(result.Succeeded);
        Assert.Contains("installation folder", result.Failure);
    }
}
