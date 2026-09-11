using System.Text.Json;

namespace AdamCodexHub.Core.Maintenance;

/// <summary>
/// The small update package: only the files that actually change between releases, checked into place
/// one by one. Measured 2026-09-11 against the real publish output — the hub's own assemblies are
/// 6.6 MB of a 293.5 MB folder, the other 88% being the .NET runtime that never changes between
/// patches. The full installer is 91 MB and, at the 525 KB/min this machine managed, that download is
/// a three-hour wait; the package that carries the actual change is a few megabytes.
///
/// The full installer stays for first install and for repair: this is deliberately an addition, not a
/// replacement. An update package assumes a working install of the previous version, and nothing else.
///
/// Everything here is pure: parsing, validation and "which files need replacing" are decided from
/// strings and dictionaries so they can be tested without touching a disk or the network. The part
/// that writes bytes lives in the app, where it can be measured against a real install instead.
/// </summary>
public sealed class UpdatePackage
{
    /// <summary>One file the package will supply, with the digest it must have.</summary>
    public sealed record FileEntry(string RelativePath, string Sha256, long Size);

    /// <summary>A parsed package manifest.</summary>
    public sealed record Manifest(
        string Version,
        string RuntimeIdentifier,
        IReadOnlyList<FileEntry> Files);

    /// <summary>Why a manifest was refused. The caller only has to log it; there is no partial accept.</summary>
    public sealed record Rejection(string Reason);

    /// <summary>
    /// Reads a manifest. Returns a rejection rather than throwing, and refuses the WHOLE manifest if any
    /// single entry is unusable — a package that is accepted piecemeal is how a mixed-version install
    /// happens.
    /// </summary>
    public static Manifest? Parse(string manifestJson, out Rejection? rejection)
    {
        rejection = null;
        try
        {
            // A manifest may arrive with a byte-order mark: PowerShell's Set-Content -Encoding utf8
            // writes one on 5.1, and this file travels through a zip and a network before it is read.
            // JsonDocument rejects a leading BOM outright, so strip it here rather than let a
            // byte-order detail read as a malformed package.
            var json = manifestJson.TrimStart('\uFEFF');

            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            var version = root.TryGetProperty("version", out var versionElement)
                ? versionElement.GetString()
                : null;
            var runtime = root.TryGetProperty("runtimeIdentifier", out var runtimeElement)
                ? runtimeElement.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(runtime))
            {
                rejection = new Rejection("the manifest names no version or runtime");
                return null;
            }

            if (!root.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
            {
                rejection = new Rejection("the manifest lists no files");
                return null;
            }

            var entries = new List<FileEntry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files.EnumerateArray())
            {
                var path = file.TryGetProperty("path", out var pathElement) ? pathElement.GetString() : null;
                var sha = file.TryGetProperty("sha256", out var shaElement) ? shaElement.GetString() : null;
                var size = file.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var parsed)
                    ? parsed
                    : -1L;

                if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(sha) || size < 0)
                {
                    rejection = new Rejection("a file entry is missing its path, digest or size");
                    return null;
                }

                if (!IsSafeRelativePath(path))
                {
                    // The whole point of a manifest is that it can be trusted to stay inside the
                    // install directory. "..\\..\\Windows\\System32\\..." is not a file we replace.
                    rejection = new Rejection($"the manifest contains an unsafe path: {path}");
                    return null;
                }

                if (!seen.Add(path))
                {
                    rejection = new Rejection($"the manifest lists {path} twice");
                    return null;
                }

                entries.Add(new FileEntry(path, sha.Trim().ToLowerInvariant(), size));
            }

            if (entries.Count == 0)
            {
                rejection = new Rejection("the manifest lists no usable files");
                return null;
            }

            return new Manifest(version, runtime, entries);
        }
        catch (JsonException)
        {
            rejection = new Rejection("the manifest is not valid JSON");
            return null;
        }
    }

    /// <summary>
    /// The entries whose file is absent or different in the installed copy, i.e. the work to do.
    /// Hashes come in as a dictionary so the decision needs no disk access; a missing key means the
    /// file is not installed at all.
    /// </summary>
    public static IReadOnlyList<FileEntry> Plan(
        Manifest manifest,
        IReadOnlyDictionary<string, string> installedHashes)
    {
        var work = new List<FileEntry>();
        foreach (var entry in manifest.Files)
        {
            if (!installedHashes.TryGetValue(entry.RelativePath, out var installed) ||
                !string.Equals(installed, entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                work.Add(entry);
            }
        }

        return work;
    }

    /// <summary>
    /// True only for a plain relative path that cannot escape the directory it is joined to: no rooted
    /// paths, no drive letters, no "..". Windows path separators and forward slashes both count,
    /// because the manifest is written by one tool and read by another.
    /// </summary>
    public static bool IsSafeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var normalised = path.Replace('/', '\\').Trim();
        if (normalised.StartsWith('\\') || normalised.Contains(':'))
        {
            return false;
        }

        foreach (var segment in normalised.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == "..")
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Compares a file against the digest the manifest promised. Kept next to the plan so the rule
    /// "verified or not installed" reads in one place.
    /// </summary>
    public static bool Matches(FileEntry entry, string? actualSha256) =>
        !string.IsNullOrWhiteSpace(actualSha256) &&
        string.Equals(entry.Sha256, actualSha256.Trim(), StringComparison.OrdinalIgnoreCase);
}
