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
        var service = new ProviderShutdownService(providers);

        var status = await service.RestoreAccountAsync();

        Assert.Equal(ProviderShutdownStatus.AccountRestored, status);
        Assert.Equal("codex-account", providers.Active?.Id);
    }

    [Fact]
    public async Task RestoreAccountDoesNotRequireAnAccountProfileOnDisk()
    {
        // Managed providers live in a sandboxed CODEX_HOME, so shutting down never needs a
        // captured ~/.codex profile: the app only resets its internal active state.
        var providers = new ShutdownProviderManager(CreateProvider("openrouter"));
        var service = new ProviderShutdownService(providers);

        var status = await service.RestoreAccountAsync();

        Assert.Equal(ProviderShutdownStatus.AccountRestored, status);
        Assert.Equal("codex-account", providers.Active?.Id);
    }

    [Fact]
    public async Task ActiveAccountDoesNotRewriteConfiguration()
    {
        var providers = new ShutdownProviderManager(CreateProvider("codex-account"));
        var service = new ProviderShutdownService(providers);

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
}
