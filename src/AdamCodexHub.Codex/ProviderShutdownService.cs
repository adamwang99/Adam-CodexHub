using AdamCodexHub.Core.Interfaces;

namespace AdamCodexHub.Codex;

public enum ProviderShutdownStatus
{
    AlreadyUsingAccount,
    AccountRestored,
    AccountProfileUnavailable
}

/// <summary>
/// Runs when the app exits. Because managed providers now live in a sandboxed CODEX_HOME that is
/// separate from the user's real ~/.codex, shutting down never rewrites any file on disk: it only
/// resets the app's internal active-provider state back to the native Codex Account.
/// </summary>
public sealed class ProviderShutdownService
{
    private const string CodexAccountProviderId = "codex-account";
    private readonly IProviderManager _providers;

    public ProviderShutdownService(IProviderManager providers)
    {
        _providers = providers;
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

        await _providers.SetActiveAsync(CodexAccountProviderId, cancellationToken);
        return ProviderShutdownStatus.AccountRestored;
    }
}
