namespace AdamCodexHub.Core.Domain;

public sealed record ProviderProfile
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Adapter { get; init; }
    public required string BaseUrl { get; init; }

    public ProviderTrustLevel TrustLevel { get; init; } = ProviderTrustLevel.Custom;
    public bool Enabled { get; init; } = true;
    public ProviderHealth Health { get; init; } = ProviderHealth.Unknown;

    public string AuthType { get; init; } = "bearer";
    public string? AuthHeaderName { get; init; }
    public string? ModelsEndpoint { get; init; }
    public string? ResponsesEndpoint { get; init; }
    public string? ChatCompletionsEndpoint { get; init; }

    public IReadOnlyDictionary<string, string> ExtraHeaders { get; init; }
        = new Dictionary<string, string>();

    public IReadOnlyList<string> DeclaredCapabilities { get; init; }
        = Array.Empty<string>();

    /// <summary>Human-readable explanation of the current health, shown as a tooltip.</summary>
    public string HealthTooltip => Health switch
    {
        ProviderHealth.Unknown => "Not health-checked yet. Test an API key or run a model probe to refresh this provider's status.",
        ProviderHealth.Healthy => "Last test succeeded. Provider is reachable and the key is valid.",
        ProviderHealth.Warning => "The provider responded but something looked off (e.g. a partial result).",
        ProviderHealth.RateLimited => "The provider returned HTTP 429 (rate limit). The key may recover later.",
        ProviderHealth.QuotaEmpty => "The provider reported the quota is exhausted (HTTP 402 / 'quota').",
        ProviderHealth.Unauthorized => "Authentication failed (HTTP 401). The stored key may be invalid or expired.",
        ProviderHealth.Offline => "The provider could not be reached (timeout or network error).",
        ProviderHealth.Disabled => "This provider is disabled and will not be used.",
        _ => "Unknown health state."
    };
}
