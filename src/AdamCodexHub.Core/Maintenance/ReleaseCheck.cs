using System.Runtime.InteropServices;
using System.Text.Json;

namespace AdamCodexHub.Core.Maintenance;

/// <summary>
/// Whether a newer release exists on GitHub. There is deliberately no auto-update here: the app is
/// unsigned and downloads 90–130 MB, so silently replacing itself is the wrong trade. What was
/// missing is smaller and more honest — a person running v1.5.3 had no way at all to learn that
/// v1.5.4 fixed their problem, which meant every fix had to be hand-delivered by Adam.
///
/// Design notes: the check is explicit (a button, never a background poll), it asks for exactly one
/// URL, it never sends anything about the user, and a failure is a status message rather than an
/// error dialog — an unreachable GitHub must never look like a broken hub.
/// </summary>
public static class ReleaseCheck
{
    /// <summary>The public releases endpoint for this repository — no token, no user data.</summary>
    public const string LatestReleaseUrl =
        "https://api.github.com/repos/adamwang99/Adam-CodexHub/releases/latest";

    /// <summary>What a check found. <see cref="IsNewer"/> is the only thing the UI has to act on.</summary>
    public sealed record Result(
        bool Succeeded,
        bool IsNewer,
        string? LatestVersion,
        string? ReleaseUrl,
        string? Failure);

    /// <summary>
    /// Compares <paramref name="runningVersion"/> with the tag in a GitHub "latest release" payload.
    /// Kept separate from the HTTP call so the comparison is testable without a network.
    /// </summary>
    public static Result Evaluate(string runningVersion, string releaseJson)
    {
        try
        {
            using var document = JsonDocument.Parse(releaseJson);
            var tag = document.RootElement.TryGetProperty("tag_name", out var tagName)
                ? tagName.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(tag))
            {
                return new Result(false, false, null, null, "the release feed carried no tag");
            }

            var url = document.RootElement.TryGetProperty("html_url", out var htmlUrl)
                ? htmlUrl.GetString()
                : null;

            var latest = ParseVersion(tag);
            var running = ParseVersion(runningVersion);
            if (latest is null || running is null)
            {
                return new Result(false, false, tag.TrimStart('v'), url, "the version numbers could not be read");
            }

            return new Result(true, latest > running, tag.TrimStart('v'), url, null);
        }
        catch (JsonException)
        {
            return new Result(false, false, null, null, "the release feed could not be read");
        }
    }

    /// <summary>
    /// Accepts what both sides really contain: a tag like <c>v1.5.4</c> and an informational version
    /// like <c>1.5.4+945e6d2…</c>. Anything after the numbers is build metadata and is ignored, so a
    /// local build never reads as "newer" or "older" than the release it came from.
    /// </summary>
    private static Version? ParseVersion(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var text = raw.Trim().TrimStart('v', 'V');
        var cut = text.IndexOfAny(new[] { '+', '-' });
        if (cut > 0)
        {
            text = text[..cut];
        }

        return Version.TryParse(text, out var version) ? version : null;
    }

    /// <summary>
    /// The installer a release offers, plus the checksum published next to it. Null when the release
    /// carries no Setup asset — a release built on a runner without Inno Setup is ZIP-only, and the
    /// honest answer there is "nothing to fetch", not a broken download button.
    /// </summary>
    public sealed record SetupDownload(
        string FileName,
        string DownloadUrl,
        string? ChecksumUrl,
        long SizeBytes);

    /// <summary>
    /// The runtime identifier of the process doing the asking, so a release that ships installers for
    /// more than one architecture can be matched to the machine actually running the hub.
    /// </summary>
    public static string CurrentRuntimeIdentifier =>
        RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";

    /// <summary>
    /// Reads the Setup asset (and its <c>.sha256</c> sibling) out of a release payload.
    ///
    /// Nothing here is pinned to a version: the asset name is matched by shape, so the release that
    /// comes after this one is handled by the same rule. The architecture is checked, though — the
    /// packaging script emits <c>AdamCodexHub-Setup-v&lt;version&gt;-&lt;rid&gt;.exe</c> for both
    /// <c>win-x64</c> and <c>win-arm64</c>, and handing an ARM installer to an x64 machine (or taking
    /// whichever the JSON happened to list first) would be worse than offering nothing.
    ///
    /// Returns null when the release carries no installer for this architecture — the honest answer
    /// there is "no download button" rather than an installer that cannot run.
    /// </summary>
    public static SetupDownload? FindSetupDownload(string releaseJson, string? runtimeIdentifier = null)
    {
        var wanted = string.IsNullOrWhiteSpace(runtimeIdentifier) ? CurrentRuntimeIdentifier : runtimeIdentifier;

        try
        {
            using var document = JsonDocument.Parse(releaseJson);
            if (!document.RootElement.TryGetProperty("assets", out var assets) ||
                assets.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var found = new List<(string Name, string Url, long Size)>();
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
                var url = asset.TryGetProperty("browser_download_url", out var urlElement)
                    ? urlElement.GetString()
                    : null;
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url))
                {
                    continue;
                }

                var size = asset.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var parsed)
                    ? parsed
                    : 0L;
                found.Add((name, url, size));
            }

            var setup = found.FirstOrDefault(asset =>
                asset.Name.StartsWith("AdamCodexHub-Setup-", StringComparison.OrdinalIgnoreCase) &&
                asset.Name.EndsWith("-" + wanted + ".exe", StringComparison.OrdinalIgnoreCase));
            if (setup.Name is null)
            {
                return null;
            }

            var checksum = found.FirstOrDefault(asset =>
                asset.Name.Equals(setup.Name + ".sha256", StringComparison.OrdinalIgnoreCase));

            return new SetupDownload(setup.Name, setup.Url, checksum.Url, setup.Size);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The small update package for this architecture, if the release carries one. Same shape as the
    /// installer lookup and matched the same way — by name shape and architecture, never by version —
    /// so the release after this one is handled by the same rule.
    /// </summary>
    public static SetupDownload? FindUpdatePackage(string releaseJson, string? runtimeIdentifier = null)
    {
        var wanted = string.IsNullOrWhiteSpace(runtimeIdentifier) ? CurrentRuntimeIdentifier : runtimeIdentifier;

        try
        {
            using var document = JsonDocument.Parse(releaseJson);
            if (!document.RootElement.TryGetProperty("assets", out var assets) ||
                assets.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var found = new List<(string Name, string Url, long Size)>();
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
                var url = asset.TryGetProperty("browser_download_url", out var urlElement)
                    ? urlElement.GetString()
                    : null;
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url))
                {
                    continue;
                }

                var size = asset.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var parsed)
                    ? parsed
                    : 0L;
                found.Add((name, url, size));
            }

            var package = found.FirstOrDefault(asset =>
                asset.Name.StartsWith("AdamCodexHub-update-", StringComparison.OrdinalIgnoreCase) &&
                asset.Name.EndsWith("-" + wanted + ".zip", StringComparison.OrdinalIgnoreCase));
            if (package.Name is null)
            {
                return null;
            }

            var checksum = found.FirstOrDefault(asset =>
                asset.Name.Equals(package.Name + ".sha256", StringComparison.OrdinalIgnoreCase));

            return new SetupDownload(package.Name, package.Url, checksum.Url, package.Size);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>What a finished transfer turned out to be.</summary>
    public enum DownloadOutcome
    {
        /// <summary>Every byte arrived and the SHA-256 matched the published one.</summary>
        Verified,

        /// <summary>Every byte arrived, but the release published no checksum to compare against.</summary>
        CompleteButUnverified,

        /// <summary>Fewer bytes arrived than the release says the file has.</summary>
        Incomplete,

        /// <summary>Every byte arrived, and they are not the published file.</summary>
        ChecksumMismatch
    }

    /// <summary>
    /// Decides what to do with a finished transfer. Kept separate from the download itself because the
    /// distinction matters to whoever reads the message: a truncated transfer is a network problem
    /// worth simply retrying, while a checksum mismatch on a complete file is a much more serious
    /// claim about the file itself, and the two must never be reported as the same thing.
    ///
    /// Measured 2026-09-11: a test download of the real v1.5.4 installer stopped at 20.0 MB of 91.0 MB
    /// when the connection dropped, and the code as first written would have called that "does NOT
    /// match its SHA-256" — blaming the file for what the network did.
    /// </summary>
    public static DownloadOutcome JudgeDownload(
        long expectedBytes,
        long receivedBytes,
        string? publishedChecksum,
        string? computedHash)
    {
        if (expectedBytes > 0 && receivedBytes != expectedBytes)
        {
            return DownloadOutcome.Incomplete;
        }

        if (string.IsNullOrWhiteSpace(publishedChecksum))
        {
            return DownloadOutcome.CompleteButUnverified;
        }

        return ChecksumMatches(publishedChecksum, computedHash)
            ? DownloadOutcome.Verified
            : DownloadOutcome.ChecksumMismatch;
    }

    /// <summary>
    /// Compares a published checksum line (<c>&lt;hex&gt;  &lt;filename&gt;</c>, the format
    /// <c>Get-FileHash</c> and the packaging script both write) against a computed digest. Only the
    /// hex token is compared, and case does not matter.
    /// </summary>
    public static bool ChecksumMatches(string? publishedLine, string? computedHex)
    {
        if (string.IsNullOrWhiteSpace(publishedLine) || string.IsNullOrWhiteSpace(computedHex))
        {
            return false;
        }

        var expected = publishedLine
            .Trim()
            .Split(new[] { ' ', '\t', '*' }, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(expected))
        {
            return false;
        }

        return string.Equals(expected, computedHex.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
