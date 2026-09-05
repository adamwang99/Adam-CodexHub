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

    /// <summary>
    /// Prepares a private, sandboxed Codex home (under the app data directory) for a managed
    /// third-party provider. The user's real <c>~/.codex/config.toml</c> is only ever READ as the
    /// base; all gateway keys are written into the sandbox home so the system Codex configuration
    /// stays immutable. Returns the fully qualified path of the prepared runtime home.
    /// </summary>
    Task<string> PrepareGatewayHomeAsync(
        string providerId,
        string modelId,
        int gatewayPort,
        string gatewayToken,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Computes the sandboxed Codex home path that <see cref="PrepareGatewayHomeAsync"/> would use
    /// for a managed provider, without creating it. Callers use this as <c>CODEX_HOME</c> when
    /// launching Codex for that provider.
    /// </summary>
    string GetGatewayHomePath(string providerId);
}
