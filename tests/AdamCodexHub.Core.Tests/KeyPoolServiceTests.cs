using AdamCodexHub.Core.Domain;
using AdamCodexHub.Core.Interfaces;
using AdamCodexHub.Infrastructure.Database;
using AdamCodexHub.Infrastructure.Keys;
using AdamCodexHub.Infrastructure.Paths;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AdamCodexHub.Core.Tests;

public sealed class KeyPoolServiceTests
{
    [Fact]
    public async Task FailureAndReorderSelectNextEligibleKey()
    {
        await using var fixture = new KeyPoolFixture();
        var first = await fixture.Service.AddAsync("provider", "First", "secret-first", 10);
        var second = await fixture.Service.AddAsync("provider", "Second", "secret-second", 20);

        Assert.Equal(first.Id, (await fixture.Service.GetActiveAsync("provider"))?.Key.Id);

        await fixture.Service.MarkFailureAsync(first.Id, KeyHealth.QuotaEmpty, "quota exhausted");
        Assert.Equal(second.Id, (await fixture.Service.GetActiveAsync("provider"))?.Key.Id);

        await fixture.Service.MarkSuccessAsync(first.Id);
        await fixture.Service.ReorderAsync("provider", new[] { second.Id, first.Id });
        Assert.Equal(second.Id, (await fixture.Service.GetActiveAsync("provider"))?.Key.Id);

        await fixture.Service.SetEnabledAsync(second.Id, false);
        Assert.Equal(first.Id, (await fixture.Service.GetActiveAsync("provider"))?.Key.Id);
    }

    [Fact]
    public async Task CooldownAndExplicitExclusionAreRespected()
    {
        await using var fixture = new KeyPoolFixture();
        var first = await fixture.Service.AddAsync("provider", "First", "secret-first", 1);
        var second = await fixture.Service.AddAsync("provider", "Second", "secret-second", 2);

        await fixture.Service.MarkFailureAsync(
            first.Id,
            KeyHealth.Cooldown,
            "rate limited",
            TimeSpan.FromMinutes(5));

        Assert.Equal(second.Id, (await fixture.Service.GetActiveAsync("provider"))?.Key.Id);

        await fixture.Service.MarkSuccessAsync(first.Id);
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { first.Id };
        Assert.Equal(second.Id, (await fixture.Service.GetActiveAsync("provider", excluded))?.Key.Id);
    }

    [Fact]
    public async Task DeleteRemovesMetadataAndProtectedSecret()
    {
        await using var fixture = new KeyPoolFixture();
        var key = await fixture.Service.AddAsync("provider", "Delete me", "secret", 1);

        Assert.True(await fixture.Service.DeleteAsync(key.Id));
        Assert.Empty(await fixture.Service.ListAsync("provider"));
        Assert.False(fixture.Vault.Contains(key.SecretReference));
    }

    [Fact]
    public async Task ReorderRequiresEveryKeyExactlyOnce()
    {
        await using var fixture = new KeyPoolFixture();
        var first = await fixture.Service.AddAsync("provider", "First", "secret-first", 1);
        await fixture.Service.AddAsync("provider", "Second", "secret-second", 2);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            fixture.Service.ReorderAsync("provider", new[] { first.Id, first.Id }));
    }

    private sealed class KeyPoolFixture : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "AdamCodexHub.Tests",
            Guid.NewGuid().ToString("N"));

        public KeyPoolFixture()
        {
            var database = new SqliteDatabase(AppPaths.ForRoot(_root));
            Vault = new MemoryKeyVault();
            Service = new SqliteKeyPoolService(database, Vault);
        }

        public MemoryKeyVault Vault { get; }
        public SqliteKeyPoolService Service { get; }

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

    private sealed class MemoryKeyVault : IKeyVault
    {
        private readonly Dictionary<string, string> _secrets = new();

        public Task<string> StoreAsync(
            string providerId,
            string secret,
            CancellationToken cancellationToken = default)
        {
            var reference = $"{providerId}-{Guid.NewGuid():N}";
            _secrets[reference] = secret;
            return Task.FromResult(reference);
        }

        public Task<string?> RetrieveAsync(
            string secretReference,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_secrets.GetValueOrDefault(secretReference));

        public Task DeleteAsync(
            string secretReference,
            CancellationToken cancellationToken = default)
        {
            _secrets.Remove(secretReference);
            return Task.CompletedTask;
        }

        public bool Contains(string secretReference) => _secrets.ContainsKey(secretReference);
    }
}
