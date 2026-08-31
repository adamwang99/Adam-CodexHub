namespace AdamCodexHub.Core.Domain;

public sealed record ModelDescriptor
{
    public required string ProviderId { get; init; }
    public required string RemoteId { get; init; }

    public string DisplayName { get; init; } = string.Empty;
    public ModelLifecycleState State { get; init; } = ModelLifecycleState.Discovered;
    public bool Enabled { get; init; }

    public IReadOnlyList<string> InputModalities { get; init; } = new[] { "text" };
    public IReadOnlyList<string> Capabilities { get; init; } = Array.Empty<string>();

    public int? ContextWindow { get; init; }
    public DateTimeOffset? LastSeenAt { get; init; }
    public DateTimeOffset? LastVerifiedAt { get; init; }
    public int? CompatibilityScore { get; init; }
}
