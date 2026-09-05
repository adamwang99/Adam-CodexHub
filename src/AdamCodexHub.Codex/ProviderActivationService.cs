using AdamCodexHub.Core.Domain;
using AdamCodexHub.Core.Interfaces;

namespace AdamCodexHub.Codex;

public sealed class ProviderActivationService : IProviderActivationService
{
    private const string CodexAccountProviderId = "codex-account";
    private readonly IProviderManager _providers;
    private readonly IModelStore _models;
    private readonly ICodexConfigService _config;
    private readonly IGatewayService _gateway;
    private readonly ISessionContinuityService _sessions;

    public ProviderActivationService(
        IProviderManager providers,
        IModelStore models,
        ICodexConfigService config,
        IGatewayService gateway,
        ISessionContinuityService sessions)
    {
        _providers = providers;
        _models = models;
        _config = config;
        _gateway = gateway;
        _sessions = sessions;
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
            if (source?.Id == CodexAccountProviderId)
            {
                return new ProviderActivationResult(
                    target,
                    null,
                    plan,
                    "Codex Account is already active.");
            }

            // Codex Account is the native, immutable configuration: activation only flips the
            // app's internal active state. Nothing is ever written to ~/.codex and the in-process
            // gateway keeps running (the app now lives in the tray between window closures).
            await _providers.SetActiveAsync(target.Id, cancellationToken);
            return new ProviderActivationResult(
                target,
                null,
                plan,
                plan is null
                    ? "Codex Account restored. No project handoff was generated."
                    : "Codex Account restored and project handoff state refreshed.");
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

        return new ProviderActivationResult(
            target,
            model,
            plan,
            plan is null
                ? $"{target.Name} / {model.DisplayName} activated. Set a project path to generate handoff state."
                : $"{target.Name} / {model.DisplayName} activated with {plan.RecommendedSyncLevel} project sync.");
    }
}
