using AdamCodexHub.Core.Domain;
using AdamCodexHub.Core.Interfaces;

namespace AdamCodexHub.Providers;

public sealed class ModelDiscoveryService : IModelDiscoveryService
{
    private readonly IProviderManager _providers;
    private readonly IKeyPoolService _keys;
    private readonly IModelStore _models;
    private readonly IEnumerable<IProviderAdapter> _adapters;

    public ModelDiscoveryService(
        IProviderManager providers,
        IKeyPoolService keys,
        IModelStore models,
        IEnumerable<IProviderAdapter> adapters)
    {
        _providers = providers;
        _keys = keys;
        _models = models;
        _adapters = adapters;
    }

    public async Task<IReadOnlyList<ModelDescriptor>> ScanAsync(
        string providerId,
        CancellationToken cancellationToken = default)
    {
        var provider = await _providers.GetAsync(providerId, cancellationToken)
            ?? throw new InvalidOperationException($"Unknown provider '{providerId}'.");

        if (!provider.Enabled)
        {
            throw new InvalidOperationException($"Provider '{provider.Name}' is disabled.");
        }

        var adapter = _adapters.FirstOrDefault(a =>
            string.Equals(a.AdapterId, provider.Adapter, StringComparison.OrdinalIgnoreCase))
            ?? throw new NotSupportedException(
                $"No adapter is registered for '{provider.Adapter}'.");

        var key = await _keys.GetActiveSecretAsync(provider.Id, cancellationToken);
        var discovered = await adapter.ListModelsAsync(provider, key, cancellationToken);
        await _providers.SetHealthAsync(provider.Id, ProviderHealth.Healthy, cancellationToken);
        var existing = (await _models.GetAllAsync(provider.Id, cancellationToken))
            .ToDictionary(x => x.RemoteId, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var now = DateTimeOffset.UtcNow;

        foreach (var remote in discovered)
        {
            if (string.IsNullOrWhiteSpace(remote.RemoteId) || !seen.Add(remote.RemoteId))
            {
                continue;
            }

            existing.TryGetValue(remote.RemoteId, out var saved);
            var merged = remote with
            {
                ProviderId = provider.Id,
                DisplayName = string.IsNullOrWhiteSpace(remote.DisplayName)
                    ? remote.RemoteId
                    : remote.DisplayName,
                Enabled = saved?.Enabled ?? false,
                State = saved switch
                {
                    { Enabled: true } => ModelLifecycleState.Enabled,
                    { LastVerifiedAt: not null, CompatibilityScore: > 0 } => ModelLifecycleState.Verified,
                    _ => ModelLifecycleState.Discovered
                },
                InputModalities = remote.InputModalities.Count > 0
                    ? remote.InputModalities
                    : saved?.InputModalities ?? new[] { "text" },
                Capabilities = remote.Capabilities.Count > 0
                    ? remote.Capabilities
                    : saved?.Capabilities ?? Array.Empty<string>(),
                ContextWindow = remote.ContextWindow ?? saved?.ContextWindow,
                LastSeenAt = now,
                LastVerifiedAt = saved?.LastVerifiedAt,
                CompatibilityScore = saved?.CompatibilityScore
            };

            await _models.UpsertAsync(merged, cancellationToken);
        }

        await _models.MarkUnavailableExceptAsync(provider.Id, seen, cancellationToken);
        return await _models.GetAllAsync(provider.Id, cancellationToken);
    }
}
