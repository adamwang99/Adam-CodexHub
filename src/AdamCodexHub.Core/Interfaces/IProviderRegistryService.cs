using AdamCodexHub.Core.Domain;

namespace AdamCodexHub.Core.Interfaces;

public interface IProviderRegistryService
{
    Task<IReadOnlyList<ProviderProfile>> GetBuiltInAsync(
        CancellationToken cancellationToken = default);
}
