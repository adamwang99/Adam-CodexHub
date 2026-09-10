using System.Collections.Concurrent;
using AdamCodexHub.Core.Domain;
using AdamCodexHub.Core.Interfaces;

namespace AdamCodexHub.Core.Services;

/// <summary>Result of one background auto-ping tick.</summary>
public sealed record ModelAutoPingTick(
    string ProviderId,
    string ProviderName,
    DateTimeOffset CompletedAt,
    IReadOnlyList<CompatibilityResult> Results,
    IReadOnlyList<string> SkippedModelIds);

/// <summary>
/// Bounded background refresher for the ACTIVE provider's models. Every tick it re-runs
/// <see cref="ICompatibilityService.TestAsync"/> for at most <see cref="BatchSize"/> models,
/// oldest/unknown/lowest-score first, so the "callable vs slow vs skip" classification the UI
/// shows is kept current without the user pressing Test.
///
/// Safety rules (all deliberate):
/// <list type="bullet">
/// <item>never two ticks at once (a <see cref="SemaphoreSlim"/> gate, non-blocking),</item>
/// <item>never a model while another probe of that same model is in flight
/// (<see cref="ModelProbeGate"/>),</item>
/// <item>never faster than <see cref="Interval"/> (15 minutes),</item>
/// <item>never throws out of the timer callback — every failure is logged and swallowed.</item>
/// </list>
/// </summary>
public sealed class ModelAutoPingService : IDisposable
{
    /// <summary>Minimum spacing between ticks the user asked for: no faster than every 15 min.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(15);

    /// <summary>Models re-tested per tick — small, so a slow provider does not saturate.</summary>
    public const int DefaultBatchSize = 3;

    /// <summary>
    /// Models re-tested in the opening tick after a launch. The in-memory picture starts empty, so
    /// every model the stored results cannot answer reads "not checked yet" — at three models per
    /// 15 minutes a 14-model provider would sit like that for over an hour. The first tick sweeps
    /// the whole enabled list in one pass instead; later ticks stay incremental. Sized for the
    /// biggest provider in the catalogue (HHTech publishes ~80 ids) so the pass really is full.
    /// </summary>
    public const int DefaultSweepBatchSize = 100;

    /// <summary>First tick starts almost at once, so the real colours and F/N/S tags are there
    /// as soon as the app is up rather than minutes later.</summary>
    public static readonly TimeSpan DefaultStartDelay = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Longest one model may hold its turn in a sweep. The provider adapters wait up to 150 seconds
    /// per request, so a single dead id (HHTech's image endpoints answer a 503 after ~65 s) turns
    /// into a stall that every model behind it waits for — with 80 ids that is the difference
    /// between a sweep that finishes and one that never does. A model that cannot answer inside
    /// this budget is not usable in a Codex turn either.
    /// </summary>
    private static readonly TimeSpan ProbeBudget = TimeSpan.FromSeconds(45);

    /// <summary>How long a model that ran out of budget is left alone. Long enough that a pass stops
    /// re-buying the same 45 s slot every tick, short enough that a provider having a bad half hour
    /// is picked up again on its own.</summary>
    private static readonly TimeSpan TimeoutMemory = TimeSpan.FromHours(1);

    /// <summary>How long a model the provider refused (5xx/429) is left alone. A refusal is usually a
    /// bad moment for the provider rather than a dead model, so it is retried sooner than a timeout.</summary>
    private static readonly TimeSpan RefusalMemory = TimeSpan.FromMinutes(15);

    /// <summary>How long the whole provider is left alone once its key is rate limited or gone. The key
    /// belongs to the user's own Codex session first; measuring models through it is what turns a
    /// working session into "Reconnecting" and provider errors.</summary>
    private static readonly TimeSpan ProviderPause = TimeSpan.FromMinutes(10);

    /// <summary>Consecutive empty results that mean "the provider is busy" rather than "these models
    /// are broken". A throttling provider used to get a whole sweep of score-0 verdicts written
    /// against it, which is how models lose their flag for six hours over a blip.</summary>

    private readonly IProviderManager _providers;
    private readonly IModelStore _models;
    private readonly ICompatibilityService _compatibility;
    private readonly SemaphoreSlim _tickGate = new(1, 1);

    private Timer? _timer;
    private volatile bool _paused;
    private bool _disposed;

    /// <summary>True until the first tick of this run has picked up work: that tick sweeps the
    /// whole enabled list instead of the usual small batch.</summary>
    private bool _openingSweepPending = true;
    // Written from probe paths that can overlap with a manual verification: plain dictionaries lose
    // entries under two writers, and a lost "parked" mark means the same model is asked again.
    private readonly ConcurrentDictionary<string, DateTimeOffset> _timeouts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _refusals = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _providerPauses = new(StringComparer.Ordinal);

    public ModelAutoPingService(
        IProviderManager providers,
        IModelStore models,
        ICompatibilityService compatibility)
    {
        _providers = providers;
        _models = models;
        _compatibility = compatibility;
    }

    /// <summary>Spacing between ticks. Must stay at or above 15 minutes in production; tests
    /// may shorten it.</summary>
    public TimeSpan Interval { get; init; } = DefaultInterval;

    /// <summary>How long a provider is left unprobed after the user's own traffic touched it.</summary>
    public TimeSpan UserQuietWindow { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>How many models one tick may re-test.</summary>
    public int BatchSize { get; init; } = DefaultBatchSize;

    /// <summary>Batch size of the opening sweep (see <see cref="DefaultSweepBatchSize"/>).</summary>
    public int SweepBatchSize { get; init; } = DefaultSweepBatchSize;

    /// <summary>Pause/resume switch: while true no tick starts (in-flight work finishes first).</summary>
    public bool IsPaused
    {
        get => _paused;
        set => _paused = value;
    }

    /// <summary>True while a tick is executing.</summary>
    public bool IsTickRunning => _tickGate.CurrentCount == 0;

    /// <summary>Timestamp of the last completed tick.</summary>
    public DateTimeOffset? LastTickAt { get; private set; }

    /// <summary>How many ticks have completed since start.</summary>
    public int TickCount { get; private set; }

    /// <summary>Raised after every tick that produced at least one refreshed result.</summary>
    public event Action<ModelAutoPingTick>? TickCompleted;

    /// <summary>Raised for progress/error lines. The host routes it to its own log file.</summary>
    public event Action<string, Exception?>? LogMessage;

    /// <summary>Starts the timer. Safe to call more than once.</summary>
    public void Start()
    {
        if (_disposed)
        {
            return;
        }

        var delay = Interval < DefaultStartDelay ? Interval : DefaultStartDelay;
        _timer ??= new Timer(OnTimer, null, delay, Interval);
    }

    /// <summary>Stops the timer (an in-flight tick still finishes).</summary>
    public void Stop()
    {
        var timer = _timer;
        _timer = null;
        timer?.Dispose();
    }

    /// <summary>
    /// Timer callback. Fire-and-forget on purpose, but never lets an exception escape:
    /// <see cref="RunTickAsync"/> catches everything and has no UI thread affinity.
    /// </summary>
    private void OnTimer(object? state)
    {
        if (_paused || _disposed)
        {
            return;
        }

        _ = RunTickAsync(CancellationToken.None);
    }

    /// <summary>Runs one tick unless another is already running. Never throws.</summary>
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

    /// <summary>
    /// Re-tests the batch of models chosen for this tick and returns how many were refreshed.
    /// Only the ACTIVE provider's enabled models are considered.
    /// </summary>
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
            Log($"user traffic in the last {UserQuietWindow.TotalMinutes:0} min — not competing with the session");
            LastTickAt = now;
            TickCount++;
            return 0;
        }

        var candidates = new List<Candidate>();
        var skipped = new List<string>();
        foreach (var model in models)
        {
            if (ModelCallability.IsImageEndpoint(model.RemoteId))
            {
                // Image endpoints: never usable by a Codex turn, and each probe costs the key the user's
                // own session is waiting on.
                continue;
            }

            if (ModelProbeGate.IsBusy(provider.Id, model.RemoteId))
            {
                skipped.Add(model.RemoteId);
                continue;
            }

            if (_timeouts.TryGetValue(ModelKey(provider.Id, model.RemoteId), out var timedOutAt) &&
                now - timedOutAt < TimeoutMemory)
            {
                // Ran out of budget earlier in this run: not a verdict, just a model that will not
                // answer. Without this the sweep re-buys the same 45 s slot on every pass.
                continue;
            }

            if (RefusedRecently(provider.Id, model.RemoteId, now))
            {
                // The provider said no to this model a moment ago; it gets a few minutes before the
                // next ask.
                continue;
            }

            var latest = await _models
                .GetLatestCompatibilityAsync(provider.Id, model.RemoteId, cancellationToken)
                .ConfigureAwait(false);
            var callability = ModelCallability.Classify(latest, now);
            if (callability == Callability.Skip)
            {
                // Already proved it cannot serve a request (the image endpoints HHTech publishes
                // under text-looking ids). Re-testing those every pass only slows the sweep down;
                // the 6 h TTL makes the verdict lapse on its own, so nothing is stuck forever.
                continue;
            }

            candidates.Add(new Candidate(
                model.RemoteId,
                callability,
                latest?.Score,
                latest?.VerifiedAt,
                // Evidence that this model can serve a request at all, even if the verdict has
                // lapsed: that is what decides who is measured first.
                Known: latest is not null &&
                    (latest.Text || latest.Responses || latest.Streaming || latest.Score > 0)));
        }

        // Models with evidence behind them first: the user is waiting on the tag of the models they
        // can actually run, and HHTech publishes a long tail of image endpoints beside them that no
        // Codex turn can use. Measuring the long tail first (never-measured ids sorted by name) is how
        // the useful models stayed tagged "(U)" while the pass spent its turns being refused by ids
        // nobody can select. Then never-measured, then the lowest score, then the oldest verification.
        // The first tick of a run takes the whole list, so the picture is there in one pass.
        var limit = _openingSweepPending ? Math.Max(BatchSize, SweepBatchSize) : Math.Max(1, BatchSize);
        var batch = candidates
            .OrderBy(c => c.Known ? 0 : 1)
            .ThenBy(c => c.Callability == Callability.Unknown ? 0 : 1)
            .ThenBy(c => c.Score ?? int.MinValue)
            .ThenBy(c => c.VerifiedAt ?? DateTimeOffset.MinValue)
            .Take(Math.Max(1, limit))
            .ToArray();
        if (batch.Length > 0)
        {
            _openingSweepPending = false;
        }
        else
        {
            // Finished work and a stuck sweep both look like silence in the log; say which one it is,
            // so "the hub is quiet" is never mistaken for "the hub is wedged".
            Log($"nothing due: all {models.Length} enabled model(s) still have a current verdict");
        }

        var results = new List<CompatibilityResult>();
        foreach (var candidate in batch)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_paused)
            {
                break;
            }

            if (ModelProbeGate.IsBusy(provider.Id, candidate.ModelId))
            {
                skipped.Add(candidate.ModelId);
                continue;
            }

            if (RefusedRecently(provider.Id, candidate.ModelId, now))
            {
                continue;
            }

            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(ProbeBudget);
            try
            {
                var result = await _compatibility
                    .TestAsync(provider.Id, candidate.ModelId, progress: null, budget.Token)
                    .ConfigureAwait(false);

                if (LooksLikeBackPressure(result))
                {
                    // A 5xx/429 is the provider pushing back, not a measurement of this model: it is
                    // not counted as refreshed and it is not stored (CompatibilityService refuses to
                    // save a refusal), so the model keeps whatever verdict it had. Park this model for
                    // a few minutes — but never abandon the pass, because the provider refuses a
                    // handful of ids and answers the next one in the same second, and giving up here
                    // is what left the models the user can run without a tag.
                    _refusals[ModelKey(provider.Id, candidate.ModelId)] = DateTimeOffset.UtcNow;
                    Log(
                        $"model {candidate.ModelId}: provider was busy ({result.Notes}) — parked for " +
                        $"{RefusalMemory.TotalMinutes:0} min, verdict kept");

                    if (KeyLimited(result.Notes))
                    {
                        _providerPauses[provider.Id] = DateTimeOffset.UtcNow;
                        Log($"provider key is rate limited — no probes for {ProviderPause.TotalMinutes:0} min");
                        break;
                    }

                    continue;
                }

                results.Add(result);

                Log(
                    $"refreshed {candidate.ModelId}: score {result.Score}, " +
                    $"first byte {Format(result.FirstByteMs)}, total {Format(result.TotalMs)}");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Our own budget ran out, not a shutdown: remember it so the next pass does not buy
                // the same 45 s slot again, and keep the sweep moving.
                _timeouts[ModelKey(provider.Id, candidate.ModelId)] = DateTimeOffset.UtcNow;
                Log($"model {candidate.ModelId} gave no answer within {ProbeBudget.TotalSeconds:0}s");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (ProviderKeyUnavailableException ex)
            {
                // Nothing to spend: the whole provider goes quiet instead of writing a 401 verdict
                // against models that are perfectly fine.
                _providerPauses[provider.Id] = DateTimeOffset.UtcNow;
                Log($"{ex.Message} — parking the provider for {ProviderPause.TotalMinutes:0} min");
                break;
            }
            catch (Exception ex)
            {
                // One bad model must not abort the rest of the batch.
                Log($"model {candidate.ModelId} could not be refreshed", ex);
            }
        }

        LastTickAt = DateTimeOffset.UtcNow;
        TickCount++;
        if (results.Count > 0)
        {
            try
            {
                TickCompleted?.Invoke(new ModelAutoPingTick(
                    provider.Id,
                    provider.Name,
                    LastTickAt.Value,
                    results,
                    skipped));
            }
            catch (Exception ex)
            {
                Log("tick listener failed", ex);
            }
        }

        return results.Count;
    }

    /// <summary>Key of the in-memory timeout memory: one model on one provider.</summary>
    private static string ModelKey(string providerId, string modelId) => $"{providerId}/{modelId}";

    /// <summary>True while the provider's key is rate limited or gone: nothing is probed then.</summary>
    private bool PausedForKeys(string providerId, DateTimeOffset now) =>
        _providerPauses.TryGetValue(providerId, out var pausedAt) && now - pausedAt < ProviderPause;

    /// <summary>True when the notes name the provider's key (rate limited / out of quota) rather than
    /// the model's own capabilities.</summary>
    private static bool KeyLimited(string? notes)
    {
        var text = notes ?? string.Empty;
        return text.Contains("rate limited", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("no usable api key", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("quota", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True while a model the provider refused is parked. Only the asking pauses: the stored
    /// verdict stands and it is asked again a few minutes later.</summary>
    private bool RefusedRecently(string providerId, string modelId, DateTimeOffset now) =>
        _refusals.TryGetValue(ModelKey(providerId, modelId), out var refusedAt) &&
        now - refusedAt < RefusalMemory;

    /// <summary>True when a probe came back with nothing: either the adapter's notes name a 5xx/429, or
    /// the result carries no capability at all. That is the provider refusing us rather than twenty
    /// models being broken, so the model is parked instead of being written off.</summary>
    private static bool LooksLikeBackPressure(CompatibilityResult result)
    {
        var notes = result.Notes ?? string.Empty;
        if (notes.Contains("429", StringComparison.Ordinal) ||
            notes.Contains("500", StringComparison.Ordinal) ||
            notes.Contains("502", StringComparison.Ordinal) ||
            notes.Contains("503", StringComparison.Ordinal) ||
            notes.Contains("504", StringComparison.Ordinal))
        {
            return true;
        }

        return result.Score == 0 &&
            !result.Text &&
            !result.Responses &&
            !result.Streaming;
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

    private static string Format(int? milliseconds) =>
        milliseconds.HasValue ? $"{milliseconds.Value}ms" : "n/a";

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

    private readonly record struct Candidate(
        string ModelId,
        Callability Callability,
        int? Score,
        DateTimeOffset? VerifiedAt,
        bool Known);
}
