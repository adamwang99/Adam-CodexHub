using AdamCodexHub.Core.Domain;

namespace AdamCodexHub.Core.Interfaces;

public interface ICompatibilityService
{
    Task<CompatibilityResult> TestAsync(
        string providerId,
        string modelId,
        CancellationToken cancellationToken = default);
}
