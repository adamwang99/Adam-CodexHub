using AdamCodexHub.Codex;
using AdamCodexHub.Core.Domain;
using AdamCodexHub.Core.Interfaces;
using Xunit;

namespace AdamCodexHub.Core.Tests;

public sealed class ProviderActivationServiceTests
{
    [Fact]
    public async Task ActivateAccountSetsActiveWithoutRewritingConfigOrStoppingGateway()
    {
        var remote = CreateProvider("openrouter", enabled: true);
        var account = CreateProvider("codex-account", enabled: true);
        var providers = new FakeProviderManager(remote, active: remote);
        var config = new FakeConfig(hasAccountProfile: false);
        var gateway = new FakeGateway(running: true);

        var service = Create(providers, config: config, gateway: gateway);
        var result = await service.ActivateAsync("codex-account", null);

        Assert.Equal("codex-account", result.Provider.Id);
        Assert.Null(result.Model);
        Assert.Equal(0, config.ActivateAccountCalls);
        Assert.Equal(0, config.PrepareGatewayHomeCalls);
        Assert.Equal(0, gateway.StopCalls);
        Assert.Equal("codex-account", providers.Active?.Id);
    }

    [Fact]
    public async Task ActivateAccountWhenAlreadyActiveIsNoOp()
    {
        var account = CreateProvider("codex-account", enabled: true);
        var providers = new FakeProviderManager(account, active: account);
        var config = new FakeConfig(hasAccountProfile: true);
        var gateway = new FakeGateway(running: false);

        var service = Create(providers, config: config, gateway: gateway);
        var result = await service.ActivateAsync("codex-account", null);

        Assert.Equal("Codex Account is already active.", result.Message);
        Assert.Equal(0, config.ActivateAccountCalls);
        Assert.Equal(0, gateway.StopCalls);
    }

    [Fact]
    public async Task ActivateRemoteStartsGatewayPreparesSandboxHomeAndSetsActive()
    {
        var remote = CreateProvider("deepseek", enabled: true);
        var account = CreateProvider("codex-account", enabled: true);
        var model = CreateModel("deepseek", "deepseek-chat", enabled: true);

        var providers = new FakeProviderManager(remote, active: account);
        var models = new FakeModelStore(model);
        var config = new FakeConfig(hasAccountProfile: true);
        var gateway = new FakeGateway(running: false);

        var service = Create(providers, models: models, config: config, gateway: gateway);
        var result = await service.ActivateAsync("deepseek", "deepseek-chat");

        Assert.Equal("deepseek", result.Provider.Id);
        Assert.Equal("deepseek-chat", result.Model?.RemoteId);
        Assert.Equal(1, gateway.StartCalls);
        Assert.Equal(1, config.PrepareGatewayHomeCalls);
        Assert.Equal("deepseek", config.LastPreparedProviderId);
        Assert.Equal("deepseek-chat", config.LastPreparedModelId);
        Assert.Equal("deepseek", providers.Active?.Id);
        Assert.Equal(0, gateway.StopCalls);
    }

    [Fact]
    public async Task DisabledProviderIsRejected()
    {
        var remote = CreateProvider("deepseek", enabled: false);
        var account = CreateProvider("codex-account", enabled: true);

        var service = Create(new FakeProviderManager(remote, active: account));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ActivateAsync("deepseek", "deepseek-chat"));
    }

    [Fact]
    public async Task UnknownProviderIsRejected()
    {
        var account = CreateProvider("codex-account", enabled: true);
        var service = Create(new FakeProviderManager(account, active: account));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ActivateAsync("nope", "model"));
    }

    [Fact]
    public async Task MissingModelIdForRemoteProviderIsRejected()
    {
        var remote = CreateProvider("deepseek", enabled: true);
        var account = CreateProvider("codex-account", enabled: true);

        var service = Create(new FakeProviderManager(remote, active: account));
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => service.ActivateAsync("deepseek", null));
    }

    [Fact]
    public async Task NonEnabledModelIsRejected()
    {
        var remote = CreateProvider("deepseek", enabled: true);
        var account = CreateProvider("codex-account", enabled: true);
        var model = CreateModel("deepseek", "deepseek-chat", enabled: false);

        var providers = new FakeProviderManager(remote, active: account);
        var models = new FakeModelStore(model);
        var service = Create(providers, models: models);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ActivateAsync("deepseek", "deepseek-chat"));
    }

    [Fact]
    public async Task GatewayConfigWriteFailureRollsBackActiveAndStopsGateway()
    {
        var remote = CreateProvider("deepseek", enabled: true);
        var account = CreateProvider("codex-account", enabled: true);
        var model = CreateModel("deepseek", "deepseek-chat", enabled: true);

        var providers = new FakeProviderManager(remote, active: account);
        var models = new FakeModelStore(model);
        var config = new FakeConfig(hasAccountProfile: true, failGatewayWrite: true);
        var gateway = new FakeGateway(running: false);

        var service = Create(providers, models: models, config: config, gateway: gateway);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ActivateAsync("deepseek", "deepseek-chat"));

        // Rollback: active restored to the previous source, and gateway stopped.
        Assert.Equal("codex-account", providers.Active?.Id);
        Assert.Equal(1, gateway.StopCalls);
    }

    private static ProviderActivationService Create(
        IProviderManager providers,
        IModelStore? models = null,
        ICodexConfigService? config = null,
        IGatewayService? gateway = null,
        ISessionContinuityService? sessions = null) =>
        new(
            providers,
            models ?? new FakeModelStore(),
            config ?? new FakeConfig(hasAccountProfile: true),
            gateway ?? new FakeGateway(running: false),
            sessions ?? new FakeSessions());

    private static ProviderProfile CreateProvider(string id, bool enabled) => new()
    {
        Id = id,
        Name = id == "codex-account" ? "Codex Account" : id,
        Adapter = id == "codex-account" ? "codex-account" : "openai-compatible",
        BaseUrl = id == "codex-account" ? "native://codex-account" : $"https://{id}.example.test/v1",
        Enabled = enabled
    };

    private static ModelDescriptor CreateModel(string providerId, string remoteId, bool enabled) => new()
    {
        ProviderId = providerId,
        RemoteId = remoteId,
        DisplayName = remoteId,
        State = enabled ? ModelLifecycleState.Enabled : ModelLifecycleState.Discovered,
        Enabled = enabled
    };

    private sealed class FakeProviderManager : IProviderManager
    {
        private readonly List<ProviderProfile> _all = new();

        public FakeProviderManager(ProviderProfile provider, ProviderProfile? active)
        {
            _all.Add(provider);
            if (active is not null && _all.All(x => x.Id != active.Id))
            {
                _all.Add(active);
            }

            if (_all.All(x => x.Id != "codex-account"))
            {
                _all.Add(CreateProvider("codex-account", enabled: true));
            }

            Active = active;
        }

        public ProviderProfile? Active { get; private set; }

        public IReadOnlyList<string> StartupWarnings => Array.Empty<string>();

        public Task<ProviderProfile?> GetActiveAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Active);

        public Task<ProviderProfile?> GetAsync(
            string providerId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_all.FirstOrDefault(x => x.Id == providerId));

        public Task SetActiveAsync(
            string providerId,
            CancellationToken cancellationToken = default)
        {
            Active = _all.FirstOrDefault(x => x.Id == providerId)
                ?? new ProviderProfile
                {
                    Id = providerId,
                    Name = providerId,
                    Adapter = "openai-compatible",
                    BaseUrl = $"https://{providerId}.example.test/v1",
                    Enabled = true
                };
            return Task.CompletedTask;
        }

        public Task InitializeAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<ProviderProfile>> GetAllAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProviderProfile>>(_all);

        public Task SaveAsync(ProviderProfile provider, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task SetEnabledAsync(
            string providerId,
            bool enabled,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<bool> DeleteAsync(
            string providerId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task SetHealthAsync(
            string providerId,
            ProviderHealth health,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class FakeModelStore : IModelStore
    {
        private readonly List<ModelDescriptor> _models = new();

        public FakeModelStore(params ModelDescriptor[] models)
        {
            _models.AddRange(models);
        }

        public Task<ModelDescriptor?> GetAsync(
            string providerId,
            string modelId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_models.FirstOrDefault(x =>
                x.ProviderId == providerId && x.RemoteId == modelId));

        public Task<IReadOnlyList<ModelDescriptor>> GetAllAsync(
            string providerId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ModelDescriptor>>(
                _models.Where(x => x.ProviderId == providerId).ToArray());

        public Task InitializeAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task UpsertAsync(ModelDescriptor model, CancellationToken cancellationToken = default)
        {
            _models.RemoveAll(x => x.ProviderId == model.ProviderId && x.RemoteId == model.RemoteId);
            _models.Add(model);
            return Task.CompletedTask;
        }

        public Task MarkUnavailableExceptAsync(
            string providerId,
            IReadOnlySet<string> seenModelIds,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task SetEnabledAsync(
            string providerId,
            string modelId,
            bool enabled,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task SaveCompatibilityAsync(
            CompatibilityResult result,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<CompatibilityResult?> GetLatestCompatibilityAsync(
            string providerId,
            string modelId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<CompatibilityResult?>(null);
    }

    private sealed class FakeConfig : ICodexConfigService
    {
        private readonly bool _hasAccountProfile;
        private readonly bool _failGatewayWrite;
        private readonly bool _overlayActive;

        public FakeConfig(bool hasAccountProfile, bool failGatewayWrite = false, bool overlayActive = false)
        {
            _hasAccountProfile = hasAccountProfile;
            _failGatewayWrite = failGatewayWrite;
            _overlayActive = overlayActive;
        }

        public string CodexHome => "test-codex-home";
        public int ActivateAccountCalls { get; private set; }
        public int ActivateGatewayCalls { get; private set; }
        public string? LastGatewayModelId { get; private set; }
        public int PrepareGatewayHomeCalls { get; private set; }
        public string? LastPreparedProviderId { get; private set; }
        public string? LastPreparedModelId { get; private set; }

        public Task<bool> HasAccountProfileAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_hasAccountProfile);

        public Task ActivateAccountAsync(CancellationToken cancellationToken = default)
        {
            ActivateAccountCalls++;
            return Task.CompletedTask;
        }

        public string GetGatewayHomePath(string providerId) =>
            Path.Combine("test-homes", providerId);

        public Task<string> PrepareGatewayHomeAsync(
            string providerId,
            string modelId,
            int gatewayPort,
            string gatewayToken,
            CancellationToken cancellationToken = default)
        {
            if (_failGatewayWrite)
            {
                throw new InvalidOperationException("Simulated config write failure.");
            }

            PrepareGatewayHomeCalls++;
            LastPreparedProviderId = providerId;
            LastPreparedModelId = modelId;
            return Task.FromResult(GetGatewayHomePath(providerId));
        }

        public Task ActivateGatewayAsync(
            string modelId,
            int gatewayPort,
            string gatewayToken,
            CancellationToken cancellationToken = default)
        {
            if (_failGatewayWrite)
            {
                throw new InvalidOperationException("Simulated config write failure.");
            }

            ActivateGatewayCalls++;
            LastGatewayModelId = modelId;
            return Task.CompletedTask;
        }

        public Task<bool> HasGatewayOverlayAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_overlayActive);

        public Task<bool> RestoreAccountIfGatewayOverlayAsync(CancellationToken cancellationToken = default)
        {
            if (!_overlayActive)
            {
                return Task.FromResult(false);
            }

            ActivateAccountCalls++;
            return Task.FromResult(true);
        }

        public Task<string?> BackupCurrentAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public Task RestoreLastKnownGoodAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class FakeGateway : IGatewayService
    {
        private readonly bool _running;

        public FakeGateway(bool running)
        {
            _running = running;
        }

        public bool IsRunning => _running;
        public int Port => 18771;
        public string LocalToken => "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        public int StartCalls { get; private set; }
        public int StopCalls { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            StartCalls++;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            StopCalls++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeSessions : ISessionContinuityService
    {
        public Task<SessionSwitchPlan> PrepareSwitchAsync(
            string projectPath,
            string sourceProviderId,
            string targetProviderId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new SessionSwitchPlan
            {
                SourceProviderId = sourceProviderId,
                TargetProviderId = targetProviderId,
                ProjectState = new ProjectState
                {
                    ProjectPath = projectPath,
                    Revision = 1,
                    UpdatedAt = DateTimeOffset.UtcNow
                }
            });

        public Task RegisterSessionAsync(
            SessionBinding session,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<SessionBinding?> FindLatestAsync(
            string projectPath,
            string providerId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<SessionBinding?>(null);
    }
}
