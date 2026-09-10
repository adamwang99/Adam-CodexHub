using AdamCodexHub.Core.Domain;
using AdamCodexHub.Core.Interfaces;
using AdamCodexHub.Core.Services;
using Xunit;

namespace AdamCodexHub.Core.Tests;

/// <summary>
/// The background pass behind the Codex Desktop model picker. Two properties matter and both were
/// user-visible: a pass over a stale catalogue must not be the sum of every probe (a single model
/// waiting on its budget used to hold the whole list back), and a provider pushing back must not be
/// recorded as a verdict about the models (that is how the picker empties itself).
/// </summary>
public sealed class CodexReadinessPingTests
{
    private const string ProviderId = "hhtech";

    [Fact]
    public async Task TwoModelsAreProbedAtTheSameTime()
    {
        // Both probes have to be inside the checker before either of them answers. A one-at-a-time
        // pass can never satisfy that: the first probe waits out the timeout and reports a maximum
        // of one model in flight, which is what this asserts against.
        var store = new FakeModelStore(Model("a"), Model("b"));
        var checker = new RecordingChecker { WaitFor = 2 };
        using var service = new CodexReadinessPingService(
            new FakeProviderManager(ProviderId),
            store,
            checker,
            new FakeReadinessStore());

        var stored = await service.PingOnceAsync();

        Assert.Equal(2, stored);
        Assert.Equal(2, checker.MaxConcurrent);
        Assert.Equal(new[] { "a", "b" }, checker.Probed.OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public async Task AStaleReadyModelIsCheckedBeforeOneThatNeverAnswered()
    {
        // The picker came up as a wall of "(U)" because the pass spent its turns on the image endpoints
        // HHTech publishes, the provider refused those, the pass gave up — and the models the user can
        // actually run never got a fresh verdict. A model that had a ready verdict earlier is the one
        // to re-check first, ahead of one that has never answered.
        var readiness = new FakeReadinessStore();
        readiness.Seed(new CodexReadiness(
            ProviderId,
            "usable",
            Ready: true,
            CheckedAt: DateTimeOffset.UtcNow - TimeSpan.FromHours(9),
            LatencyMs: 1200,
            Detail: null));
        var store = new FakeModelStore(Model("image-thing"), Model("usable"));
        var checker = new RecordingChecker();
        using var service = new CodexReadinessPingService(
            new FakeProviderManager(ProviderId),
            store,
            checker,
            readiness)
        {
            BatchSize = 1,
            SweepBatchSize = 1
        };

        await service.PingOnceAsync();

        Assert.Equal(new[] { "usable" }, checker.Probed);
    }

    [Fact]
    public async Task ARateLimitedKeyParksTheWholeProviderInsteadOfOneModelAtATime()
    {
        // The key is what every probe spends and the user's own Codex session needs it. Parking one
        // model at a time kept the key busy and the session showed "Reconnecting" with provider errors.
        var store = new FakeModelStore(Model("a"), Model("b"), Model("c"));
        var checker = new RecordingChecker
        {
            RefuseDetail = "HTTP 503 on attempt 1: API key is rate limited."
        };
        using var service = new CodexReadinessPingService(
            new FakeProviderManager(ProviderId),
            store,
            checker,
            new FakeReadinessStore());
        var logs = new List<string>();
        service.LogMessage += (message, _) =>
        {
            lock (logs)
            {
                logs.Add(message);
            }
        };

        var stored = await service.PingOnceAsync();

        Assert.Equal(0, stored);
        Assert.Contains(logs, line => line.Contains("rate limited", StringComparison.OrdinalIgnoreCase));
        // The pass ends at the first refusal (two probes may be in flight) instead of spending the key
        // on the rest of the catalogue.
        Assert.InRange(checker.Probed.Count, 1, 2);

        // And the next tick does not touch the provider at all.
        var asked = checker.Probed.Count;
        Assert.Equal(0, await service.PingOnceAsync());
        Assert.Equal(asked, checker.Probed.Count);
    }

    [Fact]
    public async Task AProviderRefusalIsNotStoredAsAVerdict()
    {
        // 503 is the provider being busy, not a statement about these models: the verdict already
        // stored has to survive, the model has to be left alone for a while, and — the part that made
        // a picker come up as a wall of "(U)" — the refusal of one model must not abandon the pass and
        // leave every other model unverified.
        var readiness = new FakeReadinessStore();
        readiness.Seed(new CodexReadiness(
            ProviderId,
            "a",
            Ready: true,
            CheckedAt: DateTimeOffset.UtcNow.AddMinutes(-10),
            LatencyMs: 1200,
            Detail: null));
        var store = new FakeModelStore(Model("a"), Model("b"), Model("c"), Model("d"));
        var checker = new RecordingChecker { Refuse = true };
        using var service = new CodexReadinessPingService(
            new FakeProviderManager(ProviderId),
            store,
            checker,
            readiness);
        var logs = new List<string>();
        // Probes report from two threads at once.
        service.LogMessage += (message, _) =>
        {
            lock (logs)
            {
                logs.Add(message);
            }
        };

        var stored = await service.PingOnceAsync();

        Assert.Equal(0, stored);
        Assert.True(readiness.Get(ProviderId, "a")!.Ready);
        Assert.Contains(logs, line => line.Contains("verdict kept", StringComparison.Ordinal));
        // "a" already has a fresh verdict, so it is not asked at all; the other three are asked and
        // refused, and the pass still reaches all of them.
        Assert.Equal(3, checker.Probed.Count);
        Assert.DoesNotContain("a", checker.Probed);
        Assert.DoesNotContain(logs, line => line.Contains("stopping this pass", StringComparison.Ordinal));

        // And the next pass does not buy the same refusals again.
        Assert.Equal(0, await service.PingOnceAsync());
        Assert.Equal(3, checker.Probed.Count);
    }

    [Fact]
    public async Task TheOpeningSweepCoversTheWholeCatalogueOnce()
    {
        // A provider whose verdicts are missing or stale has to be rebuilt in one pass: four models
        // per five minutes left the picker half empty for an hour after a rescan.
        var store = new FakeModelStore(
            Model("m1"), Model("m2"), Model("m3"), Model("m4"), Model("m5"), Model("m6"));
        // Every model answers "not ready", so they all stay candidates — that is what makes the
        // difference between the opening sweep and the incremental ticks observable.
        var checker = new RecordingChecker { NotReady = true };
        using var service = new CodexReadinessPingService(
            new FakeProviderManager(ProviderId),
            store,
            checker,
            new FakeReadinessStore())
        {
            BatchSize = 2,
            SweepBatchSize = 6
        };

        Assert.Equal(6, await service.PingOnceAsync());
        Assert.Equal(6, checker.Probed.Count);

        // Later ticks are incremental again.
        Assert.Equal(2, await service.PingOnceAsync());
        Assert.Equal(8, checker.Probed.Count);
    }

    private static ModelDescriptor Model(string remoteId) => new()
    {
        ProviderId = ProviderId,
        RemoteId = remoteId,
        DisplayName = remoteId,
        State = ModelLifecycleState.Enabled,
        Enabled = true
    };

    /// <summary>Answers like the real checker, but records how many probes were in flight together
    /// and can hold them until the expected number has arrived.</summary>
    private sealed class RecordingChecker : ICodexReadinessChecker
    {
        private readonly object _gate = new();
        private readonly TaskCompletionSource _allArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _arrived;
        private int _inFlight;
        private int _maxConcurrent;

        public int WaitFor { get; init; }

        public TimeSpan WaitTimeout { get; init; } = TimeSpan.FromSeconds(1.5);

        public bool Refuse { get; init; }

        /// <summary>What the provider answered instead of a result — used to spell out a rate limit,
        /// which is about the key rather than about the model.</summary>
        public string? RefuseDetail { get; init; }

        /// <summary>Answers "the model did not call the tool": a verdict, so the model stays a
        /// candidate and the next pass has something to do.</summary>
        public bool NotReady { get; init; }

        public List<string> Probed { get; } = new();

        public int MaxConcurrent => Volatile.Read(ref _maxConcurrent);

        /// <summary>Waits until <see cref="WaitFor"/> probes are inside the checker at the same time.
        /// A pass that probes one model at a time lets this wait run out, which is what the
        /// concurrency test asserts against.</summary>
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
                await allArrived.WaitAsync(WaitTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Nobody else showed up: a sequential pass.
            }
        }

        public async Task<CodexReadiness> CheckAsync(
            string providerId,
            string modelId,
            CancellationToken cancellationToken = default)
        {
            var inFlight = Interlocked.Increment(ref _inFlight);
            lock (_gate)
            {
                Probed.Add(modelId);
                _maxConcurrent = Math.Max(_maxConcurrent, inFlight);
            }

            try
            {
                await WaitForTheOthersAsync(cancellationToken).ConfigureAwait(false);

                return new CodexReadiness(
                    providerId,
                    modelId,
                    Ready: !Refuse && RefuseDetail is null && !NotReady,
                    CheckedAt: DateTimeOffset.UtcNow,
                    LatencyMs: Refuse || NotReady || RefuseDetail is not null ? null : 12,
                    Detail: RefuseDetail ?? (Refuse
                        ? "HTTP 503 on attempt 1"
                        : NotReady
                            ? "the model did not call the tool"
                            : null));
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }
    }

    private sealed class FakeReadinessStore : ICodexReadinessStore
    {
        private readonly Dictionary<string, CodexReadiness> _verdicts = new(StringComparer.OrdinalIgnoreCase);

        public void Seed(CodexReadiness readiness) => _verdicts[Key(readiness.ProviderId, readiness.ModelId)] = readiness;

        public CodexReadiness? Get(string providerId, string modelId) =>
            _verdicts.TryGetValue(Key(providerId, modelId), out var verdict) ? verdict : null;

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<CodexReadiness>> GetAllAsync(
            string providerId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CodexReadiness>>(
                _verdicts.Values.Where(v => v.ProviderId == providerId).ToArray());

        public Task<CodexReadiness?> GetAsync(
            string providerId,
            string modelId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Get(providerId, modelId));

        public Task SaveAsync(CodexReadiness readiness, CancellationToken cancellationToken = default)
        {
            _verdicts[Key(readiness.ProviderId, readiness.ModelId)] = readiness;
            return Task.CompletedTask;
        }

        private static string Key(string providerId, string modelId) => $"{providerId}/{modelId}";
    }

    private sealed class FakeModelStore : IModelStore
    {
        private readonly List<ModelDescriptor> _models;

        public FakeModelStore(params ModelDescriptor[] models) => _models = models.ToList();

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<ModelDescriptor>> GetAllAsync(
            string providerId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ModelDescriptor>>(
                _models.Where(x => x.ProviderId == providerId).ToArray());

        public Task<ModelDescriptor?> GetAsync(
            string providerId,
            string modelId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_models.FirstOrDefault(x =>
                x.ProviderId == providerId && x.RemoteId == modelId));

        public Task UpsertAsync(ModelDescriptor model, CancellationToken cancellationToken = default)
        {
            _models.RemoveAll(x => x.ProviderId == model.ProviderId && x.RemoteId == model.RemoteId);
            _models.Add(model);
            return Task.CompletedTask;
        }

        public Task MarkUnavailableExceptAsync(
            string providerId,
            IReadOnlySet<string> seenModelIds,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SetEnabledAsync(
            string providerId,
            string modelId,
            bool enabled,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SaveCompatibilityAsync(
            CompatibilityResult result,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<CompatibilityResult?> GetLatestCompatibilityAsync(
            string providerId,
            string modelId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<CompatibilityResult?>(null);
    }

    private sealed class FakeProviderManager : IProviderManager
    {
        private readonly Dictionary<string, ProviderProfile> _providers;

        public FakeProviderManager(string activeProviderId)
        {
            _providers = new Dictionary<string, ProviderProfile>(StringComparer.OrdinalIgnoreCase)
            {
                [activeProviderId] = new()
                {
                    Id = activeProviderId,
                    Name = activeProviderId,
                    Adapter = "openai-compatible",
                    BaseUrl = "https://api.example.test/v1",
                    Enabled = true
                }
            };
            Active = _providers[activeProviderId];
        }

        public ProviderProfile? Active { get; private set; }

        public IReadOnlyList<string> StartupWarnings => Array.Empty<string>();

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<ProviderProfile>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProviderProfile>>(_providers.Values.ToArray());

        public Task<ProviderProfile?> GetAsync(string providerId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_providers.TryGetValue(providerId, out var provider) ? provider : null);

        public Task SaveAsync(ProviderProfile provider, CancellationToken cancellationToken = default)
        {
            _providers[provider.Id] = provider;
            return Task.CompletedTask;
        }

        public Task SetEnabledAsync(string providerId, bool enabled, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<bool> DeleteAsync(string providerId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_providers.Remove(providerId));

        public Task SetActiveAsync(string providerId, CancellationToken cancellationToken = default)
        {
            Active = _providers.TryGetValue(providerId, out var provider) ? provider : null;
            return Task.CompletedTask;
        }

        public Task<ProviderProfile?> GetActiveAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Active);

        public Task SetHealthAsync(
            string providerId,
            ProviderHealth health,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
