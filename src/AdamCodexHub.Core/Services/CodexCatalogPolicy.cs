using AdamCodexHub.Core.Domain;

namespace AdamCodexHub.Core.Services;

/// <summary>
/// One shared rule for "which models may be offered to Codex", used by the local gateway
/// (<c>GET /v1/models</c> app-server shape) and by the tray model submenu so the two can never
/// disagree.
/// </summary>
public static class CodexCatalogPolicy
{
    /// <summary>
    /// Keeps only the enabled models that carry a fresh <see cref="CodexReadiness.Ready"/> verdict.
    /// When nothing has been verified yet (fresh install, provider just switched, hub restarted
    /// before the first check) the full enabled set is returned so the Codex picker is never
    /// empty — the checker then removes the failing models within its next tick.
    /// </summary>
    public static IReadOnlyList<string> SelectPublished(
        IEnumerable<string> enabledModelIds,
        IEnumerable<CodexReadiness> verdicts,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(enabledModelIds);
        ArgumentNullException.ThrowIfNull(verdicts);

        var enabled = enabledModelIds.Distinct(StringComparer.Ordinal).ToArray();
        if (enabled.Length == 0)
        {
            return enabled;
        }

        var ready = verdicts
            .Where(v => v.Ready && v.IsFresh(now))
            .Select(v => v.ModelId)
            .ToHashSet(StringComparer.Ordinal);

        var published = enabled.Where(ready.Contains).ToArray();
        return published.Length > 0 ? published : enabled;
    }

    /// <summary>True when the model may be offered to Codex right now.</summary>
    public static bool IsPublishable(
        string modelId,
        IEnumerable<CodexReadiness> verdicts,
        DateTimeOffset now) =>
        verdicts.Any(v =>
            string.Equals(v.ModelId, modelId, StringComparison.Ordinal) &&
            v.Ready &&
            v.IsFresh(now));
}
