using AdamCodexHub.Core.Domain;
using AdamCodexHub.Core.Interfaces;
using AdamCodexHub.Core.Services;
using AdamCodexHub.Infrastructure.Database;
using AdamCodexHub.Infrastructure.Models;
using AdamCodexHub.Infrastructure.Paths;
using AdamCodexHub.Providers;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AdamCodexHub.Core.Tests;

/// <summary>
/// The catalogue has to keep itself current: a provider that ships a model (DeepSeek's V4.1 Flash)
/// or comes back from a quota outage must not need a trip to Setup → "Scan models". These tests pin
/// the sweep's rules and the two behaviours that made that trip necessary in the first place.
/// </summary>
public sealed class ModelCatalogRefreshTests
{
    [Fact]
    public async Task SweepRefreshesTheActiveProviderFirstAndLeavesFreshOnesAlone()
    {
        var active = Provider("hhtech");
        var other = Provider("deepseek");
        var providers = new FakeProviderManager(active, other, activeProvider: active);
        var models = new FakeModelStore
        {
            Models =
            {
                // deepseek was read five minutes ago — nothing has changed since.
                Model("deepseek", "deepseek-flash", DateTimeOffset.UtcNow.AddMinutes(-5))
            }
        };
        var discovery = new RecordingDiscovery(models);
        using var service = new ModelCatalogRefreshService(providers, models, discovery);

        var refreshed = await service.RefreshOnceAsync();

        Assert.Equal(1, refreshed);
        Assert.Equal(new[] { "hhtech" }, discovery.Scanned);
    }

    [Fact]
    public async Task SweepSkipsDisabledProvidersAndTheCodexAccount()
    {
        var account = Provider("codex-account");
        var disabled = Provider("ttmapi", enabled: false);
        var live = Provider("deepseek");
        var providers = new FakeProviderManager(account, disabled, live, activeProvider: account);
        var models = new FakeModelStore();
        var discovery = new RecordingDiscovery(models);
        using var service = new ModelCatalogRefreshService(providers, models, discovery);

        await service.RefreshOnceAsync();

        Assert.Equal(new[] { "deepseek" }, discovery.Scanned);
    }

    [Fact]
    public async Task AProviderWithNothingStoredIsNeverFresh()
    {
        var providers = new FakeProviderManager(Provider("deepseek"), activeProvider: null);
        var models = new FakeModelStore();
        using var service = new ModelCatalogRefreshService(providers, models, new RecordingDiscovery(models));

        Assert.False(await service.IsFreshAsync("deepseek"));

        models.Models.Add(Model("deepseek", "deepseek-flash", DateTimeOffset.UtcNow));

        Assert.True(await service.IsFreshAsync("deepseek"));
    }

    [Fact]
    public async Task OnDemandRefreshIgnoresTheFreshnessWindow()
    {
        var providers = new FakeProviderManager(Provider("deepseek"), activeProvider: null);
        var models = new FakeModelStore
        {
            Models = { Model("deepseek", "deepseek-flash", DateTimeOffset.UtcNow) }
        };
        var discovery = new RecordingDiscovery(models);
        using var service = new ModelCatalogRefreshService(providers, models, discovery);

        Assert.True(await service.IsFreshAsync("deepseek"));

        var refreshed = await service.RefreshProviderAsync("deepseek");

        Assert.True(refreshed);
        Assert.Equal(new[] { "deepseek" }, discovery.Scanned);
    }

    [Fact]
    public async Task AProviderThatFailsIsLoggedAndDoesNotStopTheSweep()
    {
        var broken = Provider("hhtech");
        var live = Provider("deepseek");
        var providers = new FakeProviderManager(broken, live, activeProvider: broken);
        var models = new FakeModelStore();
        var discovery = new RecordingDiscovery(models) { FailingProviders = { "hhtech" } };
        using var service = new ModelCatalogRefreshService(providers, models, discovery);
        var log = new List<string>();
        // The sweep reports from more than one thread now, so the collector has to be safe to write
        // from all of them.
        service.LogMessage += (message, _) =>
        {
            lock (log)
            {
                log.Add(message);
            }
        };

        var refreshed = await service.RefreshOnceAsync();

        Assert.Equal(1, refreshed);
        // Both catalogues are read in the same wave now, so the order they are recorded in is not a
        // promise; that both were read is.
        Assert.Equal(
            new[] { "deepseek", "hhtech" },
            discovery.Scanned.OrderBy(id => id, StringComparer.Ordinal).ToArray());
        Assert.Contains(log, line => line.Contains("hhtech: catalogue refresh failed"));
    }

    // ---------------------------------------------------------------------------------------
    // The two behaviours that made the manual scan necessary.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task ScanRestoresAModelTheProviderOutageHadUnpicked()
    {
        await using var fixture = new DatabaseFixture();
        // The fingerprint a failed compatibility run left behind: the model is off, its state says
        // Failed, and it had been verified (only a verified model can be enabled in the first place).
        await fixture.Store.UpsertAsync(Model(
            "hhtech",
            "claude-5.5",
            enabled: false,
            state: ModelLifecycleState.Failed,
            verifiedAt: DateTimeOffset.UtcNow.AddDays(-1),
            score: 0));

        var discovery = Discovery(fixture.Store, Model("hhtech", "claude-5.5", DateTimeOffset.UtcNow));
        await discovery.ScanAsync("hhtech");

        var stored = await fixture.Store.GetAsync("hhtech", "claude-5.5");
        Assert.NotNull(stored);
        Assert.True(stored.Enabled);
        Assert.Equal(ModelLifecycleState.Enabled, stored.State);
    }

    [Fact]
    public async Task ScanLeavesAModelTheUserTurnedOffAlone()
    {
        await using var fixture = new DatabaseFixture();
        // The user's own "off" writes Disabled — that choice is not ours to undo.
        await fixture.Store.UpsertAsync(Model(
            "hhtech",
            "deepseek-v4",
            enabled: false,
            state: ModelLifecycleState.Disabled,
            verifiedAt: DateTimeOffset.UtcNow.AddDays(-1),
            score: 0));

        var discovery = Discovery(fixture.Store, Model("hhtech", "deepseek-v4", DateTimeOffset.UtcNow));
        await discovery.ScanAsync("hhtech");

        var stored = await fixture.Store.GetAsync("hhtech", "deepseek-v4");
        Assert.NotNull(stored);
        Assert.False(stored.Enabled);
        Assert.NotEqual(ModelLifecycleState.Enabled, stored.State);
    }

    [Fact]
    public async Task ScanNeverEnablesAModelThatWasNeverVerified()
    {
        await using var fixture = new DatabaseFixture();
        await fixture.Store.UpsertAsync(Model(
            "hhtech",
            "brand-new",
            enabled: false,
            state: ModelLifecycleState.Failed,
            verifiedAt: null,
            score: null));

        var discovery = Discovery(fixture.Store, Model("hhtech", "brand-new", DateTimeOffset.UtcNow));
        await discovery.ScanAsync("hhtech");

        var stored = await fixture.Store.GetAsync("hhtech", "brand-new");
        Assert.NotNull(stored);
        Assert.False(stored.Enabled);
    }

    [Fact]
    public async Task AFailedCompatibilityRunKeepsTheUsersEnableFlag()
    {
        await using var fixture = new DatabaseFixture();
        await fixture.Store.UpsertAsync(Model(
            "hhtech",
            "claude-5.5",
            enabled: true,
            state: ModelLifecycleState.Enabled,
            verifiedAt: DateTimeOffset.UtcNow.AddDays(-1),
            score: 95));

        // The provider is out of quota: every probe comes back with a score of zero.
        await fixture.Store.SaveCompatibilityAsync(new CompatibilityResult
        {
            ProviderId = "hhtech",
            ModelId = "claude-5.5",
            VerifiedAt = DateTimeOffset.UtcNow,
            Text = false,
            Responses = false,
            Streaming = false,
            Score = 0,
            Notes = "401 Unauthorized"
        });

        var stored = await fixture.Store.GetAsync("hhtech", "claude-5.5");
        Assert.NotNull(stored);
        // Out of the pickers while it does not work (the state says Failed) …
        Assert.Equal(ModelLifecycleState.Failed, stored.State);
        // … but the user's choice is still theirs, so the next scan can put it straight back.
        Assert.True(stored.Enabled);
    }

    // ---------------------------------------------------------------------------------------

    private static ProviderProfile Provider(string id, bool enabled = true) => new()
    {
        Id = id,
        Name = id,
        Adapter = id == "codex-account" ? "codex-account" : "openai-compatible",
        BaseUrl = $"https://{id}.example.test/v1",
        Enabled = enabled
    };

    private static ModelDescriptor Model(
        string providerId,
        string remoteId,
        DateTimeOffset? lastSeenAt = null,
        bool enabled = false,
        ModelLifecycleState state = ModelLifecycleState.Enabled,
        DateTimeOffset? verifiedAt = null,
        int? score = null) => new()
    {
        ProviderId = providerId,
        RemoteId = remoteId,
        DisplayName = remoteId,
        Enabled = enabled,
        State = state,
        LastSeenAt = lastSeenAt,
        LastVerifiedAt = verifiedAt,
        CompatibilityScore = score
    };

    /// <summary>The real service, wired to a real store — so what the scan writes is what the app reads.</summary>
    private static ModelDiscoveryService Discovery(IModelStore store, params ModelDescriptor[] catalog) =>
        new(
            new FakeProviderManager(Provider("hhtech"), activeProvider: Provider("hhtech")),
            new FakeKeys(),
            store,
            new[] { (IProviderAdapter)new FakeAdapter(catalog) });

    [Fact]
    public async Task SeveralCataloguesAreReadAtOnce()
    {
        // Providers do not share a key, so the sweep reads three of them together: one at a time made
        // a full sweep of 17 catalogues a two-minute queue of single requests.
        var active = Provider("p1");
        var providers = new FakeProviderManager(
            Provider("p1"),
            Provider("p2"),
            Provider("p3"),
            activeProvider: active);
        var models = new FakeModelStore();
        var discovery = new RecordingDiscovery(models) { WaitFor = 3 };
        using var service = new ModelCatalogRefreshService(providers, models, discovery);

        var refreshed = await service.RefreshOnceAsync();

        Assert.Equal(3, refreshed);
        Assert.Equal(3, discovery.MaxConcurrent);
    }

    private sealed class RecordingDiscovery : IModelDiscoveryService
    {
        private readonly FakeModelStore _models;
        private readonly object _gate = new();
        private readonly TaskCompletionSource _allArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _arrived;
        private int _inFlight;
        private int _maxConcurrent;

        public RecordingDiscovery(FakeModelStore models) => _models = models;

        public List<string> Scanned { get; } = new();

        public HashSet<string> FailingProviders { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>How many scans must be in flight together before any of them is allowed to
        /// finish. A sweep that reads one catalogue at a time can never satisfy this.</summary>
        public int WaitFor { get; init; } = 1;

        public int MaxConcurrent => Volatile.Read(ref _maxConcurrent);

        /// <summary>Waits until <see cref="WaitFor"/> scans are running at the same time. A sweep that
        /// reads one catalogue at a time lets this wait run out.</summary>
        private async Task WaitForTheOthersAsync(CancellationToken cancellationToken)
        {
            Task allArrived;
            lock (_gate)
            {
                if (Interlocked.Increment(ref _arrived) >= WaitFor)
                {
                    _allArrived.TrySetResult();
                }

                allArrived = _allArrived.Task;
            }

            try
            {
                await allArrived.WaitAsync(TimeSpan.FromSeconds(1.5), cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Nobody else showed up: a one-at-a-time sweep.
            }
        }

        public async Task<IReadOnlyList<ModelDescriptor>> ScanAsync(
            string providerId,
            CancellationToken cancellationToken = default)
        {
            var inFlight = Interlocked.Increment(ref _inFlight);
            lock (Scanned)
            {
                Scanned.Add(providerId);
                _maxConcurrent = Math.Max(_maxConcurrent, inFlight);
            }

            try
            {
                await WaitForTheOthersAsync(cancellationToken).ConfigureAwait(false);

                if (FailingProviders.Contains(providerId))
                {
                    throw new InvalidOperationException($"{providerId} is out of quota.");
                }

                // Stand in for the real scan: the provider answered, so its models are seen now.
                for (var index = 0; index < _models.Models.Count; index++)
                {
                    if (_models.Models[index].ProviderId == providerId)
                    {
                        _models.Models[index] = _models.Models[index] with { LastSeenAt = DateTimeOffset.UtcNow };
                    }
                }

                return _models.Models.Where(m => m.ProviderId == providerId).ToArray();
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }
    }

    private sealed class FakeAdapter : IProviderAdapter
    {
        private readonly IReadOnlyList<ModelDescriptor> _catalog;

        public FakeAdapter(IReadOnlyList<ModelDescriptor> catalog) => _catalog = catalog;

        public string AdapterId => "openai-compatible";

        public Task<IReadOnlyList<ModelDescriptor>> ListModelsAsync(
            ProviderProfile provider,
            string? apiKey,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_catalog);

        public Task<ProviderProbeResult> ProbeAsync(
            ProviderProfile provider,
            string? apiKey,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<CompatibilityResult> TestModelAsync(
            ProviderProfile provider,
            string modelId,
            string? apiKey,
            IProgress<ModelTestProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeKeys : IKeyPoolService
    {
        public Task<ProviderKeyInfo> AddAsync(
            string providerId,
            string label,
            string secret,
            int priority = 100,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ProviderKeyInfo>> ListAsync(
            string providerId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProviderKeyInfo>>(Array.Empty<ProviderKeyInfo>());

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

    private sealed class FakeModelStore : IModelStore
    {
        public List<ModelDescriptor> Models { get; } = new();

        public Task InitializeAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<ModelDescriptor>> GetAllAsync(
            string providerId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ModelDescriptor>>(
                Models.Where(m => m.ProviderId == providerId).ToArray());

        public Task<ModelDescriptor?> GetAsync(
            string providerId,
            string modelId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Models.FirstOrDefault(m =>
                m.ProviderId == providerId && m.RemoteId == modelId));

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
        private readonly List<ProviderProfile> _all;
        private readonly ProviderProfile? _active;

        public FakeProviderManager(ProviderProfile provider, ProviderProfile? activeProvider) =>
            (_all, _active) = (new List<ProviderProfile> { provider }, activeProvider);

        public FakeProviderManager(
            ProviderProfile first,
            ProviderProfile second,
            ProviderProfile? activeProvider) =>
            (_all, _active) = (new List<ProviderProfile> { first, second }, activeProvider);

        public FakeProviderManager(
            ProviderProfile first,
            ProviderProfile second,
            ProviderProfile third,
            ProviderProfile? activeProvider) =>
            (_all, _active) = (new List<ProviderProfile> { first, second, third }, activeProvider);

        public IReadOnlyList<string> StartupWarnings => Array.Empty<string>();

        public Task<ProviderProfile?> GetActiveAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_active);

        public Task<ProviderProfile?> GetAsync(
            string providerId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_all.FirstOrDefault(p => p.Id == providerId));

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

    private sealed class DatabaseFixture : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "AdamCodexHub.Tests",
            Guid.NewGuid().ToString("N"));

        private readonly SqliteDatabase _database;

        public DatabaseFixture()
        {
            Paths = AppPaths.ForRoot(_root);
            _database = new SqliteDatabase(Paths);
            _database.InitializeAsync().GetAwaiter().GetResult();
            Store = new SqliteModelStore(_database);
        }

        public AppPaths Paths { get; }

        public SqliteModelStore Store { get; }

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();

            if (Directory.Exists(_root))
            {
                try
                {
                    Directory.Delete(_root, recursive: true);
                }
                catch
                {
                    // Temp cleanup is best-effort.
                }
            }

            return ValueTask.CompletedTask;
        }
    }
}
