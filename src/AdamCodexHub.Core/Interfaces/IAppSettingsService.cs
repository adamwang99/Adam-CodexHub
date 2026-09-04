namespace AdamCodexHub.Core.Interfaces;

public interface IAppSettingsService
{
    Task<bool> HasAcknowledgedSessionMechanismAsync(
        int requiredVersion,
        CancellationToken cancellationToken = default);

    Task AcknowledgeSessionMechanismAsync(
        int version,
        CancellationToken cancellationToken = default);

    Task<bool> HasAcknowledgedProviderDisclosureAsync(
        string providerId,
        int requiredVersion,
        CancellationToken cancellationToken = default);

    Task AcknowledgeProviderDisclosureAsync(
        string providerId,
        int version,
        CancellationToken cancellationToken = default);
}
