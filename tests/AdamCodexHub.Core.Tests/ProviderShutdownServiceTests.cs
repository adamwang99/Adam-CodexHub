using AdamCodexHub.Codex;
using AdamCodexHub.Core.Domain;
using AdamCodexHub.Core.Interfaces;
using Xunit;

namespace AdamCodexHub.Core.Tests;

public sealed class ProviderShutdownServiceTests
{
    [Fact]
    public async Task RemoteProviderResetsActiveToCodexAccountOnShutdown()
    {
        var providers = new ShutdownProviderManager(CreateProvider("openrouter"));
        var service = new ProviderShutdownService(providers, new ShutdownConfig());

        var status = await service.RestoreAccountAsync();

        Assert.Equal(ProviderShutdownStatus.AccountRestored, status);
        Assert.Equal("codex-account", providers.Active?.Id);
    }

    [Fact]
    public async Task RestoreAccountDoesNotRequireAnAccountProfileOnDisk()
    {
        // CLI sandbox activations never touch ~/.codex, so shutting down only resets the
        // internal active state when no gateway overlay is present.
        var providers = new ShutdownProviderManager(CreateProvider("openrouter"));
        var service = new ProviderShutdownService(providers, new ShutdownConfig());

        var status = await service.RestoreAccountAsync();

        Assert.Equal(ProviderShutdownStatus.AccountRestored, status);
        Assert.Equal("codex-account", providers.Active?.Id);
    }

    [Fact]
    public async Task ActiveAccountDoesNotRewriteConfiguration()
    {
        var providers = new ShutdownProviderManager(CreateProvider("codex-account"));
        var service = new ProviderShutdownService(providers, new ShutdownConfig());

        var status = await service.RestoreAccountAsync();

        Assert.Equal(ProviderShutdownStatus.AlreadyUsingAccount, status);
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

    /// <summary>Minimal config stub: never reports an active desktop gateway overlay.</summary>
    private sealed class ShutdownConfig : ICodexConfigService
    {
        public string CodexHome => "test-codex-home";

        public Task<bool> HasAccountProfileAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task ActivateAccountAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task ActivateGatewayAsync(
            string modelId,
            int gatewayPort,
            string gatewayToken,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<string?> BackupCurrentAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public Task RestoreLastKnownGoodAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<string> PrepareGatewayHomeAsync(
            string providerId,
            string modelId,
            int gatewayPort,
            string gatewayToken,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Path.Combine("test-homes", providerId));

        public string GetGatewayHomePath(string providerId) =>
            Path.Combine("test-homes", providerId);

        public Task<bool> HasGatewayOverlayAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<bool> RestoreAccountIfGatewayOverlayAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }
}
