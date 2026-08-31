using AdamCodexHub.Core.Domain;
using AdamCodexHub.Core.Interfaces;

namespace AdamCodexHub.Providers;

public sealed class CompatibilityService : ICompatibilityService
{
    private readonly IProviderManager _providers;
    private readonly IKeyPoolService _keys;
    private readonly IModelStore _models;
    private readonly IEnumerable<IProviderAdapter> _adapters;

    public CompatibilityService(
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

    public async Task<CompatibilityResult> TestAsync(
        string providerId,
        string modelId,
        CancellationToken cancellationToken = default)
    {
        var provider = await _providers.GetAsync(providerId, cancellationToken)
            ?? throw new InvalidOperationException($"Unknown provider '{providerId}'.");
        var model = await _models.GetAsync(provider.Id, modelId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Unknown model '{modelId}' for provider '{provider.Name}'.");

        if (!provider.Enabled)
        {
            throw new InvalidOperationException($"Provider '{provider.Name}' is disabled.");
        }

        if (model.State == ModelLifecycleState.Unavailable)
        {
            throw new InvalidOperationException($"Model '{model.DisplayName}' is unavailable.");
        }

        var adapter = _adapters.FirstOrDefault(x =>
            string.Equals(x.AdapterId, provider.Adapter, StringComparison.OrdinalIgnoreCase))
            ?? throw new NotSupportedException(
                $"No adapter is registered for '{provider.Adapter}'.");
        var key = await _keys.GetActiveSecretAsync(provider.Id, cancellationToken);
        var tested = await adapter.TestModelAsync(provider, model.RemoteId, key, cancellationToken);
        var result = tested with
        {
            ProviderId = provider.Id,
            ModelId = model.RemoteId,
            VerifiedAt = DateTimeOffset.UtcNow,
            Score = Math.Clamp(tested.Score, 0, 100)
        };

        await _models.SaveCompatibilityAsync(result, cancellationToken);
        return result;
    }
}
