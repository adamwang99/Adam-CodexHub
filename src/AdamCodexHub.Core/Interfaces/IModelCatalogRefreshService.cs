namespace AdamCodexHub.Core.Interfaces;

/// <summary>
/// Re-reads a provider's own model list (<c>GET /models</c>) so the catalogue the hub stores never
/// goes stale behind the provider. A provider that ships a new model, retires an old one, or comes
/// back after a quota outage only becomes visible here when somebody scans — this is that scan,
/// without the trip to Setup.
/// </summary>
public interface IModelCatalogRefreshService
{
    /// <summary>
    /// Raised after a provider's catalogue was re-read and stored, carrying the provider id, so a
    /// screen built from the stored state (the Home cards) can rebuild itself instead of keeping a
    /// provider on show as unusable after it has already come back. Raised from a background thread:
    /// handlers marshal to whatever thread they need.
    /// </summary>
    event EventHandler<string>? ProviderRefreshed;

    /// <summary>
    /// Re-reads <paramref name="providerId"/>'s catalogue now, whatever the stored freshness says.
    /// Returns false when the provider could not be reached — a provider failure is logged and
    /// reported, never thrown at the caller.
    /// </summary>
    Task<bool> RefreshProviderAsync(
        string providerId,
        CancellationToken cancellationToken = default);
}
