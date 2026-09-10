using AdamCodexHub.Core.Domain;
using AdamCodexHub.Core.Interfaces;

namespace AdamCodexHub.Codex;

public sealed class ProviderActivationService : IProviderActivationService
{
    private const string CodexAccountProviderId = "codex-account";

    /// <summary>
    /// Adam's usual picks, in order. Codex pins a model per thread, so when the account comes back
    /// every thread the gateway left behind has to be pointed at something the account offers.
    /// </summary>
    private static readonly string[] PreferredAccountModels =
    {
        "gpt-5.6-sol",
        "gpt-5.6-terra",
        "gpt-6-astra"
    };

    private readonly IProviderManager _providers;
    private readonly IModelStore _models;
    private readonly ICodexConfigService _config;
    private readonly IGatewayService _gateway;
    private readonly ISessionContinuityService _sessions;
    private readonly CodexThreadModelMigration? _threads;

    public ProviderActivationService(
        IProviderManager providers,
        IModelStore models,
        ICodexConfigService config,
        IGatewayService gateway,
        ISessionContinuityService sessions,
        CodexThreadModelMigration? threadMigration = null)
    {
        _providers = providers;
        _models = models;
        _config = config;
        _gateway = gateway;
        _sessions = sessions;
        _threads = threadMigration;   // no instance handed in → nothing is ever rewritten
    }

    public async Task<ProviderActivationResult> ActivateAsync(
        string providerId,
        string? modelId,
        string? projectPath = null,
        CancellationToken cancellationToken = default)
    {
        var target = await _providers.GetAsync(providerId, cancellationToken)
            ?? throw new InvalidOperationException($"Unknown provider '{providerId}'.");
        var source = await _providers.GetActiveAsync(cancellationToken);

        SessionSwitchPlan? plan = null;
        if (source is not null &&
            !string.Equals(source.Id, target.Id, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(projectPath) &&
            Directory.Exists(projectPath))
        {
            plan = await _sessions.PrepareSwitchAsync(
                projectPath,
                source.Id,
                target.Id,
                cancellationToken);
        }

        if (target.Id == CodexAccountProviderId)
        {
            if (source?.Id == CodexAccountProviderId &&
                !await _config.HasGatewayOverlayAsync(cancellationToken))
            {
                return new ProviderActivationResult(
                    target,
                    null,
                    plan,
                    "Codex Account is already active.");
            }

            // Native Codex Account: flip the app's internal active state AND, when the real
            // ~/.codex/config.toml still carries the desktop gateway overlay (e.g. from an
            // earlier Windows-card activation or an abnormal exit), restore the saved account
            // profile so the Desktop app goes back to the ChatGPT sign-in.
            var restored = await _config.RestoreAccountIfGatewayOverlayAsync(cancellationToken);
            var threadsMoved = MigrateThreadsToAccount();
            await _providers.SetActiveAsync(target.Id, cancellationToken);
            var accountMessage = plan is null
                ? restored
                    ? "Codex Account restored (config returned to the native sign-in)."
                    : "Codex Account restored. No project handoff was generated."
                : "Codex Account restored and project handoff state refreshed.";
            return new ProviderActivationResult(
                target,
                null,
                plan,
                accountMessage + ThreadMigrationNote(threadsMoved));
        }

        if (!target.Enabled)
        {
            throw new InvalidOperationException($"Provider '{target.Name}' is disabled.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        var model = await _models.GetAsync(target.Id, modelId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Unknown model '{modelId}' for provider '{target.Name}'.");
        if (model is not { Enabled: true, State: ModelLifecycleState.Enabled })
        {
            throw new InvalidOperationException(
                $"Model '{model.DisplayName}' must be verified and enabled before activation.");
        }

        var gatewayWasRunning = _gateway.IsRunning;
        await _gateway.StartAsync(cancellationToken);

        try
        {
            await _providers.SetActiveAsync(target.Id, cancellationToken);
            // The prepared sandbox home is written under the app data directory; the launcher
            // resolves its path again via GetGatewayHomePath when starting Codex with CODEX_HOME.
            _ = await _config.PrepareGatewayHomeAsync(
                target.Id,
                model.RemoteId,
                _gateway.Port,
                _gateway.LocalToken,
                target.Name,
                cancellationToken);
        }
        catch
        {
            if (source is { Enabled: true })
            {
                await _providers.SetActiveAsync(source.Id, CancellationToken.None);
            }

            if (!gatewayWasRunning)
            {
                await _gateway.StopAsync(CancellationToken.None);
            }

            throw;
        }

        var movedToProvider = await MigrateThreadsToProviderAsync(target.Id, model.RemoteId, cancellationToken);
        var providerMessage = plan is null
            ? $"{target.Name} / {model.DisplayName} activated. Set a project path to generate handoff state."
            : $"{target.Name} / {model.DisplayName} activated with {plan.RecommendedSyncLevel} project sync.";
        return new ProviderActivationResult(
            target,
            model,
            plan,
            providerMessage + ThreadMigrationNote(movedToProvider));
    }

    /// <summary>
    /// Points every thread the previous provider had pinned at a model this provider actually
    /// offers, so switching providers never leaves Codex refusing a thread.
    /// </summary>
    private async Task<int> MigrateThreadsToProviderAsync(
        string providerId,
        string targetModel,
        CancellationToken cancellationToken)
    {
        try
        {
            var offered = (await _models.GetAllAsync(providerId, cancellationToken))
                .Where(model => model is { Enabled: true, State: ModelLifecycleState.Enabled })
                .Select(model => model.RemoteId)
                .ToArray();
            return _threads?.Migrate(offered, targetModel) ?? 0;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Thread migration is a convenience, never a reason to fail an activation.
            return 0;
        }
    }

    /// <summary>
    /// Codex Account: the account catalogue lives in Codex's own model cache, and the model we aim
    /// stranded threads at is picked from it (Adam's usual model first).
    /// </summary>
    private int MigrateThreadsToAccount()
    {
        var offered = CodexAccountCatalog.Read(_config.CodexHome);
        var target = PreferredAccountModels.FirstOrDefault(offered.Contains) ??
                     offered.FirstOrDefault(model => model != CodexThreadModelMigration.InternalModelId);
        return _threads?.Migrate(offered, target) ?? 0;
    }

    private static string ThreadMigrationNote(int migrated) =>
        migrated > 0
            ? $" {migrated} Codex thread(s) pinned to another provider's model were moved over."
            : string.Empty;

    /// <summary>
    /// Desktop (Windows) activation for a keyed provider: runs the regular sandboxed activation
    /// and THEN overlays the real <c>~/.codex/config.toml</c> with a gateway provider block
    /// (model_provider "adam_codexhub" + base_url pointing at the in-process gateway). The Codex
    /// Desktop app reads that file on startup and routes model traffic through our gateway to the
    /// provider's own API — never the ChatGPT account quota. The previous config is preserved as
    /// the account profile and restored when the user switches back to Codex Account or exits.
    /// </summary>
    public async Task<ProviderActivationResult> ActivateDesktopAsync(
        string providerId,
        string? modelId,
        string? projectPath = null,
        CancellationToken cancellationToken = default)
    {
        var result = await ActivateAsync(providerId, modelId, projectPath, cancellationToken);
        if (result.Model is null)
        {
            // Codex Account — nothing to overlay.
            return result;
        }

        await _config.ActivateGatewayAsync(
            result.Model.RemoteId,
            result.Provider.Name,
            _gateway.Port,
            _gateway.LocalToken,
            cancellationToken);
        return result;
    }
}
