using AdamCodexHub.Core.Domain;
using AdamCodexHub.Core.Interfaces;
using AdamCodexHub.Core.Services;

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
    private readonly ICodexReadinessStore? _readiness;

    public ProviderActivationService(
        IProviderManager providers,
        IModelStore models,
        ICodexConfigService config,
        IGatewayService gateway,
        ISessionContinuityService sessions,
        CodexThreadModelMigration? threadMigration = null,
        ICodexReadinessStore? readiness = null)
    {
        _providers = providers;
        _models = models;
        _config = config;
        _gateway = gateway;
        _sessions = sessions;
        _threads = threadMigration;   // no instance handed in → nothing is ever rewritten
        _readiness = readiness;
    }

    /// <summary>
    /// The models the Codex picker offers right now: enabled with a current verdict, quickest first,
    /// then whatever <see cref="CodexCatalogPolicy"/> falls back to before a run's first verdict
    /// exists. The policy is the same rule the gateway serves, so this cannot disagree with what Codex
    /// lists.
    /// </summary>
    public async Task<IReadOnlyList<string>> PublishedModelsAsync(
        string providerId,
        CancellationToken cancellationToken = default)
    {
        var enabled = (await _models.GetAllAsync(providerId, cancellationToken).ConfigureAwait(false))
            .Where(m => m.Enabled && m.State == ModelLifecycleState.Enabled)
            .Select(m => m.RemoteId)
            .ToArray();
        if (enabled.Length == 0)
        {
            return Array.Empty<string>();
        }

        var verdicts = _readiness is null
            ? Array.Empty<CodexReadiness>()
            : (await _readiness.GetAllAsync(providerId, cancellationToken).ConfigureAwait(false)).ToArray();
        var now = DateTimeOffset.UtcNow;
        var published = CodexCatalogPolicy.SelectPublished(enabled, verdicts, now);
        var ready = verdicts
            .Where(v => v.Ready && v.IsFresh(now) && published.Contains(v.ModelId, StringComparer.Ordinal))
            .OrderBy(v => v.LatencyMs ?? int.MaxValue)
            .Select(v => v.ModelId)
            .ToArray();

        return ready
            .Concat(published.Where(id => !ready.Contains(id, StringComparer.Ordinal)))
            .ToArray();
    }

    public async Task<string?> PreferredDesktopModelAsync(
        string providerId,
        CancellationToken cancellationToken = default) =>
        (await PublishedModelsAsync(providerId, cancellationToken).ConfigureAwait(false)).FirstOrDefault();

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

        // Aim at a model the catalogue actually offers. An enabled model that no current verdict
        // covers is not in the Codex list at all: Codex reads it as "Custom", and then switches the
        // session by itself ("Model changed from Custom to claude-opus-4-7 (F)") as soon as the
        // offered list changes underneath it.
        var offered = await PublishedModelsAsync(target.Id, cancellationToken);
        var substitution = string.Empty;
        if (offered.Count > 0 && !offered.Contains(model.RemoteId, StringComparer.Ordinal))
        {
            var replacement = await _models.GetAsync(target.Id, offered[0], cancellationToken);
            if (replacement is not null)
            {
                substitution =
                    $" {model.DisplayName} is not offered by the catalogue right now, so " +
                    $"{replacement.DisplayName} was activated instead.";
                model = replacement;
            }
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
        var providerMessage = (plan is null
            ? $"{target.Name} / {model.DisplayName} activated. Set a project path to generate handoff state."
            : $"{target.Name} / {model.DisplayName} activated with {plan.RecommendedSyncLevel} project sync.")
            + substitution;
        return new ProviderActivationResult(
            target,
            model,
            plan,
            providerMessage + ThreadMigrationNote(movedToProvider));
    }

    /// <summary>
    /// Repairs the threads the provider that is active right now cannot serve — run at startup as well
    /// as on every activation. Codex keeps the pinned model per thread, so a hub restart alone used to
    /// leave the open chats answering "The 'claude-opus-4-8[1M]' model is not supported when using Codex
    /// with a ChatGPT account." until the user started a new chat.
    /// </summary>
    public async Task<int> RepairThreadsForActiveProviderAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var active = await _providers.GetActiveAsync(cancellationToken);
            if (active is null || active.Adapter == "codex-account")
            {
                return MigrateThreadsToAccount();
            }

            if (!active.Enabled)
            {
                return 0;
            }

            var offered = (await _models.GetAllAsync(active.Id, cancellationToken))
                .Where(model => model is { Enabled: true, State: ModelLifecycleState.Enabled })
                .Select(model => model.RemoteId)
                .ToArray();
            var current = await _config.GetCurrentModelAsync(cancellationToken);
            var target = !string.IsNullOrWhiteSpace(current) && offered.Contains(current!)
                ? current!
                : offered.FirstOrDefault();
            return target is null || _threads is null
                ? 0
                : _threads.Migrate(offered, target, CodexThreadModelMigration.GatewayProviderId);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Thread repair is a convenience, never a reason to fail a startup.
            return 0;
        }
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
            return _threads?.Migrate(offered, targetModel, CodexThreadModelMigration.GatewayProviderId) ?? 0;
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
        return _threads?.Migrate(offered, target, CodexThreadModelMigration.AccountProviderId) ?? 0;
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
