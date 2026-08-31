namespace AdamCodexHub.Core.Domain;

public sealed record CompatibilityResult
{
    public required string ProviderId { get; init; }
    public required string ModelId { get; init; }
    public required DateTimeOffset VerifiedAt { get; init; }

    public bool Text { get; init; }
    public bool Responses { get; init; }
    public bool ChatCompletions { get; init; }
    public bool Streaming { get; init; }
    public bool ToolCalling { get; init; }
    public bool StructuredJson { get; init; }
    public bool Vision { get; init; }

    public int Score { get; init; }
    public string? Notes { get; init; }
}
