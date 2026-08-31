using AdamCodexHub.Core.Domain;
using AdamCodexHub.Core.Interfaces;
using AdamCodexHub.Infrastructure.Database;
using AdamCodexHub.Infrastructure.Paths;
using AdamCodexHub.Infrastructure.Providers;
using AdamCodexHub.Providers;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AdamCodexHub.Core.Tests;

public sealed class ProviderPersistenceTests
{
    [Fact]
    public async Task CustomProviderAndActiveSelectionSurviveRestart()
    {
        await using var fixture = new ProviderFixture();
        var manager = fixture.CreateManager();
        await manager.InitializeAsync();

        var custom = new ProviderProfile
        {
            Id = "my-provider",
            Name = "My Provider",
            Adapter = "openai-compatible",
            BaseUrl = "https://api.example.test/v1/",
            TrustLevel = ProviderTrustLevel.Custom,
            ModelsEndpoint = "/models",
            ResponsesEndpoint = "/responses",
            ExtraHeaders = new Dictionary<string, string>
            {
                ["X-Client-Name"] = "Adam CodexHub"
            },
            DeclaredCapabilities = new[] { "text", "streaming", "TEXT" }
        };

        await manager.SaveAsync(custom);
        await manager.SetActiveAsync(custom.Id);

        var restarted = fixture.CreateManager();
        await restarted.InitializeAsync();

        var restored = await restarted.GetAsync(custom.Id);
        Assert.NotNull(restored);
        Assert.Equal("https://api.example.test/v1", restored.BaseUrl);
        Assert.Equal("Adam CodexHub", restored.ExtraHeaders["X-Client-Name"]);
        Assert.Equal(2, restored.DeclaredCapabilities.Count);
        Assert.Equal(custom.Id, (await restarted.GetActiveAsync())?.Id);
    }

    [Fact]
    public async Task DisablingActiveProviderFallsBackToCodexAccountAndPersists()
    {
        await using var fixture = new ProviderFixture();
        var manager = fixture.CreateManager();
        await manager.SaveAsync(CreateCustomProvider());
        await manager.SetActiveAsync("custom-provider");

        await manager.SetEnabledAsync("custom-provider", false);

        Assert.Equal(ProviderManager.CodexAccountProviderId, (await manager.GetActiveAsync())?.Id);
        Assert.Equal(ProviderHealth.Disabled, (await manager.GetAsync("custom-provider"))?.Health);

        var restarted = fixture.CreateManager();
        Assert.Equal(ProviderManager.CodexAccountProviderId, (await restarted.GetActiveAsync())?.Id);
        Assert.False((await restarted.GetAsync("custom-provider"))?.Enabled);
    }

    [Fact]
    public async Task BuiltInProviderCannotBeDeletedButCustomProviderCan()
    {
        await using var fixture = new ProviderFixture();
        var manager = fixture.CreateManager();
        await manager.SaveAsync(CreateCustomProvider());

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.DeleteAsync("preset-provider"));
        Assert.True(await manager.DeleteAsync("custom-provider"));
        Assert.Null(await manager.GetAsync("custom-provider"));
    }

    [Fact]
    public async Task SecretBearingExtraHeaderIsRejected()
    {
        await using var fixture = new ProviderFixture();
        var manager = fixture.CreateManager();
        var provider = CreateCustomProvider() with
        {
            ExtraHeaders = new Dictionary<string, string>
            {
                ["Authorization"] = "Bearer plaintext-secret"
            }
        };

        await Assert.ThrowsAsync<ArgumentException>(() => manager.SaveAsync(provider));
    }

    private static ProviderProfile CreateCustomProvider() => new()
    {
        Id = "custom-provider",
        Name = "Custom Provider",
        Adapter = "openai-compatible",
        BaseUrl = "https://custom.example.test/v1",
        TrustLevel = ProviderTrustLevel.Custom
    };

    private sealed class ProviderFixture : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "AdamCodexHub.Tests",
            Guid.NewGuid().ToString("N"));
        private readonly SqliteDatabase _database;

        public ProviderFixture()
        {
            _database = new SqliteDatabase(AppPaths.ForRoot(_root));
        }

        public ProviderManager CreateManager() =>
            new(new FakeRegistry(), new SqliteProviderStore(_database));

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();

            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeRegistry : IProviderRegistryService
    {
        public Task<IReadOnlyList<ProviderProfile>> GetBuiltInAsync(
            CancellationToken cancellationToken = default)
        {
            IReadOnlyList<ProviderProfile> providers = new[]
            {
                new ProviderProfile
                {
                    Id = "preset-provider",
                    Name = "Preset Provider",
                    Adapter = "openai-compatible",
                    BaseUrl = "https://preset.example.test/v1",
                    TrustLevel = ProviderTrustLevel.Verified
                }
            };

            return Task.FromResult(providers);
        }
    }
}
