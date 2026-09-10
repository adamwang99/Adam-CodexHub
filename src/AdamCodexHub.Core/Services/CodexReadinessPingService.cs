using AdamCodexHub.Core.Domain;
using AdamCodexHub.Core.Interfaces;

namespace AdamCodexHub.Core.Services;

/// <summary>Result of one readiness tick.</summary>
public sealed record CodexReadinessTick(
    string ProviderId,
    string ProviderName,
    DateTimeOffset CompletedAt,
    IReadOnlyList<CodexReadiness> Verdicts,
    IReadOnlyList<string> ChangedModelIds);

/// <summary>
/// Keeps the Codex catalog honest. Every <see cref="DefaultInterval"/> (5 minutes) it re-runs the
/// Codex-shaped probe for the ACTIVE provider's enabled models that are not ready or whose verdict
/// went stale, stores the new verdict and reports which models changed. A model that starts
/// answering Codex requests is therefore offered in Codex Desktop's picker and in the tray menu
/// within one tick, and a model that starts failing disappears just as fast.
///
/// Safety rules (same as <see cref="ModelAutoPingService"/>): one tick at a time, no probe while
/// the same model is already being tested (<see cref="ModelProbeGate"/>), never faster than the
/// interval, and no exception ever escapes the timer callback.
/// </summary>
public sealed class CodexReadinessPingService : IDisposable
{
    /// <summary>The cadence the user asked for: unqualified models are retried every 5 minutes.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(5);

    /// <summary>Models probed per tick — each check is up to two real Codex requests.</summary>
    public const int DefaultBatchSize = 4;

    /// <summary>
    /// Models probed in the first tick of a provider that has no verdicts at all yet: the catalog
    /// has to become trustworthy right after startup instead of four models per 5 minutes, so the
    /// opening sweep covers the whole enabled list in one pass and later ticks stay incremental.
    /// </summary>
    public const int DefaultSweepBatchSize = 24;

    /// <summary>First tick waits a moment so it never competes with startup work.</summary>
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(20);

    private readonly IProviderManager _providers;
    private readonly IModelStore _models;
    private readonly ICodexReadinessChecker _checker;
    private readonly ICodexReadinessStore _store;
    private readonly SemaphoreSlim _tickGate = new(1, 1);

    private Timer? _timer;
    private volatile bool _paused;
    private bool _disposed;

    public CodexReadinessPingService(
        IProviderManager providers,
        IModelStore models,
        ICodexReadinessChecker checker,
        ICodexReadinessStore store)
    {
        _providers = providers;
        _models = models;
        _checker = checker;
        _store = store;
    }

    public TimeSpan Interval { get; init; } = DefaultInterval;

    public int BatchSize { get; init; } = DefaultBatchSize;

    /// <summary>Batch size used for the opening sweep of a provider with no verdicts yet.</summary>
    public int SweepBatchSize { get; init; } = DefaultSweepBatchSize;

    public bool IsPaused
    {
        get => _paused;
        set => _paused = value;
    }

    public bool IsTickRunning => _tickGate.CurrentCount == 0;

    public DateTimeOffset? LastTickAt { get; private set; }

    public int TickCount { get; private set; }

    /// <summary>Raised after a tick that stored at least one verdict.</summary>
    public event Action<CodexReadinessTick>? TickCompleted;

    /// <summary>Raised for progress/error lines; the host routes them to its log file.</summary>
    public event Action<string, Exception?>? LogMessage;

    public void Start()
    {
        if (_disposed)
        {
            return;
        }

        var delay = Interval < InitialDelay ? Interval : InitialDelay;
        _timer ??= new Timer(OnTimer, null, delay, Interval);
    }

    public void Stop()
    {
        var timer = _timer;
        _timer = null;
        timer?.Dispose();
    }

    private void OnTimer(object? state)
    {
        if (_paused || _disposed)
        {
            return;
        }

        _ = RunTickAsync(CancellationToken.None);
    }

    /// <summary>Runs one tick unless another is running. Never throws.</summary>
    public async Task<int> RunTickAsync(CancellationToken cancellationToken = default)
    {
        if (_paused || _disposed)
        {
            return 0;
        }

        if (!await _tickGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            Log("tick skipped, previous tick still running");
            return 0;
        }

        try
        {
            return await PingOnceAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Log("tick cancelled");
            return 0;
        }
        catch (Exception ex)
        {
            Log("tick failed", ex);
            return 0;
        }
        finally
        {
            _tickGate.Release();
        }
    }

    /// <summary>Probes the not-ready / stale models of the active provider. Returns verdicts stored.</summary>
    public async Task<int> PingOnceAsync(CancellationToken cancellationToken = default)
    {
        var provider = await _providers.GetActiveAsync(cancellationToken).ConfigureAwait(false);
        if (provider is null ||
            !provider.Enabled ||
            string.Equals(provider.Id, "codex-account", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        var models = (await _models.GetAllAsync(provider.Id, cancellationToken).ConfigureAwait(false))
            .Where(m => m.Enabled && m.State == ModelLifecycleState.Enabled)
            .ToArray();
        if (models.Length == 0)
        {
            LastTickAt = DateTimeOffset.UtcNow;
            TickCount++;
            return 0;
        }

        var now = DateTimeOffset.UtcNow;
        var known = (await _store.GetAllAsync(provider.Id, cancellationToken).ConfigureAwait(false))
            .ToDictionary(v => v.ModelId, StringComparer.Ordinal);

        // Never-verified or not-ready/stale models first, oldest verdict first, then alphabetical.
        // A provider with no verdicts at all is swept in one pass so the Codex catalog is correct
        // right after startup.
        var limit = known.Count == 0 ? Math.Max(BatchSize, SweepBatchSize) : Math.Max(1, BatchSize);
        var batch = models
            .Where(m => !ModelProbeGate.IsBusy(provider.Id, m.RemoteId))
            .Where(m => !known.TryGetValue(m.RemoteId, out var v) || !v.Ready || !v.IsFresh(now))
            .OrderBy(m => known.TryGetValue(m.RemoteId, out var v) ? v.CheckedAt : DateTimeOffset.MinValue)
            .ThenBy(m => m.RemoteId, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToArray();

        if (batch.Length == 0)
        {
            LastTickAt = DateTimeOffset.UtcNow;
            TickCount++;
            return 0;
        }

        var verdicts = new List<CodexReadiness>();
        var changed = new List<string>();
        foreach (var model in batch)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_paused)
            {
                break;
            }

            if (ModelProbeGate.IsBusy(provider.Id, model.RemoteId))
            {
                continue;
            }

            try
            {
                var verdict = await _checker
                    .CheckAsync(provider.Id, model.RemoteId, cancellationToken)
                    .ConfigureAwait(false);
                await _store.SaveAsync(verdict, cancellationToken).ConfigureAwait(false);
                verdicts.Add(verdict);

                var previous = known.TryGetValue(model.RemoteId, out var old) ? old.Ready : (bool?)null;
                if (previous != verdict.Ready)
                {
                    changed.Add(model.RemoteId);
                }

                Log(
                    $"Codex check {model.RemoteId}: " +
                    (verdict.Ready ? $"ready in {verdict.LatencyMs ?? 0}ms" : $"not ready — {verdict.Detail}"));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One bad model must not abort the rest of the batch.
                Log($"Codex check {model.RemoteId} failed", ex);
            }
        }

        LastTickAt = DateTimeOffset.UtcNow;
        TickCount++;
        if (verdicts.Count > 0)
        {
            try
            {
                TickCompleted?.Invoke(new CodexReadinessTick(
                    provider.Id,
                    provider.Name,
                    LastTickAt.Value,
                    verdicts,
                    changed));
            }
            catch (Exception ex)
            {
                Log("tick listener failed", ex);
            }
        }

        return verdicts.Count;
    }

    private void Log(string message, Exception? exception = null)
    {
        try
        {
            LogMessage?.Invoke(message, exception);
        }
        catch
        {
            // Logging must never break the refresher.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        _tickGate.Dispose();
    }
}
