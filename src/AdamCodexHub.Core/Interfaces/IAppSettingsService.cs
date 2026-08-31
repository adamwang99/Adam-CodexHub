namespace AdamCodexHub.Core.Interfaces;

public interface IAppSettingsService
{
    Task<bool> HasAcknowledgedSessionMechanismAsync(
        int requiredVersion,
        CancellationToken cancellationToken = default);

    Task AcknowledgeSessionMechanismAsync(
        int version,
        CancellationToken cancellationToken = default);
}
