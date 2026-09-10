namespace AdamCodexHub.Core.Domain;

/// <summary>
/// Which model the newest Codex session is actually set to.
///
/// Codex Desktop's own "Select model" picker does NOT write <c>~/.codex/config.toml</c> — it
/// stores the choice per thread in <c>~/.codex/state_5.sqlite</c> (<c>threads.model</c>). So the
/// hub can only follow an in-app model switch by reading that state back, which is what
/// <see cref="Interfaces.ICodexSessionModelReader"/> does.
/// </summary>
/// <param name="ModelId">Model id Codex will run the next turn with (e.g. <c>claude-5.5</c>).</param>
/// <param name="ThreadId">Newest Codex thread the value came from, when known.</param>
/// <param name="ProviderId">Codex model provider id of that thread (normally <c>adam_codexhub</c>).</param>
/// <param name="ObservedAt">When Codex last touched that thread (local time), when known.</param>
/// <param name="Source">Where the value came from: <c>codex-session</c> (Codex state) or
/// <c>hub-activation</c> (the hub just wrote the model and Codex has not caught up yet).</param>
public sealed record CodexSessionModel(
    string ModelId,
    string? ThreadId = null,
    string? ProviderId = null,
    DateTimeOffset? ObservedAt = null,
    string Source = CodexSessionModel.CodexSessionSource)
{
    /// <summary>Value read back from Codex's own session state.</summary>
    public const string CodexSessionSource = "codex-session";

    /// <summary>Value the hub itself just pushed (Codex has not reflected it yet).</summary>
    public const string HubActivationSource = "hub-activation";

    /// <summary>True when the value was read from Codex rather than written by the hub.</summary>
    public bool FromCodex => string.Equals(Source, CodexSessionSource, StringComparison.Ordinal);
}
