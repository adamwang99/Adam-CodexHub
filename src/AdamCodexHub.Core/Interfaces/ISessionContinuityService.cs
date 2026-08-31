using AdamCodexHub.Core.Domain;

namespace AdamCodexHub.Core.Interfaces;

public interface ISessionContinuityService
{
    Task<SessionSwitchPlan> PrepareSwitchAsync(
        string projectPath,
        string sourceProviderId,
        string targetProviderId,
        CancellationToken cancellationToken = default);

    Task RegisterSessionAsync(
        SessionBinding session,
        CancellationToken cancellationToken = default);

    Task<SessionBinding?> FindLatestAsync(
        string projectPath,
        string providerId,
        CancellationToken cancellationToken = default);
}
