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
}
