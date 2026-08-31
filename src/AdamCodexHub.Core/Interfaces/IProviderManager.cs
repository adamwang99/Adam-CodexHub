using AdamCodexHub.Core.Domain;

namespace AdamCodexHub.Core.Interfaces;

public interface IProviderManager
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProviderProfile>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<ProviderProfile?> GetAsync(string providerId, CancellationToken cancellationToken = default);
    Task SaveAsync(ProviderProfile provider, CancellationToken cancellationToken = default);
    Task SetEnabledAsync(string providerId, bool enabled, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(string providerId, CancellationToken cancellationToken = default);
    Task SetActiveAsync(string providerId, CancellationToken cancellationToken = default);
    Task<ProviderProfile?> GetActiveAsync(CancellationToken cancellationToken = default);
}
