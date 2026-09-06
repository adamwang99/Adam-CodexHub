using AdamCodexHub.Core.Domain;
using AdamCodexHub.Core.Interfaces;
using AdamCodexHub.Providers;

namespace AdamCodexHub.App.Services;

/// <summary>
/// Quietly re-verifies previously-tested provider keys after an application restart.
/// A transient gateway failure in the previous session leaves a key persisted as Offline
/// (or RateLimited/Cooldown), which would otherwise make an installed, working provider look
/// broken until the user manually presses "Test" in the provider page. This sweep gives those
/// keys one probe each so healthy providers come back on their own. Hard rejections
/// (401 Unauthorized, 402 quota, Disabled) are never auto-retried — they need user attention.
/// </summary>
public sealed class StartupKeyRevalidator
{
    private readonly IProviderManager _providers;
    private readonly IKeyPoolService _keys;
    private readonly IKeyTestService _tester;

    public StartupKeyRevalidator(
        IProviderManager providers,
        IKeyPoolService keys,
        IKeyTestService tester)
    {
        _providers = providers;
        _keys = keys;
        _tester = tester;
    }

    public async Task RevalidateAsync(CancellationToken cancellationToken = default)
    {
        var providers = await _providers.GetAllAsync(cancellationToken);

        foreach (var provider in providers.Where(p =>
                     p.Id != ProviderManager.CodexAccountProviderId && p.Enabled))
        {
            IReadOnlyList<ProviderKeyInfo> keys;
            try
            {
                keys = await _keys.ListAsync(provider.Id, cancellationToken);
            }
            catch
            {
                continue;
            }

            foreach (var key in keys.Where(k => ShouldRevalidate(k)))
            {
                try
                {
                    // On success this marks the key Healthy and the provider Healthy; on failure
                    // the persisted state is left untouched so the next start (or a manual test)
                    // can retry.
                    await _tester.TestAsync(provider.Id, key.Id, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch
                {
                    // Keep whatever state was persisted — a manual test remains available.
                }
            }
        }
    }

    private static bool ShouldRevalidate(ProviderKeyInfo key)
    {
        if (!key.Enabled || !key.LastTestAt.HasValue)
        {
            // Never-tested keys are left for the user to verify deliberately.
            return false;
        }

        if (key.Health is KeyHealth.Unauthorized or KeyHealth.QuotaEmpty or KeyHealth.Disabled)
        {
            // Hard rejections must not be silently retried.
            return false;
        }

        if (key.CooldownUntil.HasValue && key.CooldownUntil.Value > DateTimeOffset.UtcNow)
        {
            // A provider-side rate-limit back-off is still active; probing now would just fail.
            return false;
        }

        // Offline / RateLimited / Cooldown / Unknown from a previous run are all worth one
        // quiet probe. Healthy keys are skipped (nothing to heal).
        return key.Health is not KeyHealth.Healthy;
    }
}
