using AdamCodexHub.Core.Domain;
using AdamCodexHub.Core.Interfaces;

namespace AdamCodexHub.Providers;

public sealed class KeyTestService : IKeyTestService
{
    private readonly IProviderManager _providers;
    private readonly IKeyPoolService _keys;
    private readonly IKeyVault _vault;
    private readonly IEnumerable<IProviderAdapter> _adapters;

    public KeyTestService(
        IProviderManager providers,
        IKeyPoolService keys,
        IKeyVault vault,
        IEnumerable<IProviderAdapter> adapters)
    {
        _providers = providers;
        _keys = keys;
        _vault = vault;
        _adapters = adapters;
    }

    public async Task<ProviderProbeResult> TestAsync(
        string providerId,
        string keyId,
        CancellationToken cancellationToken = default)
    {
        var provider = await _providers.GetAsync(providerId, cancellationToken)
            ?? throw new InvalidOperationException($"Unknown provider '{providerId}'.");
        var key = (await _keys.ListAsync(provider.Id, cancellationToken))
            .FirstOrDefault(x => string.Equals(x.Id, keyId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Unknown API key '{keyId}'.");
        var adapter = _adapters.FirstOrDefault(x =>
            string.Equals(x.AdapterId, provider.Adapter, StringComparison.OrdinalIgnoreCase))
            ?? throw new NotSupportedException(
                $"No adapter is registered for '{provider.Adapter}'.");
        var secret = await _vault.RetrieveAsync(key.SecretReference, cancellationToken)
            ?? throw new InvalidOperationException("The protected API key could not be retrieved.");

        var result = await adapter.ProbeAsync(provider, secret, cancellationToken);
        if (result.Success)
        {
            await _keys.MarkSuccessAsync(key.Id, cancellationToken);
            return result;
        }

        var (health, cooldown) = Classify(result.Summary);
        await _keys.MarkFailureAsync(
            key.Id,
            health,
            result.Summary,
            cooldown,
            cancellationToken);
        return result;
    }

    private static (KeyHealth Health, TimeSpan? Cooldown) Classify(string summary)
    {
        if (summary.Contains("401", StringComparison.OrdinalIgnoreCase) ||
            summary.Contains("unauthorized", StringComparison.OrdinalIgnoreCase))
        {
            return (KeyHealth.Unauthorized, null);
        }

        if (summary.Contains("402", StringComparison.OrdinalIgnoreCase) ||
            summary.Contains("quota", StringComparison.OrdinalIgnoreCase))
        {
            return (KeyHealth.QuotaEmpty, null);
        }

        if (summary.Contains("429", StringComparison.OrdinalIgnoreCase) ||
            summary.Contains("rate limit", StringComparison.OrdinalIgnoreCase))
        {
            return (KeyHealth.Cooldown, TimeSpan.FromMinutes(1));
        }

        return (KeyHealth.Offline, TimeSpan.FromSeconds(30));
    }
}
