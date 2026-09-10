using System.Collections.Concurrent;
using AdamCodexHub.Core.Interfaces;

namespace AdamCodexHub.Core.Services;

/// <summary>
/// Background catalogue refresher. Every enabled provider's model list is re-read on a slow timer
/// and once shortly after launch, so a model the provider just added — DeepSeek's V4.1 Flash, say —
/// turns up without anybody pressing "Scan models", and a provider that comes back from a quota
/// outage stops looking empty.
///
/// Deliberate rules:
/// <list type="bullet">
/// <item>startup stays fast: the first sweep waits <see cref="DefaultStartDelay"/>, runs on a
/// background thread, one provider at a time, and never blocks the window coming up,</item>
/// <item>never two sweeps at once (non-blocking gate), and never a provider whose catalogue was
/// already read inside <see cref="FreshnessWindow"/>,</item>
/// <item>the ACTIVE provider goes first — that is the one the user is about to open Codex with,</item>
/// <item>a provider that fails is logged and skipped; a scan failure is never fatal and never
/// stops the rest of the sweep,</item>
/// <item>a provider that FAILED is retried on a doubling backoff (15 min, 30, 1 h … capped at
/// <see cref="FreshnessWindow"/>) instead of waiting for the next <see cref="FreshnessWindow"/>,
/// and the retry first re-probes its keys — so a provider that ran out of quota and has just come
/// back stops looking broken without anybody pressing anything,</item>
/// <item>a provider whose catalogue was re-read is announced (<see cref="ProviderRefreshed"/>) so
/// anything on screen rebuilds itself instead of showing a provider that has already come back.</item>
/// </list>
/// </summary>
public sealed class ModelCatalogRefreshService : IModelCatalogRefreshService, IDisposable
{
    /// <inheritdoc />
    public event EventHandler<string>? ProviderRefreshed;

    /// <summary>How often the sweep looks for work. Short on purpose: this is only a "is anything
    /// due?" tick, and a tick that finds nothing costs no request — a provider inside its freshness
    /// window (6 h) or its retry window is skipped, so a catalogue still changes on the provider's
    /// schedule while a provider that comes back from an outage is noticed within minutes.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(15);

    /// <summary>First sweep starts a little after launch, so opening the app stays instant.</summary>
    public static readonly TimeSpan DefaultStartDelay = TimeSpan.FromSeconds(20);

    /// <summary>A provider whose catalogue was read inside this window is left alone.</summary>
    public static readonly TimeSpan DefaultFreshnessWindow = TimeSpan.FromHours(6);

    /// <summary>Retry spacing after a failed scan. Short on purpose: a provider can come back from a
    /// quota outage at any minute, and nobody wants to press "Test keys" to find out.</summary>
    public static readonly TimeSpan DefaultFailedRetryDelay = TimeSpan.FromMinutes(15);

    private const string CodexAccountProviderId = "codex-account";

    /// <summary>
    /// How many providers may be read at once. Each provider has its own key and its own rate limit,
    /// so a sweep of 17 catalogues does not have to be one request at a time — the models of a single
    /// provider are still read one after another, because that key is shared.
    /// </summary>
    private const int SweepConcurrency = 3;

    private readonly IProviderManager _providers;
    private readonly IModelStore _models;
    private readonly IModelDiscoveryService _discovery;
    private readonly IProviderRecoveryService? _recovery;
    private readonly SemaphoreSlim _tickGate = new(1, 1);

    /// <summary>Earliest time a provider that failed its last scan may be tried again.</summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _retryAfter = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Consecutive failed scans per provider, driving the retry backoff.</summary>
    private readonly ConcurrentDictionary<string, int> _failures = new(StringComparer.OrdinalIgnoreCase);

    private Timer? _timer;
    private bool _disposed;

    public ModelCatalogRefreshService(
        IProviderManager providers,
        IModelStore models,
        IModelDiscoveryService discovery,
        IProviderRecoveryService? recovery = null)
    {
        _providers = providers;
        _models = models;
        _discovery = discovery;
        _recovery = recovery;
    }

    /// <summary>Spacing between sweeps. Tests may shorten it.</summary>
    public TimeSpan Interval { get; init; } = DefaultInterval;

    /// <summary>How stale a catalogue may be before a sweep re-reads it.</summary>
    public TimeSpan FreshnessWindow { get; init; } = DefaultFreshnessWindow;

    /// <summary>How soon a provider whose scan failed is tried again. A provider that was down for
    /// quota reasons comes back on its own schedule, so waiting the full <see cref="Interval"/>
    /// would leave the user staring at an unusable provider for hours. Tests may shorten it.</summary>
    public TimeSpan FailedRetryDelay { get; init; } = DefaultFailedRetryDelay;

    /// <summary>When the last sweep finished, or null when none has run yet.</summary>
    public DateTimeOffset? LastSweepAt { get; private set; }

    /// <summary>How many catalogues the last sweep refreshed.</summary>
    public int LastSweepCount { get; private set; }

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

    /// <summary>Stops the timer (an in-flight sweep still finishes).</summary>
    public void Stop()
    {
        var timer = _timer;
        _timer = null;
        timer?.Dispose();
    }

    public void Dispose()
    {
        _disposed = true;
        Stop();
        _tickGate.Dispose();
    }

    private void OnTimer(object? state)
    {
        if (_disposed)
        {
            return;
        }

        _ = RunTickAsync(CancellationToken.None);
    }

    /// <summary>Runs one sweep unless another is already running. Never throws.</summary>
    public async Task<int> RunTickAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return 0;
        }

        if (!await _tickGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            Log("sweep skipped, previous sweep still running", null);
            return 0;
        }

        try
        {
            return await RefreshOnceAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Log("sweep cancelled", null);
            return 0;
        }
        catch (Exception exception)
        {
            Log("sweep failed", exception);
            return 0;
        }
        finally
        {
            _tickGate.Release();
        }
    }

    /// <summary>
    /// Re-reads the catalogue of every enabled provider whose copy is stale, active provider first.
    /// Returns how many catalogues were refreshed.
    /// </summary>
    public async Task<int> RefreshOnceAsync(CancellationToken cancellationToken = default)
    {
        var providers = await _providers.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var active = await _providers.GetActiveAsync(cancellationToken).ConfigureAwait(false);
        var ordered = providers
            .Where(provider =>
                provider.Enabled &&
                !string.Equals(provider.Id, CodexAccountProviderId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(provider =>
                string.Equals(provider.Id, active?.Id, StringComparison.OrdinalIgnoreCase))
            .ThenBy(provider => provider.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var refreshed = 0;
        // SweepConcurrency catalogues at a time: providers do not share a key, so reading three of
        // them together costs nothing and takes a full sweep from ~2 minutes to well under one.
        await Parallel.ForEachAsync(
            ordered,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = SweepConcurrency,
                CancellationToken = cancellationToken
            },
            async (provider, sweepToken) =>
            {
                if (await IsFreshAsync(provider.Id, sweepToken).ConfigureAwait(false))
                {
                    return;
                }

                if (_retryAfter.TryGetValue(provider.Id, out var retryAt) && DateTimeOffset.UtcNow < retryAt)
                {
                    // Failed a moment ago; give the provider time to come back before asking again.
                    return;
                }

                if (await ScanAsync(provider.Id, sweepToken).ConfigureAwait(false))
                {
                    Interlocked.Increment(ref refreshed);
                }
            })
            .ConfigureAwait(false);

        LastSweepAt = DateTimeOffset.UtcNow;
        LastSweepCount = refreshed;
        Log($"sweep done: {refreshed} of {ordered.Length} enabled catalogue(s) refreshed", null);
        return refreshed;
    }

    /// <summary>True when the provider's stored catalogue was read inside <see cref="FreshnessWindow"/>.
    /// A provider with nothing in the catalogue is never fresh — it has not been read yet.</summary>
    public async Task<bool> IsFreshAsync(
        string providerId,
        CancellationToken cancellationToken = default)
    {
        var newest = (await _models.GetAllAsync(providerId, cancellationToken).ConfigureAwait(false))
            .Where(model => model.LastSeenAt.HasValue)
            .Select(model => model.LastSeenAt!.Value)
            .DefaultIfEmpty(DateTimeOffset.MinValue)
            .Max();
        return newest != DateTimeOffset.MinValue &&
               DateTimeOffset.UtcNow - newest < FreshnessWindow;
    }

    public Task<bool> RefreshProviderAsync(
        string providerId,
        CancellationToken cancellationToken = default) =>
        ScanAsync(providerId, cancellationToken);

    private async Task<bool> ScanAsync(string providerId, CancellationToken cancellationToken)
    {
        try
        {
            var models = await _discovery.ScanAsync(providerId, cancellationToken).ConfigureAwait(false);
            _retryAfter.TryRemove(providerId, out _);
            _failures.TryRemove(providerId, out _);
            Log($"{providerId}: {models.Count} model(s) in the catalogue", null);
            Announce(providerId);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // A provider that is down must not stop the sweep, and must not break the caller: the
            // stored catalogue simply stays as it was until the next attempt. Ask again soon (15 min
            // doubling up to the freshness window): a quota outage ends on the provider's clock.
            var failures = (_failures.TryGetValue(providerId, out var previous) ? previous : 0) + 1;
            _failures[providerId] = failures;
            var retryDelay = RetryDelayFor(failures);
            _retryAfter[providerId] = DateTimeOffset.UtcNow + retryDelay;
            Log($"{providerId}: catalogue refresh failed", exception);

            // The usual reason a scan fails is that the provider ran out of quota and parked the
            // key, which would leave the provider unusable until somebody pressed "Test keys".
            // Probe it once here (bounded and rate-limited by the recovery service): a key that
            // recovered brings its catalogue back in this same tick.
            if (_recovery is not null)
            {
                try
                {
                    if (await _recovery
                            .TryRecoverAsync(providerId, cancellationToken: cancellationToken)
                            .ConfigureAwait(false))
                    {
                        _retryAfter.TryRemove(providerId, out _);
                        Log($"{providerId}: a usable key answered after the failed scan", null);
                        Announce(providerId);
                        return true;
                    }

                    Log($"{providerId}: key still rejected, next attempt after {retryDelay}", null);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception recoveryException)
                {
                    Log($"{providerId}: recovery attempt failed", recoveryException);
                }
            }

            return false;
        }
    }

    private void Log(string message, Exception? exception) => LogMessage?.Invoke(message, exception);

    /// <summary>How long to wait before asking a provider that has failed <paramref name="failures"/>
    /// times in a row. Doubles each time and stops at the freshness window, so a provider that is
    /// simply down costs a request every 6 hours instead of one every 15 minutes.</summary>
    public static TimeSpan BackoffFor(int failures, TimeSpan baseDelay, TimeSpan cap)
    {
        var delay = baseDelay;
        for (var attempt = 1; attempt < failures && delay < cap; attempt++)
        {
            delay += delay;
        }

        return delay > cap ? cap : delay;
    }

    private TimeSpan RetryDelayFor(int failures) => BackoffFor(failures, FailedRetryDelay, FreshnessWindow);

    /// <summary>
    /// Tells whoever is listening that this provider's stored state changed. A handler that throws
    /// must not break the sweep, so it is logged and the sweep moves on.
    /// </summary>
    private void Announce(string providerId)
    {
        try
        {
            ProviderRefreshed?.Invoke(this, providerId);
        }
        catch (Exception exception)
        {
            Log($"{providerId}: a catalogue-refreshed handler failed", exception);
        }
    }
}
