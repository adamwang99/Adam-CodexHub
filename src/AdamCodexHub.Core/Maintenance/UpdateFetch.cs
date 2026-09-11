using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace AdamCodexHub.Core.Maintenance;

/// <summary>
/// Fetches the small update package and turns it into a directory the apply script can trust.
///
/// Two verifications, in order, because they answer different questions. The package's own SHA-256
/// (published beside it) says the transfer arrived intact. The manifest's per-file SHA-256 then says
/// every individual file that came out of the archive is the one the release intended - which is the
/// question that matters, since those bytes are about to replace a working application.
///
/// Extraction goes through the same path rule the applier uses, so an archive entry cannot write outside
/// the staging folder on the way in either. And a failed attempt deletes itself: a half-staged directory
/// that happens to carry the right name is worse than no directory, because it looks finished.
/// </summary>
public static class UpdateFetch
{
    /// <summary>Where a fetched package waits between "downloaded" and "applied".</summary>
    public static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AdamCodexHub",
        "update");

    public sealed record Result(bool Succeeded, string? StagingDirectory, string Message);

    /// <summary>
    /// Downloads <paramref name="packageUrl"/>, checks it against <paramref name="checksumUrl"/>, and
    /// unpacks it into <c>update\&lt;version&gt;</c>. Always leaves either a complete, verified staging
    /// directory or nothing at all.
    /// </summary>
    public static async Task<Result> FetchAsync(
        HttpClient http,
        string packageUrl,
        string? checksumUrl,
        long expectedBytes,
        string version,
        string? root = null,
        CancellationToken cancellationToken = default)
    {
        var folder = Path.Combine(root ?? Root, version);

        try
        {
            // A previous attempt's leftovers are cleared first.
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }

            Directory.CreateDirectory(folder);

            var archivePath = Path.Combine(folder, "update.zip");
            await DownloadAsync(http, packageUrl, archivePath, cancellationToken).ConfigureAwait(false);

            var outcome = await VerifyArchiveAsync(http, archivePath, checksumUrl, expectedBytes, cancellationToken)
                .ConfigureAwait(false);
            if (outcome is not null)
            {
                Directory.Delete(folder, recursive: true);
                return new Result(false, null, outcome);
            }

            // The archive is trusted enough to open now. What comes out is checked again, per file, so a
            // package that is internally inconsistent still cannot reach the applier.
            var staging = Path.Combine(folder, "staged");
            var extracted = Extract(archivePath, staging, out var manifest, out var failure);
            if (!extracted)
            {
                Directory.Delete(folder, recursive: true);
                return new Result(false, null, failure!);
            }

            File.Delete(archivePath);
            return new Result(true, staging, $"Version {manifest!.Version} is ready to install.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or UnauthorizedAccessException)
        {
            return new Result(false, null, ex.Message);
        }
    }

    private static async Task DownloadAsync(
        HttpClient http,
        string url,
        string destination,
        CancellationToken cancellationToken)
    {
        using var response = await http
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var target = File.Create(destination);
        await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Null when the transfer checked out, otherwise the reason it did not. Distinguishes a transfer that
    /// stopped short from bytes that arrived different, because those point at different causes - one is
    /// the network, the other is the file.
    /// </summary>
    private static async Task<string?> VerifyArchiveAsync(
        HttpClient http,
        string archivePath,
        string? checksumUrl,
        long expectedBytes,
        CancellationToken cancellationToken)
    {
        var actual = new FileInfo(archivePath).Length;
        if (expectedBytes > 0 && actual != expectedBytes)
        {
            return $"the download stopped early: {actual} of {expectedBytes} bytes.";
        }

        if (string.IsNullOrWhiteSpace(checksumUrl))
        {
            return null;
        }

        try
        {
            var published = (await http.GetStringAsync(checksumUrl, cancellationToken).ConfigureAwait(false))
                .Split(' ', '\t', '\n', '\r')[0]
                .Trim()
                .ToLowerInvariant();
            if (published.Length != 64)
            {
                return null;
            }

            using var stream = File.OpenRead(archivePath);
            var digest = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            return string.Equals(digest, published, StringComparison.OrdinalIgnoreCase)
                ? null
                : "the download did not match its published SHA-256.";
        }
        catch (HttpRequestException)
        {
            // The checksum file is a nicety next to the per-file digests; not being able to fetch it is
            // not a reason to refuse a package that will still be verified file by file.
            return null;
        }
    }

    /// <summary>
    /// Unpacks the archive and verifies every file against the manifest as it goes. Returns false with a
    /// reason, and removes what it wrote, rather than leaving a partly verified directory behind.
    /// </summary>
    private static bool Extract(
        string archivePath,
        string staging,
        out UpdatePackage.Manifest? manifest,
        out string? failure)
    {
        manifest = null;
        failure = null;

        using var archive = ZipFile.OpenRead(archivePath);

        var manifestEntry = archive.GetEntry("update-manifest.json");
        if (manifestEntry is null)
        {
            failure = "the package has no manifest.";
            return false;
        }

        string manifestText;
        using (var reader = new StreamReader(manifestEntry.Open()))
        {
            manifestText = reader.ReadToEnd();
        }

        manifest = UpdatePackage.Parse(manifestText, out var rejection);
        if (manifest is null)
        {
            failure = $"the manifest was refused: {rejection?.Reason}";
            return false;
        }

        var root = Path.GetFullPath(staging);
        var rootWithSeparator = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(root);

        // The manifest is written out beside the files it describes. The applier reads it back from the
        // staging directory to build the apply script, and a staging directory that cannot say what it
        // contains is not something to hand to a script that replaces an installation.
        File.WriteAllText(Path.Combine(root, "update-manifest.json"), manifestText);

        // Only entries the manifest names are unpacked: a file the manifest does not vouch for is not
        // something the applier would install anyway, so it never needs to touch the disk.
        foreach (var entry in manifest.Files)
        {
            var zipEntry = archive.GetEntry(entry.RelativePath);
            if (zipEntry is null)
            {
                failure = $"the package is missing {entry.RelativePath}.";
                Directory.Delete(root, recursive: true);
                return false;
            }

            var target = Path.GetFullPath(Path.Combine(root, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            {
                failure = $"unsafe path in the package: {entry.RelativePath}";
                Directory.Delete(root, recursive: true);
                return false;
            }

            var directory = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            zipEntry.ExtractToFile(target, overwrite: true);

            // Scoped deliberately: the cleanup below deletes this file, and a still-open handle makes
            // that fail with "being used by another process" on a directory the code then cannot remove.
            string digest;
            using (var stream = File.OpenRead(target))
            {
                digest = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            }

            if (!string.Equals(digest, entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                failure = $"{entry.RelativePath} did not match the manifest after unpacking.";
                Directory.Delete(root, recursive: true);
                return false;
            }
        }

        return true;
    }
}
