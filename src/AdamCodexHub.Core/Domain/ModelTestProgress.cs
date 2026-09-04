namespace AdamCodexHub.Core.Domain;

public enum ModelTestStepStatus
{
    Running,
    Passed,
    Failed
}

/// <summary>
/// A single probe step reported while a model compatibility test is running,
/// so the UI can show live progress and the outcome of each sub-test.
/// </summary>
public sealed record ModelTestProgress(
    string Stage,
    ModelTestStepStatus Status,
    string? Detail = null);
