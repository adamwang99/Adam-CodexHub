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

    [Fact]
    public async Task RemoteHttpProviderIsRejected()
    {
        await using var fixture = new ProviderFixture();
        var manager = fixture.CreateManager();
        var provider = CreateCustomProvider() with
        {
            BaseUrl = "http://api.example.test/v1"
        };

        var error = await Assert.ThrowsAsync<ArgumentException>(() => manager.SaveAsync(provider));
        Assert.Contains("must use HTTPS", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://127.0.0.1:11434/v1")]
    [InlineData("http://localhost:1234/v1")]
    [InlineData("http://[::1]:8080/v1")]
    public async Task LoopbackHttpProviderIsAllowed(string baseUrl)
    {
        await using var fixture = new ProviderFixture();
        var manager = fixture.CreateManager();
        var provider = CreateCustomProvider() with { BaseUrl = baseUrl };

        await manager.SaveAsync(provider);

        Assert.Equal(baseUrl, (await manager.GetAsync(provider.Id))?.BaseUrl);
    }

    [Fact]
    public async Task CredentialsEmbeddedInProviderUrlAreRejected()
    {
        await using var fixture = new ProviderFixture();
        var manager = fixture.CreateManager();
        var provider = CreateCustomProvider() with
        {
            BaseUrl = "https://user:secret@api.example.test/v1"
        };

        var error = await Assert.ThrowsAsync<ArgumentException>(() => manager.SaveAsync(provider));
        Assert.Contains("must not be embedded", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LegacyRemoteHttpProviderIsSkippedWithWarningOnStartup()
    {
        await using var fixture = new ProviderFixture();

        // Seed a provider that was valid under older versions (remote HTTP) directly
        // into the store, bypassing SaveAsync validation, then start the manager.
        var store = fixture.CreateStore();
        await store.UpsertAsync(new ProviderProfile
        {
            Id = "legacy-http-provider",
            Name = "Legacy HTTP Provider",
            Adapter = "openai-compatible",
            BaseUrl = "http://api.example.test/v1",
            TrustLevel = ProviderTrustLevel.Custom
        });

        var manager = fixture.CreateManager();
        await manager.InitializeAsync();

        Assert.Null(await manager.GetAsync("legacy-http-provider"));
        var all = await manager.GetAllAsync();
        Assert.DoesNotContain(all, p => p.Id == "legacy-http-provider");
        Assert.Contains(all, p => p.Id == "preset-provider");

        var warning = Assert.Single(manager.StartupWarnings);
        Assert.Contains("legacy-http-provider", warning, StringComparison.Ordinal);
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

        public SqliteProviderStore CreateStore() => new(_database);

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
