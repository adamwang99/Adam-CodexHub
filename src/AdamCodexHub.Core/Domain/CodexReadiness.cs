namespace AdamCodexHub.Core.Domain;

/// <summary>
/// Verdict of the "Codex-shaped" readiness probe for one model of one provider: did the model
/// answer a real Codex request (instructions + <c>tools</c>, <c>tool_choice = required</c>) with
/// an actual tool call?
///
/// Why this exists: provider catalogs (HHTech mixes OpenAI/Codex and Claude entries, several of
/// them renamed with a <c>claude-</c> prefix) advertise models that can answer a plain chat but
/// fail the tool-driven request Codex Desktop sends. The hub publishes its model list to Codex
/// through the local gateway, so a model without a fresh <see cref="Ready"/> verdict must not be
/// offered there — the picker would otherwise show models that immediately error.
/// </summary>
public sealed record CodexReadiness(
    string ProviderId,
    string ModelId,
    bool Ready,
    DateTimeOffset CheckedAt,
    int? LatencyMs = null,
    string? Detail = null)
{
    /// <summary>How long a verdict stays trustworthy before the checker re-runs it.</summary>
    public static readonly TimeSpan FreshFor = TimeSpan.FromHours(6);

    /// <summary>True when the verdict is recent enough to drive the Codex catalog.</summary>
    public bool IsFresh(DateTimeOffset now) => now - CheckedAt <= FreshFor;
}
