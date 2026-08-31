namespace AdamCodexHub.Core.Domain;

public sealed record SessionSwitchPlan
{
    public required string SourceProviderId { get; init; }
    public required string TargetProviderId { get; init; }
    public required ProjectState ProjectState { get; init; }

    public SessionBinding? ExistingTargetSession { get; init; }
    public bool RequiresNewSession => ExistingTargetSession is null;
    public bool RequiresSync { get; init; } = true;
    public SyncLevel RecommendedSyncLevel { get; init; } = SyncLevel.Normal;
    public string ContinuationInstruction { get; init; } = string.Empty;
}
