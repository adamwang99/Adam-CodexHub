using AdamCodexHub.Core.Domain;

namespace AdamCodexHub.Core.Interfaces;

public interface ICompatibilityService
{
    Task<CompatibilityResult> TestAsync(
        string providerId,
        string modelId,
        IProgress<ModelTestProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
