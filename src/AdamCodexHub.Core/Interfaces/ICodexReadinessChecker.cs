using AdamCodexHub.Core.Domain;

namespace AdamCodexHub.Core.Interfaces;

/// <summary>Runs the Codex-shaped readiness probe for one model. See <see cref="Services.CodexReadinessChecker"/>.</summary>
public interface ICodexReadinessChecker
{
    Task<CodexReadiness> CheckAsync(
        string providerId,
        string modelId,
        CancellationToken cancellationToken = default);
}
