using AdamCodexHub.Codex;
using AdamCodexHub.Core.Domain;
using AdamCodexHub.Core.Interfaces;
using Xunit;

namespace AdamCodexHub.Core.Tests;

public sealed class ProviderShutdownServiceTests
{
    [Fact]
    public async Task RemoteProviderRestoresAccountProfileOnShutdown()
    {
        var providers = new ShutdownProviderManager(CreateProvider("openrouter"));
        var config = new ShutdownConfigService(hasAccountProfile: true);
        var service = new ProviderShutdownService(providers, config);

        var status = await service.RestoreAccountAsync();

        Assert.Equal(ProviderShutdownStatus.AccountRestored, status);
        Assert.Equal(1, config.ActivateAccountCalls);
        Assert.Equal("codex-account", providers.Active?.Id);
    }

    [Fact]
    public async Task MissingAccountProfileLeavesProviderSelectionUnchanged()
    {
        var providers = new ShutdownProviderManager(CreateProvider("openrouter"));
        var config = new ShutdownConfigService(hasAccountProfile: false);
        var service = new ProviderShutdownService(providers, config);

        var status = await service.RestoreAccountAsync();

        Assert.Equal(ProviderShutdownStatus.AccountProfileUnavailable, status);
        Assert.Equal(0, config.ActivateAccountCalls);
        Assert.Equal("openrouter", providers.Active?.Id);
    }

    [Fact]
    public async Task ActiveAccountDoesNotRewriteConfiguration()
    {
        var providers = new ShutdownProviderManager(CreateProvider("codex-account"));
        var config = new ShutdownConfigService(hasAccountProfile: true);
        var service = new ProviderShutdownService(providers, config);

        var status = await service.RestoreAccountAsync();

        Assert.Equal(ProviderShutdownStatus.AlreadyUsingAccount, status);
        Assert.Equal(0, config.ActivateAccountCalls);
        Assert.Equal("codex-account", providers.Active?.Id);
    }

    private static ProviderProfile CreateProvider(string id) => new()
    {
        Id = id,
        Name = id,
        Adapter = id == "codex-account" ? "codex-account" : "openai-compatible",
        BaseUrl = id == "codex-account"
            ? "native://codex-account"
            : "https://provider.example.test/v1"
    };

    private sealed class ShutdownProviderManager : IProviderManager
    {
        public ShutdownProviderManager(ProviderProfile active)
        {
            Active = active;
        }

        public ProviderProfile? Active { get; private set; }

        public IReadOnlyList<string> StartupWarnings => Array.Empty<string>();

        public Task<ProviderProfile?> GetActiveAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Active);

        public Task SetActiveAsync(
            string providerId,
            CancellationToken cancellationToken = default)
        {
            Active = CreateProvider(providerId);
            return Task.CompletedTask;
        }

        public Task InitializeAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ProviderProfile>> GetAllAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ProviderProfile?> GetAsync(
            string providerId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task SaveAsync(
            ProviderProfile provider,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task SetEnabledAsync(
            string providerId,
            bool enabled,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> DeleteAsync(
            string providerId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task SetHealthAsync(
            string providerId,
            ProviderHealth health,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class ShutdownConfigService : ICodexConfigService
    {
        private readonly bool _hasAccountProfile;

        public ShutdownConfigService(bool hasAccountProfile)
        {
            _hasAccountProfile = hasAccountProfile;
        }

        public string CodexHome => "test-codex-home";
        public int ActivateAccountCalls { get; private set; }

        public Task<bool> HasAccountProfileAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_hasAccountProfile);

        public Task ActivateAccountAsync(CancellationToken cancellationToken = default)
        {
            ActivateAccountCalls++;
            return Task.CompletedTask;
        }

        public Task ActivateGatewayAsync(
            string modelId,
            int gatewayPort,
            string gatewayToken,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string?> BackupCurrentAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task RestoreLastKnownGoodAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
