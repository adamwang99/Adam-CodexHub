using AdamCodexHub.Core.Domain;

namespace AdamCodexHub.Core.Interfaces;

public interface IModelDiscoveryService
{
    Task<IReadOnlyList<ModelDescriptor>> ScanAsync(
        string providerId,
        CancellationToken cancellationToken = default);
}
