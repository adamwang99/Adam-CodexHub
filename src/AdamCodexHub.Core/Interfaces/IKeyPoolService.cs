using AdamCodexHub.Core.Domain;

namespace AdamCodexHub.Core.Interfaces;

public interface IKeyPoolService
{
    Task<ProviderKeyInfo> AddAsync(
        string providerId,
        string label,
        string secret,
        int priority = 100,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ProviderKeyInfo>> ListAsync(
        string providerId,
        CancellationToken cancellationToken = default);

    Task<ProviderKeySelection?> GetActiveAsync(
        string providerId,
        IReadOnlySet<string>? excludedKeyIds = null,
        CancellationToken cancellationToken = default);

    Task<string?> GetActiveSecretAsync(
        string providerId,
        CancellationToken cancellationToken = default);

    Task SetEnabledAsync(
        string keyId,
        bool enabled,
        CancellationToken cancellationToken = default);

    Task ReorderAsync(
        string providerId,
        IReadOnlyList<string> orderedKeyIds,
        CancellationToken cancellationToken = default);

    Task MarkSuccessAsync(
        string keyId,
        CancellationToken cancellationToken = default);

    Task MarkFailureAsync(
        string keyId,
        KeyHealth health,
        string? reason,
        TimeSpan? cooldown = null,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(
        string keyId,
        CancellationToken cancellationToken = default);
}
