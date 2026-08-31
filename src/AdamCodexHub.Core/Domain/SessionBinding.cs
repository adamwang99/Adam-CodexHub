namespace AdamCodexHub.Core.Domain;

public sealed record SessionBinding
{
    public required string Id { get; init; }
    public required string ProjectPath { get; init; }
    public required string ProviderId { get; init; }

    public string? ExternalSessionId { get; init; }
    public string? ModelId { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset LastUsedAt { get; init; }
    public long LastSeenProjectRevision { get; init; }
    public SessionBindingStatus Status { get; init; } = SessionBindingStatus.Active;
}
