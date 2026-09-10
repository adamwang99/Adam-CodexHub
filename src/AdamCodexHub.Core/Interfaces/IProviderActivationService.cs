using AdamCodexHub.Core.Domain;

namespace AdamCodexHub.Core.Interfaces;

public interface IProviderActivationService
{
    Task<ProviderActivationResult> ActivateAsync(
        string providerId,
        string? modelId,
        string? projectPath = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Activates a keyed provider for the Codex DESKTOP app: same as <see cref="ActivateAsync"/>
    /// plus an overlay on the real <c>~/.codex/config.toml</c> pointing model traffic at the
    /// in-process gateway (the Desktop app cannot consume sandboxed CODEX_HOME homes). Codex
    /// Account activation goes through <see cref="ActivateAsync"/> alone.
    /// </summary>
    Task<ProviderActivationResult> ActivateDesktopAsync(
        string providerId,
        string? modelId,
        string? projectPath = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Repairs the model pins of Codex threads left behind by whichever provider ran before, for the
    /// provider that is active now. Called at startup as well as on activation: Codex pins a model per
    /// thread, so without this a restart alone leaves the open chats refusing the active provider's
    /// models ("The 'claude-opus-4-8[1M]' model is not supported when using Codex with a ChatGPT
    /// account") and the user has to start a new chat. Returns how many threads were moved.
    /// </summary>
    Task<int> RepairThreadsForActiveProviderAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The models the Codex picker actually offers for a provider right now — enabled and carrying a
    /// current verdict, the quickest probe first (the same rule the gateway serves, so this can never
    /// disagree with the list Codex shows).
    /// </summary>
    Task<IReadOnlyList<string>> PublishedModelsAsync(
        string providerId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The model to aim the desktop overlay at. Writing an id the catalogue does not offer is what
    /// makes Codex read "Custom" — and then switch the session by itself ("Model changed from Custom
    /// to claude-opus-4-7 (F)") the moment the offered list changes.
    /// </summary>
    Task<string?> PreferredDesktopModelAsync(
        string providerId,
        CancellationToken cancellationToken = default);
}
