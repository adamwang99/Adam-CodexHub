using AdamCodexHub.Core.Domain;

namespace AdamCodexHub.Core.Interfaces;

public interface IProjectStateService
{
    Task<ProjectState> RefreshAsync(
        string projectPath,
        SyncLevel level,
        string? providerId = null,
        string? modelId = null,
        CancellationToken cancellationToken = default);

    Task<ProjectState?> ReadAsync(
        string projectPath,
        CancellationToken cancellationToken = default);
}
