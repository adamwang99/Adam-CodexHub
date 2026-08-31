namespace AdamCodexHub.Core.Domain;

public sealed record ProviderProbeResult(
    bool Success,
    string Summary,
    IReadOnlyList<string> SupportedEndpoints,
    IReadOnlyList<string> DetectedCapabilities);
