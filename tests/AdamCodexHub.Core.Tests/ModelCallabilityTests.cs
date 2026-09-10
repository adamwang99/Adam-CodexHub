using AdamCodexHub.Core.Domain;
using AdamCodexHub.Core.Interfaces;
using AdamCodexHub.Core.Services;
using Xunit;

namespace AdamCodexHub.Core.Tests;

/// <summary>
/// The latency-aware classifier and the bounded background auto-ping that feeds it.
/// </summary>
public sealed class ModelCallabilityTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static CompatibilityResult Result(
        int score = 90,
        int? firstByteMs = 4000,
        int? totalMs = 4200,
        bool text = true,
        bool responses = true,
        bool streaming = true,
        DateTimeOffset? verifiedAt = null) => new()
    {
        ProviderId = "hhtech",
        ModelId = "claude-opus-4-7",
        VerifiedAt = verifiedAt ?? Now.AddMinutes(-5),
        Text = text,
        Responses = responses,
        Streaming = streaming,
        ToolCalling = true,
        StructuredJson = true,
        Score = score,
        FirstByteMs = firstByteMs,
        TotalMs = totalMs
    };

    [Fact]
    public void NoResultIsUnknown()
    {
        Assert.Equal(
            Callability.Unknown,
            ModelCallability.Classify(null, Now, ModelCallability.VerificationTtl));
    }

    [Fact]
    public void StaleResultIsUnknownAgain()
    {
        var stale = Result(verifiedAt: Now - ModelCallability.VerificationTtl - TimeSpan.FromMinutes(1));

        Assert.Equal(
            Callability.Unknown,
            ModelCallability.Classify(stale, Now, ModelCallability.VerificationTtl));
    }

    [Fact]
    public void ResultInsideTheTtlIsStillTrusted()
    {
        var fresh = Result(verifiedAt: Now - ModelCallability.VerificationTtl + TimeSpan.FromMinutes(1));

        Assert.Equal(
            Callability.Callable,
            ModelCallability.Classify(fresh, Now, ModelCallability.VerificationTtl));
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void MissingRequiredCapabilityIsSkipped(bool text, bool responses, bool streaming)
    {
        var result = Result(text: text, responses: responses, streaming: streaming);

        Assert.Equal(
            Callability.Skip,
            ModelCallability.Classify(result, Now, ModelCallability.VerificationTtl));
    }

    [Fact]
    public void FastModelIsCallable()
    {
        var fast = Result(firstByteMs: 6300, totalMs: 4000);

        Assert.Equal(
            Callability.Callable,
            ModelCallability.Classify(fast, Now, ModelCallability.VerificationTtl));
    }

    [Fact]
    public void LatencyExactlyAtTheThresholdIsStillCallable()
    {
        var borderline = Result(
            firstByteMs: ModelCallability.SlowThresholdMs,
            totalMs: ModelCallability.SlowThresholdMs);

        Assert.Equal(
            Callability.Callable,
            ModelCallability.Classify(borderline, Now, ModelCallability.VerificationTtl));
    }

    [Fact]
    public void SlowFirstByteIsSlowEvenWhenTheTotalLooksFine()
    {
        // Measured on HHTech 2026-09-10: claude-opus-4-7[1M] streams its first byte at 37.7s.
        var slow = Result(firstByteMs: 37_700, totalMs: 4000);

        Assert.Equal(
            Callability.Slow,
            ModelCallability.Classify(slow, Now, ModelCallability.VerificationTtl));
    }

    [Fact]
    public void SlowTotalIsSlow()
    {
        // claude-opus-4-7 answered in 77s (non-stream) while still working.
        var slow = Result(firstByteMs: 4000, totalMs: 77_000);

        Assert.Equal(
            Callability.Slow,
            ModelCallability.Classify(slow, Now, ModelCallability.VerificationTtl));
    }

    [Fact]
    public void MissingLatencySamplesDoNotMakeAModelSlow()
    {
        var unknownLatency = Result(firstByteMs: null, totalMs: null);

        Assert.Equal(
            Callability.Callable,
            ModelCallability.Classify(unknownLatency, Now, ModelCallability.VerificationTtl));
    }

    [Fact]
    public void TtlIsAFewHours()
    {
        Assert.InRange(ModelCallability.VerificationTtl.TotalHours, 1, 12);
        Assert.InRange(ModelCallability.SlowThresholdMs, 10_000, 30_000);
    }

    // ---- background auto-ping -------------------------------------------------

    private const string ActiveProviderId = "hhtech";

    [Fact]
    public async Task AutoPingRefreshesOnlyTheActiveProvidersModelsWithinTheBatch()
    {
        var store = new FakeModelStore(
            Model("hhtech", "unknown", enabled: true),
            Model("hhtech", "skip", enabled: true),
            Model("hhtech", "fast", enabled: true),
            Model("hhtech", "disabled", enabled: false),
            Model("deepseek", "other-provider", enabled: true));
        store.SetStored("hhtech", "skip", Result(score: 0, text: false));
        store.SetStored("hhtech", "fast", Result(score: 90));
        var compatibility = new FakeCompatibilityService();
        using var service = new ModelAutoPingService(
            new FakeProviderManager(ActiveProviderId),
            store,
            compatibility)
        {
            BatchSize = 2
        };

        var refreshed = await service.PingOnceAsync();

        Assert.Equal(2, refreshed);
        Assert.Equal(2, compatibility.Tested.Count);
        // Unknown and lowest-score first.
        Assert.Contains("unknown", compatibility.Tested);
        Assert.Contains("skip", compatibility.Tested);
        Assert.DoesNotContain("fast", compatibility.Tested);
        Assert.DoesNotContain("disabled", compatibility.Tested);
        Assert.DoesNotContain("other-provider", compatibility.Tested);
        Assert.Equal("unknown", compatibility.Tested[0]);
        Assert.Equal(1, service.TickCount);
        Assert.NotNull(service.LastTickAt);
    }

    [Fact]
    public async Task AutoPingSkipsProvidersWithoutAnActiveKeyedChannel()
    {
        var store = new FakeModelStore(Model("hhtech", "fast", enabled: true));
        var compatibility = new FakeCompatibilityService();
        using var service = new ModelAutoPingService(
            new FakeProviderManager("codex-account"),
            store,
            compatibility);

        Assert.Equal(0, await service.PingOnceAsync());
        Assert.Empty(compatibility.Tested);
    }

    [Fact]
    public async Task AutoPingNeverProbesAModelThatIsAlreadyInFlight()
    {
        var store = new FakeModelStore(Model(ActiveProviderId, "busy", enabled: true));
        var compatibility = new FakeCompatibilityService();
        using var service = new ModelAutoPingService(
            new FakeProviderManager(ActiveProviderId),
            store,
            compatibility);

        using (ModelProbeGate.Enter(ActiveProviderId, "busy"))
        {
            Assert.Equal(0, await service.PingOnceAsync());
        }

        Assert.Empty(compatibility.Tested);
    }

    [Fact]
    public async Task AutoPingIsPausableAndPublishesRefreshedResults()
    {
        var store = new FakeModelStore(Model(ActiveProviderId, "fast", enabled: true));
        var compatibility = new FakeCompatibilityService();
        using var service = new ModelAutoPingService(
            new FakeProviderManager(ActiveProviderId),
            store,
            compatibility);
        var ticks = new List<ModelAutoPingTick>();
        service.TickCompleted += ticks.Add;

        service.IsPaused = true;
        Assert.Equal(0, await service.RunTickAsync());
        Assert.Empty(compatibility.Tested);

        service.IsPaused = false;
        Assert.Equal(1, await service.RunTickAsync());
        Assert.Single(ticks);
        Assert.Equal(ActiveProviderId, ticks[0].ProviderId);
        Assert.Equal("fast", Assert.Single(ticks[0].Results).ModelId);
    }

    [Fact]
    public async Task AutoPingKeepsGoingWhenOneModelFails()
    {
        var store = new FakeModelStore(
            Model(ActiveProviderId, "broken", enabled: true),
            Model(ActiveProviderId, "fine", enabled: true));
        var compatibility = new FakeCompatibilityService { FailingModelId = "broken" };
        using var service = new ModelAutoPingService(
            new FakeProviderManager(ActiveProviderId),
            store,
            compatibility);

        var refreshed = await service.PingOnceAsync();

        Assert.Equal(1, refreshed);
        Assert.Contains("fine", compatibility.Tested);
    }

    private static ModelDescriptor Model(string providerId, string remoteId, bool enabled) => new()
    {
        ProviderId = providerId,
        RemoteId = remoteId,
        DisplayName = remoteId,
        State = enabled ? ModelLifecycleState.Enabled : ModelLifecycleState.Disabled,
        Enabled = enabled
    };

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

        public Task InitializeAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

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

    private sealed class FakeModelStore : IModelStore
    {
        private readonly List<ModelDescriptor> _models;
        private readonly Dictionary<string, CompatibilityResult> _stored = new(StringComparer.OrdinalIgnoreCase);

        public FakeModelStore(params ModelDescriptor[] models) => _models = models.ToList();

        public void SetStored(string providerId, string modelId, CompatibilityResult result) =>
            _stored[$"{providerId}/{modelId}"] = result;

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
            CancellationToken cancellationToken = default)
        {
            _stored[$"{result.ProviderId}/{result.ModelId}"] = result;
            return Task.CompletedTask;
        }

        public Task<CompatibilityResult?> GetLatestCompatibilityAsync(
            string providerId,
            string modelId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                _stored.TryGetValue($"{providerId}/{modelId}", out var result) ? result : null);
    }

    private sealed class FakeCompatibilityService : ICompatibilityService
    {
        public List<string> Tested { get; } = new();

        public string? FailingModelId { get; init; }

        public Task<CompatibilityResult> TestAsync(
            string providerId,
            string modelId,
            IProgress<ModelTestProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Tested.Add(modelId);
            if (string.Equals(modelId, FailingModelId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"probe failed for {modelId}");
            }

            return Task.FromResult(new CompatibilityResult
            {
                ProviderId = providerId,
                ModelId = modelId,
                VerifiedAt = DateTimeOffset.UtcNow,
                Text = true,
                Responses = true,
                Streaming = true,
                Score = 90,
                FirstByteMs = 6300,
                TotalMs = 4000
            });
        }
    }

    /// <summary>The one-letter tag shown in parentheses in the tray menu, so the state is
    /// readable even where the colour is not (F = fast, N = normal, S = slow, U = unchecked,
    /// X = skipped). The first byte decides fast vs normal: that is the wait you feel.</summary>
    [Theory]
    [InlineData(Callability.Callable, 1200, 3000, "F")]
    [InlineData(Callability.Callable, 5000, 5200, "F")]
    [InlineData(Callability.Callable, 6000, 6500, "N")]
    [InlineData(Callability.Callable, null, 4000, "F")]
    [InlineData(Callability.Callable, null, null, "N")]
    [InlineData(Callability.Slow, 16_000, 40_000, "S")]
    [InlineData(Callability.Skip, 900, 900, "X")]
    [InlineData(Callability.Unknown, null, null, "U")]
    public void StatusTagNamesTheSameStateAsTheColour(
        Callability callability,
        int? firstByteMs,
        int? totalMs,
        string expected)
    {
        Assert.Equal(expected, ModelCallability.StatusTag(callability, firstByteMs, totalMs));
    }
}
