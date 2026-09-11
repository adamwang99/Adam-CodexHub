using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AdamCodexHub.Core.Maintenance;
using Xunit;

namespace AdamCodexHub.Core.Tests;

/// <summary>
/// The hub's own footprint on someone else's disk. Both leaks fixed here were measured on Adam's
/// machine on 2026-09-11 and neither announced itself: <c>startup.log</c> at 832 KB / 7,183 lines
/// after six days, and 231 config backups totalling 91 MB in <c>~/.codex/adam-codexhub-backups</c>.
/// The tests pin the two properties that matter — the ceiling holds, and what survives is the NEWEST
/// data, because a trimmed log that kept the oldest lines would be worse than no trimming at all.
/// </summary>
public sealed class HousekeepingTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "codexhub-housekeeping-" + Guid.NewGuid().ToString("N"));

    public HousekeepingTests() => Directory.CreateDirectory(_root);

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

    [Fact]
    public void ALogUnderTheCeilingIsLeftExactlyAsItWas()
    {
        var path = Path.Combine(_root, "small.log");
        var contents = string.Concat(Enumerable.Repeat($"a line{Environment.NewLine}", 50));
        File.WriteAllText(path, contents);

        var reclaimed = Housekeeping.TrimLogFile(path, maxBytes: 1024 * 1024);

        Assert.Equal(0, reclaimed);
        Assert.Equal(contents, File.ReadAllText(path));
    }

    [Fact]
    public void AnOversizedLogIsTrimmedAndKeepsItsNewestLines()
    {
        var path = Path.Combine(_root, "big.log");
        // 4,000 numbered lines: the number makes "which half survived?" answerable.
        using (var writer = new StreamWriter(path))
        {
            for (var i = 0; i < 4000; i++)
            {
                writer.WriteLine($"line {i:D5} " + new string('x', 200));
            }
        }

        var before = new FileInfo(path).Length;
        var reclaimed = Housekeeping.TrimLogFile(path, maxBytes: 100 * 1024);
        var after = new FileInfo(path).Length;
        var text = File.ReadAllText(path);

        Assert.True(reclaimed > 0, "an oversized log should have been trimmed");
        Assert.True(after < before, "the file should be smaller than it was");
        Assert.True(after <= 100 * 1024, $"the trimmed file ({after} bytes) should respect the ceiling");

        // The tail is what diagnoses the bug in front of you.
        Assert.Contains("line 03999", text);
        Assert.DoesNotContain("line 00000", text);

        // The gap is visible rather than mysterious.
        Assert.Contains("log trimmed", text);

        // Still line-oriented: no half-line left at the seek point.
        var firstRealLine = text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Skip(1)
            .First();
        Assert.StartsWith("line ", firstRealLine);
    }

    [Fact]
    public void TrimmingIsIdempotentSoTheWritePathCanCallItEveryTime()
    {
        var path = Path.Combine(_root, "repeat.log");
        using (var writer = new StreamWriter(path))
        {
            for (var i = 0; i < 2000; i++)
            {
                writer.WriteLine($"line {i:D5} " + new string('y', 200));
            }
        }

        Housekeeping.TrimLogFile(path, maxBytes: 64 * 1024);
        var afterFirst = new FileInfo(path).Length;
        var second = Housekeeping.TrimLogFile(path, maxBytes: 64 * 1024);

        Assert.Equal(0, second);
        Assert.Equal(afterFirst, new FileInfo(path).Length);
    }

    [Fact]
    public void AMissingLogIsNotAnError()
    {
        // Housekeeping runs beside real work; it must never be the reason a launch fails.
        Assert.Equal(0, Housekeeping.TrimLogFile(Path.Combine(_root, "absent.log")));
        Assert.Equal(0, Housekeeping.TrimLogFile(string.Empty));
        Assert.Equal(0, Housekeeping.PruneBackups(Path.Combine(_root, "absent"), "config-*.toml"));
    }

    [Fact]
    public void PruningKeepsTheNewestBackupsAndDeletesTheRest()
    {
        var directory = Path.Combine(_root, "backups");
        Directory.CreateDirectory(directory);

        // 25 backups, oldest first, with distinct write times so "newest" is well defined.
        var start = DateTime.UtcNow.AddDays(-25);
        for (var i = 0; i < 25; i++)
        {
            var file = Path.Combine(directory, $"config-{i:D2}.toml");
            File.WriteAllText(file, $"backup {i}");
            File.SetLastWriteTimeUtc(file, start.AddDays(i));
        }

        var removed = Housekeeping.PruneBackups(directory, "config-*.toml", keep: 10);

        Assert.Equal(15, removed);
        var left = Directory.GetFiles(directory).Select(Path.GetFileName).ToList();
        Assert.Equal(10, left.Count);
        Assert.Contains("config-24.toml", left);
        Assert.Contains("config-15.toml", left);
        Assert.DoesNotContain("config-14.toml", left);
        Assert.DoesNotContain("config-00.toml", left);
    }

    [Fact]
    public void PruningNeverTouchesFilesThatAreNotOurs()
    {
        var directory = Path.Combine(_root, "mixed");
        Directory.CreateDirectory(directory);
        for (var i = 0; i < 15; i++)
        {
            File.WriteAllText(Path.Combine(directory, $"config-{i:D2}.toml"), "ours");
        }

        // Anything a user parked in the folder stays, whatever the count says.
        File.WriteAllText(Path.Combine(directory, "my-notes.txt"), "keep me");
        File.WriteAllText(Path.Combine(directory, "config-KEEP.json"), "keep me too");

        Housekeeping.PruneBackups(directory, "config-*.toml", keep: 5);

        Assert.True(File.Exists(Path.Combine(directory, "my-notes.txt")));
        Assert.True(File.Exists(Path.Combine(directory, "config-KEEP.json")));
        Assert.Equal(5, Directory.GetFiles(directory, "config-*.toml").Length);
    }

    [Fact]
    public void KeepingZeroIsAllowedButNegativeIsRefused()
    {
        var directory = Path.Combine(_root, "edge");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "config-a.toml"), "a");

        Assert.Equal(0, Housekeeping.PruneBackups(directory, "config-*.toml", keep: -1));
        Assert.True(File.Exists(Path.Combine(directory, "config-a.toml")));

        Assert.Equal(1, Housekeeping.PruneBackups(directory, "config-*.toml", keep: 0));
        Assert.Empty(Directory.GetFiles(directory, "config-*.toml"));
    }
}
