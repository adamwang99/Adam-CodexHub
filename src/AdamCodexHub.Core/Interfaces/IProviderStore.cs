using AdamCodexHub.Core.Domain;

namespace AdamCodexHub.Core.Interfaces;

public interface IProviderStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProviderProfile>> GetAllAsync(CancellationToken cancellationToken = default);
    Task UpsertAsync(ProviderProfile provider, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(string providerId, CancellationToken cancellationToken = default);
    Task<string?> GetActiveProviderIdAsync(CancellationToken cancellationToken = default);
    Task SetActiveProviderIdAsync(string providerId, CancellationToken cancellationToken = default);
}
