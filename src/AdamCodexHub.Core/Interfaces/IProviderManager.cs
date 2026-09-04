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

    /// <summary>
    /// Records the outcome of a connectivity/health probe against a provider so
    /// the UI can show a real status instead of the default "Unknown".
    /// </summary>
    Task SetHealthAsync(
        string providerId,
        ProviderHealth health,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Messages recorded while loading persisted providers. A provider that no
    /// longer satisfies current validation rules is skipped so startup can
    /// continue, and the reason is reported here.
    /// </summary>
    IReadOnlyList<string> StartupWarnings { get; }
}
