using AdamCodexHub.Core.Interfaces;

namespace AdamCodexHub.Codex;

public enum ProviderShutdownStatus
{
    AlreadyUsingAccount,
    AccountRestored,
    AccountProfileUnavailable
}

/// <summary>
/// Runs when the app exits (tray Exit / session end). The CLI activation path lives in a
/// sandboxed CODEX_HOME and never touches the real ~/.codex. The Windows/Desktop activation path
/// overlays ~/.codex/config.toml with the gateway (model_provider "adam_codexhub"); shutting down
/// restores the saved Codex Account profile when that overlay is present, so the Desktop app goes
/// back to the native ChatGPT sign-in and never points at a dead gateway port after we exit.
/// </summary>
public sealed class ProviderShutdownService
{
    private const string CodexAccountProviderId = "codex-account";
    private readonly IProviderManager _providers;
    private readonly ICodexConfigService _config;

    public ProviderShutdownService(
        IProviderManager providers,
        ICodexConfigService config)
    {
        _providers = providers;
        _config = config;
    }

    public async Task<ProviderShutdownStatus> RestoreAccountAsync(
        CancellationToken cancellationToken = default)
    {
        var active = await _providers.GetActiveAsync(cancellationToken);
        if (string.Equals(
                active?.Id,
                CodexAccountProviderId,
                StringComparison.OrdinalIgnoreCase) &&
            !await _config.HasGatewayOverlayAsync(cancellationToken))
        {
            return ProviderShutdownStatus.AlreadyUsingAccount;
        }

        await _providers.SetActiveAsync(CodexAccountProviderId, cancellationToken);

        var restored = await _config.RestoreAccountIfGatewayOverlayAsync(cancellationToken);
        return restored
            ? ProviderShutdownStatus.AccountRestored
            : active?.Id == CodexAccountProviderId
                ? ProviderShutdownStatus.AlreadyUsingAccount
                : ProviderShutdownStatus.AccountRestored;
    }
}
