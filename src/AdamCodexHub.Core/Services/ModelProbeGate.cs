using System.Collections.Concurrent;

namespace AdamCodexHub.Core.Services;

/// <summary>
/// Process-wide advisory registry of compatibility probes that are currently in flight,
/// keyed by provider + model. Lets the background auto-ping skip a model while a manual
/// "Test" from the UI is already running against it (and vice versa), so the same model is
/// never probed twice at the same time.
/// </summary>
public static class ModelProbeGate
{
    private static readonly ConcurrentDictionary<string, int> InFlight =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True while a probe for this provider + model is running.</summary>
    public static bool IsBusy(string providerId, string modelId) =>
        InFlight.TryGetValue(Key(providerId, modelId), out var count) && count > 0;

    /// <summary>Mark a probe as running. Dispose the returned lease when the probe settles.
    /// Nested/re-entrant probes are counted, so the mark clears only when the last one exits.</summary>
    public static IDisposable Enter(string providerId, string modelId)
    {
        var key = Key(providerId, modelId);
        InFlight.AddOrUpdate(key, 1, static (_, current) => current + 1);
        return new Lease(key);
    }

    private static string Key(string providerId, string modelId) =>
        $"{providerId}\u001F{modelId}";

    private sealed class Lease : IDisposable
    {
        private readonly string _key;
        private int _disposed;

        public Lease(string key) => _key = key;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            if (InFlight.TryGetValue(_key, out var current) && current > 1)
            {
                InFlight.AddOrUpdate(_key, 0, static (_, value) => value - 1);
            }
            else
            {
                InFlight.TryRemove(_key, out _);
            }
        }
    }
}
