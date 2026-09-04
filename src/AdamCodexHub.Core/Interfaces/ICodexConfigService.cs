namespace AdamCodexHub.Core.Interfaces;

public interface ICodexConfigService
{
    string CodexHome { get; }

    Task<bool> HasAccountProfileAsync(CancellationToken cancellationToken = default);
    Task ActivateAccountAsync(CancellationToken cancellationToken = default);

    Task ActivateGatewayAsync(
        string modelId,
        int gatewayPort,
        string gatewayToken,
        CancellationToken cancellationToken = default);

    Task<string?> BackupCurrentAsync(CancellationToken cancellationToken = default);
    Task RestoreLastKnownGoodAsync(CancellationToken cancellationToken = default);
}
