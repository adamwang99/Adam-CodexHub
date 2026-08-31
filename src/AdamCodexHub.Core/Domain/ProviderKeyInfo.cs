namespace AdamCodexHub.Core.Domain;

public sealed record ProviderKeyInfo
{
    public required string Id { get; init; }
    public required string ProviderId { get; init; }
    public required string Label { get; init; }
    public required string SecretReference { get; init; }

    public int Priority { get; init; } = 100;
    public bool Enabled { get; init; } = true;
    public KeyHealth Health { get; init; } = KeyHealth.Unknown;

    public DateTimeOffset? CooldownUntil { get; init; }
    public DateTimeOffset? LastTestAt { get; init; }
    public DateTimeOffset? LastSuccessAt { get; init; }
    public DateTimeOffset? LastFailureAt { get; init; }
    public string? FailureReason { get; init; }

    public string MaskedDisplay { get; init; } = "••••";
}
