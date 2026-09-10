using AdamCodexHub.Core.Domain;
using AdamCodexHub.Core.Interfaces;

namespace AdamCodexHub.Core.Services;

/// <summary>
/// Re-probes the keys of a provider that a quota outage (or a provider-side glitch) marked
/// unusable, and re-reads its catalogue when a key answers again.
///
/// Deliberate rules:
/// <list type="bullet">
/// <item>at most <see cref="MaxKeyProbes"/> keys per attempt, cheapest first, one at a time,</item>
/// <item>a key is never re-probed faster than <see cref="DefaultSpacing"/> unless the caller says
/// the attempt is urgent (the user just tried to activate that very provider),</item>
/// <item>a provider-side cooldown is respected except on an urgent attempt,</item>
/// <item>nothing here is fatal: a failed probe leaves the persisted state exactly as it was.</item>
/// </list>
/// </summary>
public sealed class ProviderRecoveryService : IProviderRecoveryService
{
    /// <summary>How many keys one attempt may spend.</summary>
    public const int MaxKeyProbes = 2;

    /// <summary>Spacing between two probes of the same key on background attempts.</summary>
    public static readonly TimeSpan DefaultSpacing = TimeSpan.FromMinutes(30);

    private readonly IProviderManager _providers;
    private readonly IKeyPoolService _keys;
    private readonly IKeyTestService _tester;
    private readonly IModelDiscoveryService _discovery;

    public ProviderRecoveryService(
        IProviderManager providers,
        IKeyPoolService keys,
        IKeyTestService tester,
        IModelDiscoveryService discovery)
    {
        _providers = providers;
        _keys = keys;
        _tester = tester;
        _discovery = discovery;
    }

    /// <summary>Spacing between two probes of the same key. Tests may shorten it.</summary>
    public TimeSpan Spacing { get; init; } = DefaultSpacing;

    public async Task<bool> TryRecoverAsync(
        string providerId,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return false;
        }

        var provider = (await _providers.GetAllAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(p => string.Equals(p.Id, providerId, StringComparison.OrdinalIgnoreCase));
        if (provider is null || !provider.Enabled)
        {
            return false;
        }

        var keys = await _keys.ListAsync(providerId, cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;

        if (keys.Any(key => key.Enabled && key.Health == KeyHealth.Healthy))
        {
            // The provider already has a working key, so whatever failed was not the key: leave the
            // parked keys alone and let the caller keep its own state.
            return true;
        }

        var probeable = keys
            .Where(key => key.Enabled)
            .Where(key => key.Health != KeyHealth.Healthy && key.Health != KeyHealth.Disabled)
            .Where(key => force || !key.LastTestAt.HasValue || now - key.LastTestAt!.Value >= Spacing)
            .Where(key => force || !key.CooldownUntil.HasValue || key.CooldownUntil!.Value <= now)
            .OrderBy(key => key.Priority)
            .Take(MaxKeyProbes)
            .ToArray();

        foreach (var key in probeable)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // On success the tester marks the key Healthy and the provider Healthy; on failure
                // the persisted state is left alone so the next attempt (or a manual test) can retry.
                await _tester.TestAsync(providerId, key.Id, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Keep whatever the tester managed to persist.
            }
        }

        if (probeable.Length == 0)
        {
            // Every key was probed too recently, is on a provider-side cooldown, or is switched off.
            // Nothing to learn by asking again right now.
            return false;
        }

        var after = await _keys.ListAsync(providerId, cancellationToken).ConfigureAwait(false);
        if (!after.Any(key => key.Enabled && key.Health == KeyHealth.Healthy))
        {
            return false;
        }

        try
        {
            await _discovery.ScanAsync(providerId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // The key is back even if the catalogue read failed; the next sweep will re-read it.
        }

        return true;
    }
}
