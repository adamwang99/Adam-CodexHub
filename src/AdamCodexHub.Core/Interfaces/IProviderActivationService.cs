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
}
