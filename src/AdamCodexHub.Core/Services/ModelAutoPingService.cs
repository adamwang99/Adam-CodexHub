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

    /// <summary>First tick waits a little so it never competes with startup work.</summary>
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(90);

    private readonly IProviderManager _providers;
    private readonly IModelStore _models;
    private readonly ICompatibilityService _compatibility;
    private readonly SemaphoreSlim _tickGate = new(1, 1);

    private Timer? _timer;
    private volatile bool _paused;
    private bool _disposed;

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

    /// <summary>How many models one tick may re-test.</summary>
    public int BatchSize { get; init; } = DefaultBatchSize;

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

        var delay = Interval < InitialDelay ? Interval : InitialDelay;
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
        var candidates = new List<Candidate>();
        var skipped = new List<string>();
        foreach (var model in models)
        {
            if (ModelProbeGate.IsBusy(provider.Id, model.RemoteId))
            {
                skipped.Add(model.RemoteId);
                continue;
            }

            var latest = await _models
                .GetLatestCompatibilityAsync(provider.Id, model.RemoteId, cancellationToken)
                .ConfigureAwait(false);
            var callability = ModelCallability.Classify(latest, now);
            candidates.Add(new Candidate(
                model.RemoteId,
                callability,
                latest?.Score,
                latest?.VerifiedAt));
        }

        // Stale/unknown first, then the lowest score, then the oldest verification.
        var batch = candidates
            .OrderBy(c => c.Callability == Callability.Unknown ? 0 : 1)
            .ThenBy(c => c.Score ?? int.MinValue)
            .ThenBy(c => c.VerifiedAt ?? DateTimeOffset.MinValue)
            .Take(Math.Max(1, BatchSize))
            .ToArray();

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

            try
            {
                var result = await _compatibility
                    .TestAsync(provider.Id, candidate.ModelId, progress: null, cancellationToken)
                    .ConfigureAwait(false);
                results.Add(result);
                Log(
                    $"refreshed {candidate.ModelId}: score {result.Score}, " +
                    $"first byte {Format(result.FirstByteMs)}, total {Format(result.TotalMs)}");
            }
            catch (OperationCanceledException)
            {
                throw;
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
        DateTimeOffset? VerifiedAt);
}
