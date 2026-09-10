using AdamCodexHub.Core.Domain;
using AdamCodexHub.Core.Interfaces;
using AdamCodexHub.Core.Services;

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
        IProgress<ModelTestProgress>? progress = null,
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
        if (string.IsNullOrWhiteSpace(key))
        {
            // Every key is parked, disabled, or waiting out a rate-limit cooldown. Probing anyway
            // answers 401 and would be stored as "this model cannot serve a request", hiding a working
            // model for six hours — so the provider is reported as having nothing to spend, and the
            // caller leaves it alone until a key comes back.
            throw new ProviderKeyUnavailableException(
                $"no usable API key for '{provider.Name}' right now");
        }

        // Mark the probe as in flight so the background auto-ping never probes the same model
        // at the same time as this (manual) verification.
        using var probeLease = ModelProbeGate.Enter(provider.Id, model.RemoteId);
        var tested = await adapter.TestModelAsync(provider, model.RemoteId, key, progress, cancellationToken);
        await _providers.SetHealthAsync(
            provider.Id,
            tested.Score > 0 ? ProviderHealth.Healthy : ProviderHealth.Warning,
            cancellationToken);
        var result = tested with
        {
            ProviderId = provider.Id,
            ModelId = model.RemoteId,
            VerifiedAt = DateTimeOffset.UtcNow,
            Score = Math.Clamp(tested.Score, 0, 100)
        };

        // A 5xx/429 that answered every probe is the provider pushing back, not a measurement. Storing
        // it would mark a working model unusable until the verdict lapses (6 h) and keep it out of the
        // Codex catalogue — the hub would then look like it has nothing to offer, which is exactly the
        // "wall of (U)" a user sees after a provider hiccup. The stored verdict stands instead.
        if (IsProviderRefusal(result))
        {
            return result;
        }

        await _models.SaveCompatibilityAsync(result, cancellationToken);
        return result;
    }

    /// <summary>
    /// True when the probe never got an answer about the model: the provider refused (5xx/429) or could
    /// not be reached at all (DNS, connection, timeout). Storing that would mark working models
    /// unusable — on 2026-09-11 a momentary failure to resolve `hhtechapi.com` was written as a score-0
    /// verdict for every HHTech model, so the gateway answered "Model … is not enabled for provider"
    /// to every request and the session fell apart. The notes are about the provider, not the model.
    /// </summary>
    /// <summary>
    /// True when the probe never got an answer about the model: the provider refused (5xx/429) or could
    /// not be reached at all (DNS, connection, timeout). Storing that would mark working models
    /// unusable — on 2026-09-11 a momentary failure to resolve `hhtechapi.com` was written as a score-0
    /// verdict for every HHTech model, so the gateway answered "Model … is not enabled for provider"
    /// to every request and the session fell apart. The notes are about the provider, not the model.
    /// </summary>
    private static bool IsProviderRefusal(CompatibilityResult result)
    {
        if (result.Score > 0 || result.Text || result.Responses || result.Streaming)
        {
            return false;
        }

        return ProviderTrouble.IsNotAMeasurement(result.Notes);
    }
}
