using AdamCodexHub.Core.Domain;

namespace AdamCodexHub.Core.Interfaces;

/// <summary>
/// Persists per-model <see cref="CodexReadiness"/> verdicts so the Codex catalog the gateway
/// serves, the tray model list and the in-app badges all read the same answer, and so a verdict
/// survives an app restart.
/// </summary>
public interface ICodexReadinessStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CodexReadiness>> GetAllAsync(
        string providerId,
        CancellationToken cancellationToken = default);

    Task<CodexReadiness?> GetAsync(
        string providerId,
        string modelId,
        CancellationToken cancellationToken = default);

    /// <summary>Inserts or replaces the verdict for (provider, model).</summary>
    Task SaveAsync(CodexReadiness readiness, CancellationToken cancellationToken = default);
}
