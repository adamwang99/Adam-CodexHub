namespace AdamCodexHub.Providers.Registry;

public sealed class ProviderDefinition
{
    public int SchemaVersion { get; set; } = 1;
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Adapter { get; set; } = "openai-compatible";
    public string BaseUrl { get; set; } = string.Empty;
    public string TrustLevel { get; set; } = "verified";
    public AuthDefinition Auth { get; set; } = new();
    public EndpointDefinition Endpoints { get; set; } = new();
    public Dictionary<string, string> ExtraHeaders { get; set; } = new();
    public List<string> Capabilities { get; set; } = new();
    public string? Notes { get; set; }

    public sealed class AuthDefinition
    {
        public string Type { get; set; } = "bearer";
        public string? HeaderName { get; set; }
    }

    public sealed class EndpointDefinition
    {
        public string? Models { get; set; }
        public string? Responses { get; set; }
        public string? ChatCompletions { get; set; }
    }
}
