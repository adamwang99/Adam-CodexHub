using AdamCodexHub.Core.Interfaces;

namespace AdamCodexHub.Codex;

public enum ProviderShutdownStatus
{
    AlreadyUsingAccount,
    AccountRestored,
    AccountProfileUnavailable
}

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
                StringComparison.OrdinalIgnoreCase))
        {
            return ProviderShutdownStatus.AlreadyUsingAccount;
        }

        if (!await _config.HasAccountProfileAsync(cancellationToken))
        {
            return ProviderShutdownStatus.AccountProfileUnavailable;
        }

        await _config.ActivateAccountAsync(cancellationToken);
        await _providers.SetActiveAsync(CodexAccountProviderId, cancellationToken);
        return ProviderShutdownStatus.AccountRestored;
    }
}
