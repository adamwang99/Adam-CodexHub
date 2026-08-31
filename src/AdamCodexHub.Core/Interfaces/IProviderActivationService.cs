using AdamCodexHub.Core.Domain;

namespace AdamCodexHub.Core.Interfaces;

public interface IProviderActivationService
{
    Task<ProviderActivationResult> ActivateAsync(
        string providerId,
        string? modelId,
        string? projectPath = null,
        CancellationToken cancellationToken = default);
}
