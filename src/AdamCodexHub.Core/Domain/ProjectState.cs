namespace AdamCodexHub.Core.Domain;

public sealed record ProjectState
{
    public required string ProjectPath { get; init; }
    public long Revision { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }

    public string? GitHead { get; init; }
    public IReadOnlyList<string> ChangedFiles { get; init; } = Array.Empty<string>();

    public string CurrentObjective { get; init; } = string.Empty;
    public IReadOnlyList<string> CompletedWork { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> PendingTasks { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ImportantDecisions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> KnownIssues { get; init; } = Array.Empty<string>();

    public string? LastProviderId { get; init; }
    public string? LastModelId { get; init; }
}
