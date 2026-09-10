namespace AdamCodexHub.Core.Interfaces;

/// <summary>
/// Last-resort reviver for a provider that an outage switched off.
///
/// A hard rejection (401 Unauthorized, 402 quota empty) is persisted on the key, and the quiet
/// startup probe deliberately never retries it — the assumption being that it needs the user's
/// attention. For a provider that merely ran out of quota that is backwards: the quota returns on
/// the provider's schedule, not the user's, and until someone presses "Test" the hub keeps showing
/// a provider that cannot be activated. This seam lets the hub probe once more, on its own.
/// </summary>
public interface IProviderRecoveryService
{
    /// <summary>
    /// Re-probes a bounded number of the provider's keys and, when one answers again, re-reads that
    /// provider's model catalogue so its models come back with the key.
    /// </summary>
    /// <param name="providerId">Provider to revive.</param>
    /// <param name="force">True when the user just tried to use the provider (ignore the spacing
    /// between probes); false for background attempts.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when the provider has a usable key after this attempt.</returns>
    Task<bool> TryRecoverAsync(
        string providerId,
        bool force = false,
        CancellationToken cancellationToken = default);
}
