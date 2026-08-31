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
}
