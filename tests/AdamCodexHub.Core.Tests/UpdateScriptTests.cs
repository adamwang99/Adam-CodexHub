using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using AdamCodexHub.Core.Maintenance;
using Xunit;

namespace AdamCodexHub.Core.Tests;

/// <summary>
/// Runs the generated script for real, with PowerShell, against a real directory tree — because the
/// script is the thing that actually swaps files in production, and a string comparison would not notice
/// a quoting mistake that makes it fail on the machine.
///
/// These are slower than the rest of the suite by design: two seconds of real PowerShell beats a green
/// tick that proves nothing about whether an update works.
/// </summary>
public sealed class UpdateScriptTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "codexhub-script-" + Guid.NewGuid().ToString("N"));

    private readonly string _install;
    private readonly string _staging;
    private readonly string _log;

    public UpdateScriptTests()
    {
        _install = Path.Combine(_root, "install");
        _staging = Path.Combine(_root, "staging");
        _log = Path.Combine(_root, "update.log");
        Directory.CreateDirectory(_install);
        Directory.CreateDirectory(_staging);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static string Hash(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    private static string Put(string folder, string relativePath, string content)
    {
        var path = Path.Combine(folder, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return Hash(content);
    }

    /// <summary>Runs the script the way the app does, and waits for it.</summary>
    private int RunScript(string script, out string output)
    {
        var scriptPath = Path.Combine(_staging, UpdateScript.FileName);
        File.WriteAllText(scriptPath, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        using var process = Process.Start(new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
        })!;

        output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit(60_000);
        return process.ExitCode;
    }

    private string Generate(UpdatePackage.Manifest manifest) =>
        UpdateScript.Generate(manifest, _staging, _install, _log, relaunch: false);

    [Fact]
    public void TheScriptIsPureAsciiBecausePowerShellReadsItAsAnsiWithoutABom()
    {
        var manifest = new UpdatePackage.Manifest(
            "1.5.6",
            "win-x64",
            new List<UpdatePackage.FileEntry> { new("AdamCodexHub.App.dll", Hash("x"), 1) });

        Assert.True(UpdateScript.IsAscii(Generate(manifest)));
    }

    [Fact]
    public void TheScriptActuallyAppliesAnUpdate()
    {
        // An installed 1.5.5 with one stale file and one already current.
        Put(_install, "AdamCodexHub.App.dll", "old app");
        var coreHash = Put(_install, "AdamCodexHub.Core.dll", "core unchanged");
        var appHash = Put(_staging, "AdamCodexHub.App.dll", "new app");
        Put(_staging, "AdamCodexHub.Core.dll", "core unchanged");

        var manifest = new UpdatePackage.Manifest(
            "1.5.6",
            "win-x64",
            new List<UpdatePackage.FileEntry>
            {
                new("AdamCodexHub.App.dll", appHash, 0),
                new("AdamCodexHub.Core.dll", coreHash, 0),
            });

        var exit = RunScript(Generate(manifest), out var output);

        Assert.True(exit == 0, output);
        Assert.Equal("new app", File.ReadAllText(Path.Combine(_install, "AdamCodexHub.App.dll")));
        Assert.Equal("core unchanged", File.ReadAllText(Path.Combine(_install, "AdamCodexHub.Core.dll")));

        // The replaced file was kept, and the run was recorded where a failure would have been.
        Assert.True(File.Exists(Path.Combine(_install, "update-backup-1.5.6", "AdamCodexHub.App.dll")));
        Assert.False(File.Exists(Path.Combine(_install, "update-backup-1.5.6", "AdamCodexHub.Core.dll")));
        Assert.Contains("applied", File.ReadAllText(_log));
    }

    [Fact]
    public void APackageThatDoesNotMatchItsManifestIsRefusedAndTheInstallIsLeftAlone()
    {
        Put(_install, "AdamCodexHub.App.dll", "old app");
        Put(_staging, "AdamCodexHub.App.dll", "tampered");

        var manifest = new UpdatePackage.Manifest(
            "1.5.6",
            "win-x64",
            new List<UpdatePackage.FileEntry> { new("AdamCodexHub.App.dll", Hash("new app"), 0) });

        var exit = RunScript(Generate(manifest), out _);

        Assert.NotEqual(0, exit);
        Assert.Equal("old app", File.ReadAllText(Path.Combine(_install, "AdamCodexHub.App.dll")));
        Assert.Contains("FAILED", File.ReadAllText(_log));
    }

    [Fact]
    public void TheScriptRestoresWhatItAlreadyReplacedWhenALaterFileFails()
    {
        // The second file is named in the manifest but absent from the package, so the run fails after
        // the first file has already landed — exactly the mixed-version state to avoid.
        //
        // (A read-only target would not do it: Copy-Item -Force clears the attribute and succeeds, which
        // is how the first version of this test passed for the wrong reason.)
        Put(_install, "AdamCodexHub.App.dll", "old app");
        Put(_install, "AdamCodexHub.Core.dll", "old core");

        var appHash = Put(_staging, "AdamCodexHub.App.dll", "new app");

        var manifest = new UpdatePackage.Manifest(
            "1.5.6",
            "win-x64",
            new List<UpdatePackage.FileEntry>
            {
                new("AdamCodexHub.App.dll", appHash, 0),
                new("AdamCodexHub.Core.dll", Hash("new core"), 0),
            });

        var exit = RunScript(Generate(manifest), out var output);

        Assert.True(exit != 0, output);
        Assert.Equal("old app", File.ReadAllText(Path.Combine(_install, "AdamCodexHub.App.dll")));
        Assert.Contains("rolling back", File.ReadAllText(_log));
    }

    [Fact]
    public void RunningItTwiceIsHarmless()
    {
        var hash = Put(_install, "AdamCodexHub.App.dll", "new app");
        Put(_staging, "AdamCodexHub.App.dll", "new app");

        var manifest = new UpdatePackage.Manifest(
            "1.5.6",
            "win-x64",
            new List<UpdatePackage.FileEntry> { new("AdamCodexHub.App.dll", hash, 0) });

        var first = RunScript(Generate(manifest), out _);
        var second = RunScript(Generate(manifest), out _);

        Assert.Equal(0, first);
        Assert.Equal(0, second);
        Assert.Equal("new app", File.ReadAllText(Path.Combine(_install, "AdamCodexHub.App.dll")));
        Assert.Contains("already current", File.ReadAllText(_log));
    }
}
