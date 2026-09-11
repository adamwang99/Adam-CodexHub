using System.Security.Cryptography;

namespace AdamCodexHub.Core.Maintenance;

/// <summary>
/// Puts an update package in place.
///
/// This is the part of updating that breaks things. Windows will not let a running application
/// overwrite its own assemblies, so the files are staged elsewhere first and swapped in while the app
/// is closed; and because a half-applied swap leaves a mixed-version install that looks fine until it
/// is not, every replaceable file is copied to a backup first and the whole set is rolled back if any
/// single file fails to land.
///
/// The rule throughout: a file is only ever replaced by one whose bytes hash to what the manifest
/// promised, and the result is verified on disk afterwards. Anything that does not check out leaves
/// the installation exactly as it was.
/// </summary>
public static class UpdateApplier
{
    /// <summary>What happened. <see cref="BackupDirectory"/> is kept on success and on failure alike.</summary>
    public sealed record Result(
        bool Succeeded,
        int Replaced,
        int AlreadyCurrent,
        string? BackupDirectory,
        string? Failure);

    /// <summary>
    /// Applies every entry of <paramref name="manifest"/> from <paramref name="stagingDirectory"/> into
    /// <paramref name="installDirectory"/>. Files whose installed copy already matches are left alone,
    /// so re-running an interrupted update is harmless.
    /// </summary>
    public static Result Apply(
        string stagingDirectory,
        string installDirectory,
        UpdatePackage.Manifest manifest)
    {
        if (string.IsNullOrWhiteSpace(stagingDirectory) || !Directory.Exists(stagingDirectory))
        {
            return new Result(false, 0, 0, null, "the staged update is missing");
        }

        if (string.IsNullOrWhiteSpace(installDirectory) || !Directory.Exists(installDirectory))
        {
            return new Result(false, 0, 0, null, "the installation folder was not found");
        }

        var stagingRoot = Path.GetFullPath(stagingDirectory);
        var installRoot = Path.GetFullPath(installDirectory);

        // Deciding what to do first, so nothing is written before the whole plan is known to be sound.
        var work = new List<(UpdatePackage.FileEntry Entry, string Staged, string Target)>();
        var alreadyCurrent = 0;
        foreach (var entry in manifest.Files)
        {
            if (!TryResolve(installRoot, entry.RelativePath, out var target) ||
                !TryResolve(stagingRoot, entry.RelativePath, out var staged))
            {
                // Re-checked here even though the parser refuses such a manifest: this is the last
                // point before a path becomes a write, and defence in depth is cheap at a boundary.
                return new Result(false, 0, alreadyCurrent, null, $"unsafe path in the manifest: {entry.RelativePath}");
            }

            if (!File.Exists(staged))
            {
                return new Result(false, 0, alreadyCurrent, null, $"the package is missing {entry.RelativePath}");
            }

            if (!UpdatePackage.Matches(entry, HashFile(staged)))
            {
                // Refused before anything is touched: a package that does not match its own manifest is
                // not partially trustworthy.
                return new Result(false, 0, alreadyCurrent, null, $"{entry.RelativePath} does not match the manifest");
            }

            if (File.Exists(target) &&
                string.Equals(HashFile(target), entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                alreadyCurrent++;
                continue;
            }

            work.Add((entry, staged, target));
        }

        if (work.Count == 0)
        {
            return new Result(true, 0, alreadyCurrent, null, null);
        }

        string? backupDirectory = null;
        var replaced = 0;
        var backups = new List<(string Backup, string Target)>();

        try
        {
            backupDirectory = Path.Combine(installRoot, $"update-backup-{manifest.Version}");
            Directory.CreateDirectory(backupDirectory);

            foreach (var (entry, staged, target) in work)
            {
                var directory = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                // Back up what is there before it is lost — including "nothing was there", which is
                // recorded as absence so a rollback removes the file rather than restoring an empty one.
                var backup = Path.Combine(backupDirectory, entry.RelativePath.Replace('/', '\\'));
                var backupFolder = Path.GetDirectoryName(backup);
                if (!string.IsNullOrEmpty(backupFolder))
                {
                    Directory.CreateDirectory(backupFolder);
                }

                if (File.Exists(target))
                {
                    File.Copy(target, backup, overwrite: true);
                    backups.Add((backup, target));
                }
                else
                {
                    backups.Add((string.Empty, target));
                }

                File.Copy(staged, target, overwrite: true);

                // Verified on disk, not assumed from a successful copy call.
                if (!string.Equals(HashFile(target), entry.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException($"{entry.RelativePath} did not verify after being written");
                }

                replaced++;
            }

            return new Result(true, replaced, alreadyCurrent, backupDirectory, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var restored = RollBack(backups);
            return new Result(
                false,
                replaced,
                alreadyCurrent,
                backupDirectory,
                $"{ex.Message} — {restored} file(s) rolled back");
        }
    }

    /// <summary>
    /// Puts the backed-up files back. Returns how many were restored; a file that did not exist before
    /// is removed again, because leaving a new half-written file behind is worse than having none.
    /// </summary>
    private static int RollBack(List<(string Backup, string Target)> backups)
    {
        var restored = 0;
        foreach (var (backup, target) in backups)
        {
            try
            {
                if (string.IsNullOrEmpty(backup))
                {
                    if (File.Exists(target))
                    {
                        File.Delete(target);
                    }
                }
                else if (File.Exists(backup))
                {
                    File.Copy(backup, target, overwrite: true);
                }

                restored++;
            }
            catch (IOException)
            {
                // Nothing useful left to do: the backup stays on disk for a human.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return restored;
    }

    /// <summary>
    /// Joins a manifest path onto a root and refuses anything that lands outside it, however the path
    /// is spelled.
    /// </summary>
    private static bool TryResolve(string root, string relativePath, out string resolved)
    {
        resolved = string.Empty;
        if (!UpdatePackage.IsSafeRelativePath(relativePath))
        {
            return false;
        }

        var candidate = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var rootWithSeparator = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        if (!candidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        resolved = candidate;
        return true;
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
