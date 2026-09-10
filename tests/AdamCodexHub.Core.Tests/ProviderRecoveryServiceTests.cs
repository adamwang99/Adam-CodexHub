using AdamCodexHub.Core.Domain;
using AdamCodexHub.Core.Interfaces;
using AdamCodexHub.Core.Services;
using Xunit;

namespace AdamCodexHub.Core.Tests;

/// <summary>
/// A provider that ran out of quota parks its key (401 Unauthorized / 402 quota empty) and the
/// quiet startup probe deliberately leaves such a key alone. These tests pin the escape hatch:
/// the hub probes once more when the provider is actually needed, and picks the catalogue up with
/// the key — so the user never has to walk to Setup → "Test keys" to find out the quota is back.
/// </summary>
public sealed class ProviderRecoveryServiceTests
{
    private static readonly string[] ExpectedOne = { "k1" };
    private static readonly string[] ExpectedProviderScan = { "hhtech" };

    [Fact]
    public async Task AQuotaParkedKeyIsProbedAndItsCatalogueComesBack()
    {
        var keys = new FakeKeyPool(Key("k1", KeyHealth.Unauthorized, DateTimeOffset.UtcNow.AddHours(-3)));
        var tester = new FakeKeyTester(keys, KeyHealth.Healthy);
        var discovery = new FakeDiscovery();

        var recovered = await Service(keys, tester, discovery).TryRecoverAsync("hhtech");

        Assert.True(recovered);
        Assert.Equal(ExpectedOne, tester.Probed);
        // The key answers again, so the models that went with it are read back in the same attempt.
        Assert.Equal(ExpectedProviderScan, discovery.Scanned);
    }

    [Fact]
    public async Task AKeyProbedMomentsAgoIsLeftAloneOnABackgroundAttempt()
    {
        var keys = new FakeKeyPool(Key("k1", KeyHealth.Unauthorized, DateTimeOffset.UtcNow.AddMinutes(-2)));
        var tester = new FakeKeyTester(keys, KeyHealth.Healthy);
        var discovery = new FakeDiscovery();

        var recovered = await Service(keys, tester, discovery).TryRecoverAsync("hhtech");

        Assert.False(recovered);
        Assert.Empty(tester.Probed);
        Assert.Empty(discovery.Scanned);
    }

    [Fact]
    public async Task AnUrgentAttemptIgnoresTheSpacingBecauseTheUserJustAskedForIt()
    {
        var keys = new FakeKeyPool(Key("k1", KeyHealth.Unauthorized, DateTimeOffset.UtcNow.AddMinutes(-2)));
        var tester = new FakeKeyTester(keys, KeyHealth.Healthy);
        var discovery = new FakeDiscovery();

        var recovered = await Service(keys, tester, discovery).TryRecoverAsync("hhtech", force: true);

        Assert.True(recovered);
        Assert.Equal(ExpectedOne, tester.Probed);
        Assert.Equal(ExpectedProviderScan, discovery.Scanned);
    }

    [Fact]
    public async Task AKeyStillParkedIsNotRetriedIntoTheGround()
    {
        var keys = new FakeKeyPool(
            Key("k1", KeyHealth.Unauthorized, DateTimeOffset.UtcNow.AddHours(-4)),
            Key("k2", KeyHealth.QuotaEmpty, DateTimeOffset.UtcNow.AddHours(-4)),
            Key("k3", KeyHealth.Unauthorized, DateTimeOffset.UtcNow.AddHours(-4)));
        var tester = new FakeKeyTester(keys, KeyHealth.Unauthorized);
        var discovery = new FakeDiscovery();

        var recovered = await Service(keys, tester, discovery).TryRecoverAsync("hhtech");

        Assert.False(recovered);
        Assert.Equal(ProviderRecoveryService.MaxKeyProbes, tester.Probed.Count);
        Assert.DoesNotContain("k3", tester.Probed);
        Assert.Empty(discovery.Scanned);
    }

    [Fact]
    public async Task ACoolingDownKeyWaitsForTheCooldownToClear()
    {
        var keys = new FakeKeyPool(Key(
            "k1",
            KeyHealth.RateLimited,
            DateTimeOffset.UtcNow.AddHours(-3),
            cooldownUntil: DateTimeOffset.UtcNow.AddMinutes(20)));
        var tester = new FakeKeyTester(keys, KeyHealth.Healthy);

        var service = Service(keys, tester, new FakeDiscovery());

        Assert.False(await service.TryRecoverAsync("hhtech"));
        Assert.Empty(tester.Probed);
    }

    [Fact]
    public async Task AWorkingKeyMeansNothingIsProbed()
    {
        var keys = new FakeKeyPool(
            Key("k1", KeyHealth.Healthy, DateTimeOffset.UtcNow.AddHours(-1)),
            Key("k2", KeyHealth.Unauthorized, DateTimeOffset.UtcNow.AddHours(-5)));
        var tester = new FakeKeyTester(keys, KeyHealth.Healthy);
        var discovery = new FakeDiscovery();

        Assert.True(await Service(keys, tester, discovery).TryRecoverAsync("hhtech"));
        Assert.Empty(tester.Probed);
        Assert.Empty(discovery.Scanned);
    }

    [Fact]
    public async Task ADisabledProviderIsNeverProbed()
    {
        var keys = new FakeKeyPool(Key("k1", KeyHealth.Unauthorized, DateTimeOffset.UtcNow.AddHours(-3)));
        var tester = new FakeKeyTester(keys, KeyHealth.Healthy);

        var recovered = await new ProviderRecoveryService(
                new FakeProviderManager(Provider("hhtech", enabled: false)),
                keys,
                tester,
                new FakeDiscovery())
            .TryRecoverAsync("hhtech");

        Assert.False(recovered);
        Assert.Empty(tester.Probed);
    }

    [Fact]
    public async Task ASweepRecoversAProviderWhoseKeyCameBackInsteadOfWaitingHours()
    {
        var models = new FakeModelStore();
        models.Models.Add(Model("hhtech", "claude-5.5"));

        var keys = new FakeKeyPool(Key("k1", KeyHealth.Unauthorized, DateTimeOffset.UtcNow.AddHours(-3)));
        var tester = new FakeKeyTester(keys, KeyHealth.Healthy);
        var discovery = new FakeDiscovery { FailuresBeforeSuccess = 1 };
        var provider = Provider("hhtech");

        var sweep = new ModelCatalogRefreshService(
            new FakeProviderManager(provider),
            models,
            discovery,
            new ProviderRecoveryService(new FakeProviderManager(provider), keys, tester, discovery));

        var refreshed = await sweep.RunTickAsync();

        // The scan failed, the key was re-probed, the catalogue came back: one refresh, not zero.
        Assert.Equal(1, refreshed);
        Assert.Equal(ExpectedOne, tester.Probed);
        Assert.Equal(new[] { "hhtech", "hhtech" }, discovery.Scanned);
    }

    [Fact]
    public async Task ASweepLeavesAProviderThatJustFailedUntilItsRetryWindowPasses()
    {
        var models = new FakeModelStore();
        models.Models.Add(Model("hhtech", "claude-5.5"));

        var keys = new FakeKeyPool(Key("k1", KeyHealth.Unauthorized, DateTimeOffset.UtcNow.AddHours(-3)));
        var tester = new FakeKeyTester(keys, KeyHealth.Unauthorized);
        var discovery = new FakeDiscovery { FailuresBeforeSuccess = int.MaxValue };
        var provider = Provider("hhtech");

        var sweep = new ModelCatalogRefreshService(
            new FakeProviderManager(provider),
            models,
            discovery,
            new ProviderRecoveryService(new FakeProviderManager(provider), keys, tester, discovery));

        Assert.Equal(0, await sweep.RunTickAsync());
        Assert.Single(discovery.Scanned);

        // Same tick right after: the provider is still inside its retry window, so nothing is asked.
        discovery.Scanned.Clear();
        Assert.Equal(0, await sweep.RunTickAsync());
        Assert.Empty(discovery.Scanned);
    }

    [Fact]
    public async Task ARefreshedCatalogueIsAnnouncedSoTheCardsCanRebuildThemselves()
    {
        var models = new FakeModelStore();
        models.Models.Add(Model("hhtech", "claude-5.5"));

        var sweep = new ModelCatalogRefreshService(
            new FakeProviderManager(Provider("hhtech")),
            models,
            new FakeDiscovery());

        var announced = new List<string>();
        sweep.ProviderRefreshed += (_, providerId) => announced.Add(providerId);

        Assert.Equal(1, await sweep.RunTickAsync());
        Assert.Equal(new[] { "hhtech" }, announced);
    }

    [Fact]
    public async Task AListenerThatThrowsDoesNotBreakTheSweep()
    {
        var models = new FakeModelStore();
        models.Models.Add(Model("hhtech", "claude-5.5"));

        var sweep = new ModelCatalogRefreshService(
            new FakeProviderManager(Provider("hhtech")),
            models,
            new FakeDiscovery());

        sweep.ProviderRefreshed += (_, _) => throw new InvalidOperationException("listener is broken");

        Assert.Equal(1, await sweep.RunTickAsync());
    }

    // ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData(1, 15)]
    [InlineData(2, 30)]
    [InlineData(3, 60)]
    [InlineData(4, 120)]
    [InlineData(5, 240)]
    [InlineData(6, 360)]
    [InlineData(9, 360)]
    public void AProviderThatKeepsFailingIsAskedLessAndLessOften(int failures, int expectedMinutes)
    {
        var delay = ModelCatalogRefreshService.BackoffFor(
            failures,
            TimeSpan.FromMinutes(15),
            TimeSpan.FromHours(6));

        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), delay);
    }

    private static ProviderRecoveryService Service(
        FakeKeyPool keys,
        FakeKeyTester tester,
        FakeDiscovery discovery) =>
        new(new FakeProviderManager(Provider("hhtech")), keys, tester, discovery);

    private static ProviderProfile Provider(string id, bool enabled = true) => new()
    {
        Id = id,
        Name = id,
        Adapter = "openai-compatible",
        BaseUrl = $"https://{id}.example.test/v1",
        Enabled = enabled
    };

    private static ProviderKeyInfo Key(
        string id,
        KeyHealth health,
        DateTimeOffset? lastTestAt,
        DateTimeOffset? cooldownUntil = null) => new()
    {
        Id = id,
        ProviderId = "hhtech",
        Label = "default",
        SecretReference = $"hhtech-{id}",
        Health = health,
        LastTestAt = lastTestAt,
        CooldownUntil = cooldownUntil
    };

    private static ModelDescriptor Model(string providerId, string remoteId) => new()
    {
        ProviderId = providerId,
        RemoteId = remoteId,
        DisplayName = remoteId,
        Enabled = true,
        State = ModelLifecycleState.Enabled
    };

    private sealed class FakeKeyPool : IKeyPoolService
    {
        public FakeKeyPool(params ProviderKeyInfo[] keys) => Keys.AddRange(keys);

        public List<ProviderKeyInfo> Keys { get; } = new();

        public void SetHealth(string keyId, KeyHealth health)
        {
            var index = Keys.FindIndex(key => key.Id == keyId);
            if (index >= 0)
            {
                Keys[index] = Keys[index] with { Health = health, LastTestAt = DateTimeOffset.UtcNow };
            }
        }

        public Task<IReadOnlyList<ProviderKeyInfo>> ListAsync(
            string providerId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProviderKeyInfo>>(
                Keys.Where(key => key.ProviderId == providerId).ToArray());

        public Task<ProviderKeyInfo> AddAsync(
            string providerId,
            string label,
            string secret,
            int priority = 100,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ProviderKeySelection?> GetActiveAsync(
            string providerId,
            IReadOnlySet<string>? excludedKeyIds = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ProviderKeySelection?>(null);

        public Task<string?> GetActiveSecretAsync(
            string providerId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>("test-key");

        public Task SetEnabledAsync(
            string keyId,
            bool enabled,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task ReorderAsync(
            string providerId,
            IReadOnlyList<string> orderedKeyIds,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task MarkSuccessAsync(string keyId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task MarkFailureAsync(
            string keyId,
            KeyHealth health,
            string? reason,
            TimeSpan? cooldown = null,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<bool> DeleteAsync(string keyId, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }

    /// <summary>Stands in for the real tester: records the probe and moves the key to an outcome.</summary>
    private sealed class FakeKeyTester : IKeyTestService
    {
        private readonly FakeKeyPool _keys;
        private readonly KeyHealth _outcome;

        public FakeKeyTester(FakeKeyPool keys, KeyHealth outcome) => (_keys, _outcome) = (keys, outcome);

        public List<string> Probed { get; } = new();

        public Task<ProviderProbeResult> TestAsync(
            string providerId,
            string keyId,
            CancellationToken cancellationToken = default)
        {
            Probed.Add(keyId);
            _keys.SetHealth(keyId, _outcome);
            return Task.FromResult(new ProviderProbeResult(
                _outcome == KeyHealth.Healthy,
                _outcome.ToString(),
                Array.Empty<string>(),
                Array.Empty<string>()));
        }
    }

    private sealed class FakeDiscovery : IModelDiscoveryService
    {
        public List<string> Scanned { get; } = new();

        /// <summary>How many scans fail before the provider starts answering again.</summary>
        public int FailuresBeforeSuccess { get; init; }

        public Task<IReadOnlyList<ModelDescriptor>> ScanAsync(
            string providerId,
            CancellationToken cancellationToken = default)
        {
            Scanned.Add(providerId);
            if (Scanned.Count <= FailuresBeforeSuccess)
            {
                throw new InvalidOperationException($"{providerId} is out of quota.");
            }

            return Task.FromResult<IReadOnlyList<ModelDescriptor>>(Array.Empty<ModelDescriptor>());
        }
    }

    private sealed class FakeModelStore : IModelStore
    {
        public List<ModelDescriptor> Models { get; } = new();

        public Task InitializeAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<ModelDescriptor>> GetAllAsync(
            string providerId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ModelDescriptor>>(
                Models.Where(model => model.ProviderId == providerId).ToArray());

        public Task<ModelDescriptor?> GetAsync(
            string providerId,
            string modelId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Models.FirstOrDefault(model =>
                model.ProviderId == providerId && model.RemoteId == modelId));

        public Task UpsertAsync(ModelDescriptor model, CancellationToken cancellationToken = default)
        {
            Models.RemoveAll(m => m.ProviderId == model.ProviderId && m.RemoteId == model.RemoteId);
            Models.Add(model);
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

    private sealed class FakeProviderManager : IProviderManager
    {
        private readonly List<ProviderProfile> _all = new();

        public FakeProviderManager(params ProviderProfile[] providers) => _all.AddRange(providers);

        public IReadOnlyList<string> StartupWarnings => Array.Empty<string>();

        public Task<ProviderProfile?> GetActiveAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_all.FirstOrDefault());

        public Task<ProviderProfile?> GetAsync(
            string providerId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_all.FirstOrDefault(provider => provider.Id == providerId));

        public Task<IReadOnlyList<ProviderProfile>> GetAllAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProviderProfile>>(_all);

        public Task InitializeAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task SaveAsync(ProviderProfile provider, CancellationToken cancellationToken = default)
        {
            _all.RemoveAll(p => p.Id == provider.Id);
            _all.Add(provider);
            return Task.CompletedTask;
        }

        public Task SetActiveAsync(string providerId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task SetEnabledAsync(
            string providerId,
            bool enabled,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<bool> DeleteAsync(string providerId, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task SetHealthAsync(
            string providerId,
            ProviderHealth health,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
