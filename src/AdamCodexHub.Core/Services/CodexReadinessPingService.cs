using System.Collections.Concurrent;
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
    /// Models probed in the first tick of a run: the catalog has to be trustworthy right after
    /// startup instead of four models per 5 minutes, so the opening sweep covers the whole enabled
    /// list in one pass and later ticks stay incremental. Sized for the biggest provider in the
    /// catalogue (HHTech publishes ~80 ids, most of them not Codex-capable).
    /// </summary>
    public const int DefaultSweepBatchSize = 100;

    /// <summary>First tick waits a moment so it never competes with startup work.</summary>
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Longest one model may hold its turn. The checker waits up to 150 seconds per attempt and
    /// tries twice, so one dead id can block the tick for five minutes — while the Codex picker
    /// only offers models that already have a verdict. A model that cannot answer inside this
    /// budget is not usable in a Codex turn either.
    /// </summary>
    private static readonly TimeSpan ProbeBudget = TimeSpan.FromSeconds(45);

    /// <summary>How long a model that ran out of budget is left alone. Long enough that a pass stops
    /// re-buying the same 45 s slot every tick, short enough that a provider having a bad half hour
    /// is picked up again on its own.</summary>
    private static readonly TimeSpan TimeoutMemory = TimeSpan.FromHours(1);

    /// <summary>
    /// How long a model that the provider refused (5xx/429) is left alone. Shorter than
    /// <see cref="TimeoutMemory"/> on purpose: a refusal is usually the provider having a bad moment
    /// rather than the model being dead, so three ticks later it is worth asking again.
    /// </summary>
    private static readonly TimeSpan RefusalMemory = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How long the whole provider is left alone after its key comes back "rate limited" / "no usable
    /// API key". Parking one model is not enough there: every probe spends the same key, and the user's
    /// own Codex session needs it. Probing through a rate-limited key is how a working session starts
    /// showing "Reconnecting" and provider errors.
    /// </summary>
    private static readonly TimeSpan ProviderPause = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How many models may be probed at once. One at a time made a pass the sum of every probe,
    /// so a model waiting on its 45 s budget held every model behind it; two at a time halves
    /// that without changing the number of requests. Deliberately small: every probe spends the
    /// same provider key (HHTech answers 429/503 when a key is crowded), each answer is still
    /// stored one at a time, and a refusal still ends the pass.
    /// </summary>
    private const int ProbeConcurrency = 2;

    /// <summary>
    /// How long a provider is left unprobed after the user's own Codex traffic touched it. Every probe
    /// spends the key the session is waiting on, and HHTech answers 429/503 to a crowded key — probing
    /// under an active session is what turned a working chat into "Reconnecting" (2026-09-11).
    /// </summary>
    public TimeSpan UserQuietWindow { get; init; } = TimeSpan.FromMinutes(3);

    private readonly IProviderManager _providers;
    private readonly IModelStore _models;
    private readonly ICodexReadinessChecker _checker;
    private readonly ICodexReadinessStore _store;
    private readonly SemaphoreSlim _tickGate = new(1, 1);

    private Timer? _timer;
    private volatile bool _paused;
    private bool _openingSweepPending = true;
    // Two probes run at once, so these have to tolerate two writers: a plain Dictionary silently loses
    // one of the two entries, and a model whose "parked" mark is lost gets asked again on the next pass
    // (a test caught exactly that). These are the same fixes the catalogue sweep already carries.
    private readonly ConcurrentDictionary<string, DateTimeOffset> _timeouts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _refusals = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _providerPauses = new(StringComparer.Ordinal);
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
        if (PausedForKeys(provider.Id, now))
        {
            // The key is rate limited: the user's own session needs it more than this pass does.
            Log($"provider key is rate limited — no probes for {ProviderPause.TotalMinutes:0} min");
            LastTickAt = now;
            TickCount++;
            return 0;
        }

        if (ProviderBackoff.IsParked(provider.Id))
        {
            // The provider answered 429: it asked for less traffic, so the probes stand down.
            Log("provider asked for less traffic — probes parked");
            LastTickAt = now;
            TickCount++;
            return 0;
        }

        if (UserTrafficClock.IsBusy(provider.Id, UserQuietWindow))
        {
            // The user is in a session on this provider: their turn matters more than a fresh verdict.
            Log($"user traffic in the last {UserQuietWindow.TotalMinutes:0} min — not competing with the session");
            LastTickAt = now;
            TickCount++;
            return 0;
        }

        var known = (await _store.GetAllAsync(provider.Id, cancellationToken).ConfigureAwait(false))
            .ToDictionary(v => v.ModelId, StringComparer.Ordinal);

        // One pass over the stored compatibility results: it tells us which models a Codex session can
        // actually use (they answered a text request) and which already proved they cannot. The
        // callable ones go first — an image endpoint that eats its own 45 s slot must not push every
        // real model behind it — and the proven-hopeless ones are not probed at all.
        var callabilities = new Dictionary<string, Callability>(StringComparer.Ordinal);
        foreach (var model in models)
        {
            var latest = await _models
                .GetLatestCompatibilityAsync(provider.Id, model.RemoteId, cancellationToken)
                .ConfigureAwait(false);
            callabilities[model.RemoteId] = ModelCallability.Classify(latest, now);
        }

        // The first tick of this run sweeps every model that is not fresh yet: the Codex picker only
        // offers models with a current verdict, so a catalog that went stale (or never existed) has to
        // be rebuilt in one pass right after startup instead of four models per five minutes.
        var limit = _openingSweepPending ? Math.Max(BatchSize, SweepBatchSize) : Math.Max(1, BatchSize);

        // Refresh what Codex can actually use first. A model whose ready verdict merely expired is one
        // the picker was offering an hour ago; the image endpoints HHTech publishes under text-looking
        // ids never were. Both used to tie in this ordering, and the tie broke on the oldest check —
        // so the pass spent its turns on models nobody can use, the provider refused them, the pass
        // gave up, and the picker came up as a wall of "(U)" while the real models stayed unverified.
        static int UsableFirst(bool wasReady, Callability callability) =>
            wasReady || callability is Callability.Callable or Callability.Slow ? 0 : 1;

        var batch = models
            .Where(m => !ModelProbeGate.IsBusy(provider.Id, m.RemoteId))
            .Where(m => !ModelCallability.IsImageEndpoint(m.RemoteId))
            .Where(m => !RefusedRecently(provider.Id, m.RemoteId, now))
            .Where(m => !known.TryGetValue(m.RemoteId, out var v) || !v.Ready || !v.IsFresh(now))
            .OrderBy(m => UsableFirst(
                known.TryGetValue(m.RemoteId, out var stored) && stored.Ready,
                callabilities.TryGetValue(m.RemoteId, out var callability) ? callability : Callability.Unknown))
            .ThenBy(m => known.TryGetValue(m.RemoteId, out var v) ? v.CheckedAt : DateTimeOffset.MinValue)
            .ThenBy(m => m.RemoteId, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToArray();

        if (batch.Length == 0)
        {
            // Finished work and a stuck sweep both look like silence in the log; say which one it is,
            // so "the Codex picker is already full" is never mistaken for "the probe is wedged".
            Log($"nothing due: every enabled model already has a current verdict ({models.Length} checked)");
            LastTickAt = DateTimeOffset.UtcNow;
            TickCount++;
            return 0;
        }

        _openingSweepPending = false;

        var verdicts = new List<CodexReadiness>();
        var changed = new List<string>();
        var stopped = 0;
        var listGate = new object();
        var saveGate = new SemaphoreSlim(1, 1);
        var probes = new SemaphoreSlim(ProbeConcurrency, ProbeConcurrency);

        // ProbeConcurrency models in flight instead of one: the Codex picker only offers models that
        // already have a verdict, so this pass is exactly what the user waits for, and a single slow
        // model used to hold every model behind it. The cap stays small and every answer is stored
        // through saveGate, so neither the store nor the provider key sees more pressure than a
        // sequential pass put on them.
        try
        {
            await Parallel.ForEachAsync(
                batch,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = ProbeConcurrency,
                    CancellationToken = cancellationToken
                },
                async (model, passToken) =>
                {
                    if (_paused || Volatile.Read(ref stopped) != 0)
                    {
                        return;
                    }

                    if (ModelProbeGate.IsBusy(provider.Id, model.RemoteId))
                    {
                        return;
                    }

                    if (_timeouts.TryGetValue(ModelKey(provider.Id, model.RemoteId), out var timedOutAt) &&
                        now - timedOutAt < TimeoutMemory)
                    {
                        return;
                    }

                    if (RefusedRecently(provider.Id, model.RemoteId, now))
                    {
                        // The provider said no a moment ago: park this model, do not park the pass.
                        return;
                    }

                    if (callabilities.TryGetValue(model.RemoteId, out var storedCallability) &&
                        storedCallability == Callability.Skip)
                    {
                        // Already proved it cannot serve a request — the image endpoints HHTech
                        // publishes under text-looking ids. A Codex check would fail the same way, and
                        // each one costs a real request; the 6 h TTL lapses the verdict by itself.
                        return;
                    }

                    await probes.WaitAsync(passToken).ConfigureAwait(false);
                    try
                    {
                        using var budget = CancellationTokenSource.CreateLinkedTokenSource(passToken);
                        budget.CancelAfter(ProbeBudget);
                        try
                        {
                            var verdict = await _checker
                                .CheckAsync(provider.Id, model.RemoteId, budget.Token)
                                .ConfigureAwait(false);

                            if (!verdict.Ready && IsBackPressure(verdict))
                            {
                                // A 5xx/429 is the provider pushing back, not a verdict about this
                                // model: the stored verdict stands and this model is parked for a few
                                // minutes instead of being asked again on the next tick. The pass
                                // itself keeps going — HHTech refuses one batch of ids and answers
                                // the next one in the same second, and abandoning the pass left the
                                // models the user can actually run unverified, which is what a picker
                                // reading as a wall of "(U)" looks like from the outside.
                                _refusals[ModelKey(provider.Id, model.RemoteId)] = DateTimeOffset.UtcNow;
                                Log(
                                    $"Codex check {model.RemoteId}: provider was busy ({verdict.Detail}), verdict kept");

                                if (KeyLimited(verdict.Detail))
                                {
                                    // Not this model: the key itself. Every further probe would spend
                                    // the key the user's own Codex session is waiting on, so the whole
                                    // provider goes quiet for a while and this pass ends here.
                                    _providerPauses[provider.Id] = DateTimeOffset.UtcNow;
                                    Interlocked.Exchange(ref stopped, 1);
                                    Log(
                                        $"provider key is rate limited — no probes for {ProviderPause.TotalMinutes:0} min");
                                }

                                return;
                            }

                            await saveGate.WaitAsync(passToken).ConfigureAwait(false);
                            try
                            {
                                await _store.SaveAsync(verdict, passToken).ConfigureAwait(false);
                            }
                            finally
                            {
                                saveGate.Release();
                            }

                            lock (listGate)
                            {
                                verdicts.Add(verdict);
                                var previous = known.TryGetValue(model.RemoteId, out var old)
                                    ? old.Ready
                                    : (bool?)null;
                                if (previous != verdict.Ready)
                                {
                                    changed.Add(model.RemoteId);
                                }
                            }

                            Log(
                                $"Codex check {model.RemoteId}: " +
                                (verdict.Ready
                                    ? $"ready in {verdict.LatencyMs ?? 0}ms"
                                    : $"not ready — {verdict.Detail}"));
                        }
                        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                        {
                            // Our own budget ran out, not a shutdown: remember it, so the next pass
                            // does not buy the same 45 s slot again, and keep the sweep moving.
                            _timeouts[ModelKey(provider.Id, model.RemoteId)] = DateTimeOffset.UtcNow;
                            Log($"Codex check {model.RemoteId} gave no answer within {ProbeBudget.TotalSeconds:0}s");
                        }
                        catch (Exception ex)
                        {
                            // One bad model must not abort the rest of the batch.
                            Log($"Codex check {model.RemoteId} failed", ex);
                        }
                    }
                    finally
                    {
                        probes.Release();
                    }
                })
                .ConfigureAwait(false);
        }
        finally
        {
            saveGate.Dispose();
            probes.Dispose();
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

    /// <summary>Key of the in-memory timeout memory: one model on one provider.</summary>
    private static string ModelKey(string providerId, string modelId) => $"{providerId}/{modelId}";

    /// <summary>True while a model the provider refused (5xx/429) is parked. The verdict already
    /// stored stands — only the asking pauses, and only for a few minutes.</summary>
    private bool RefusedRecently(string providerId, string modelId, DateTimeOffset now) =>
        _refusals.TryGetValue(ModelKey(providerId, modelId), out var refusedAt) &&
        now - refusedAt < RefusalMemory;

    /// <summary>True while the provider's key is rate limited. Nothing is probed then: the key belongs
    /// to the user's own Codex session first.</summary>
    private bool PausedForKeys(string providerId, DateTimeOffset now) =>
        _providerPauses.TryGetValue(providerId, out var pausedAt) && now - pausedAt < ProviderPause;

    /// <summary>True when the failure is the provider's key (rate limited / out of quota) rather than
    /// the model: the message is the gateway's or the provider's own wording.</summary>
    private static bool KeyLimited(string? detail)
    {
        var text = detail ?? string.Empty;
        return text.Contains("rate limited", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("no usable api key", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("quota", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when the checker's detail reads like the provider refusing to serve us right
    /// now (a 5xx or a 429) rather than a verdict about the model itself.</summary>
    private static bool IsBackPressure(CodexReadiness verdict)
    {
        var detail = verdict.Detail ?? string.Empty;
        return detail.Contains("429", StringComparison.Ordinal) ||
            detail.Contains("500", StringComparison.Ordinal) ||
            detail.Contains("502", StringComparison.Ordinal) ||
            detail.Contains("503", StringComparison.Ordinal) ||
            detail.Contains("504", StringComparison.Ordinal);
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
