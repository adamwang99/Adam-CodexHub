namespace AdamCodexHub.Core.Maintenance;

/// <summary>
/// Keeps the hub's own footprint on the user's disk bounded.
///
/// Two leaks were measured on Adam's machine on 2026-09-11, both from code that only ever appended:
/// <c>%LOCALAPPDATA%\AdamCodexHub\logs\startup.log</c> had grown to 832 KB / 7,183 lines in six days
/// (the gateway logs every turn), and <c>~/.codex/adam-codexhub-backups</c> held 231 config backups
/// totalling 91 MB because a backup was written before every activation and none was ever removed.
/// Neither is a crash; both are the kind of slow rot that shows up months later on someone else's
/// machine as "this app ate my disk".
///
/// Design rules, in order of importance:
/// <list type="bullet">
/// <item>Never lose the newest data. Trimming a log keeps the TAIL (recent lines diagnose the bug in
/// front of you); pruning backups keeps the NEWEST files.</item>
/// <item>Never throw. Housekeeping runs beside real work and a failure to tidy up must not break a
/// launch, an activation, or a turn. Every path returns a count instead.</item>
/// <item>Never delete what was not clearly ours. Pruning matches an explicit filename pattern, so a
/// file a user parked in the folder stays.</item>
/// </list>
/// </summary>
public static class Housekeeping
{
    /// <summary>Default ceiling for a single log file: generous for diagnosis, invisible on disk.</summary>
    public const long DefaultMaxLogBytes = 5 * 1024 * 1024;

    /// <summary>How much of the file survives a trim, as a fraction of the ceiling.</summary>
    private const double KeepFraction = 0.5;

    /// <summary>Default number of timestamped backups to keep per folder.</summary>
    public const int DefaultBackupsToKeep = 10;

    /// <summary>
    /// Trims <paramref name="path"/> to roughly half the ceiling when it grows past
    /// <paramref name="maxBytes"/>, keeping the most recent lines and prefixing a marker line so the
    /// gap is visible rather than mysterious. Returns the number of bytes reclaimed (0 = untouched).
    /// </summary>
    public static long TrimLogFile(string path, long maxBytes = DefaultMaxLogBytes)
    {
        if (string.IsNullOrWhiteSpace(path) || maxBytes <= 0)
        {
            return 0;
        }

        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length <= maxBytes)
            {
                return 0;
            }

            var before = info.Length;
            var keepBytes = (long)(maxBytes * KeepFraction);

            // Read only the tail we intend to keep: a 5 MB log must not become a 5 MB string plus a
            // 5 MB array just to be shortened.
            string tail;
            using (var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            {
                stream.Seek(-keepBytes, SeekOrigin.End);
                using var reader = new StreamReader(stream);
                tail = reader.ReadToEnd();
            }

            // The seek lands mid-line; drop that fragment so the file stays line-oriented.
            var firstBreak = tail.IndexOf('\n');
            if (firstBreak >= 0 && firstBreak + 1 < tail.Length)
            {
                tail = tail[(firstBreak + 1)..];
            }

            var marker =
                $"{DateTimeOffset.Now:O} | log trimmed | " +
                $"{before / 1024} KB exceeded the {maxBytes / 1024} KB ceiling; " +
                $"older lines were discarded{Environment.NewLine}";

            var temp = path + ".trim";
            File.WriteAllText(temp, marker + tail);
            File.Move(temp, path, overwrite: true);

            var after = new FileInfo(path);
            return after.Exists ? Math.Max(0, before - after.Length) : 0;
        }
        catch (IOException)
        {
            // The log is open for append elsewhere, or the disk said no. Tidying is best effort.
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
        catch (ArgumentException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Deletes all but the newest <paramref name="keep"/> files matching <paramref name="pattern"/>
    /// in <paramref name="directory"/>. Returns how many files were removed (0 = nothing to do).
    /// Ordered by last-write time, so a restored backup keeps its place.
    /// </summary>
    public static int PruneBackups(
        string directory,
        string pattern,
        int keep = DefaultBackupsToKeep)
    {
        if (string.IsNullOrWhiteSpace(directory) ||
            string.IsNullOrWhiteSpace(pattern) ||
            keep < 0 ||
            !Directory.Exists(directory))
        {
            return 0;
        }

        try
        {
            var stale = new DirectoryInfo(directory)
                .EnumerateFiles(pattern, SearchOption.TopDirectoryOnly)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Skip(keep)
                .ToList();

            var removed = 0;
            foreach (var file in stale)
            {
                try
                {
                    file.Delete();
                    removed++;
                }
                catch (IOException)
                {
                    // In use; it will be picked up by a later pass.
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            return removed;
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
