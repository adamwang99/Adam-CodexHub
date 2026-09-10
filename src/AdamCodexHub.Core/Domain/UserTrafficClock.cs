using System.Collections.Concurrent;

namespace AdamCodexHub.Core.Domain;

/// <summary>
/// When the hub last served the user's own traffic to a provider.
///
/// The background passes (catalogue sweep, model auto-ping, Codex readiness) measure models with the
/// same key the user's Codex session runs on. HHTech answers 429/503 when a key is crowded, so probing
/// while the user is typing is how a working session turned into "Reconnecting … No usable API key
/// remains" (2026-09-11). A pass now leaves a provider alone for a few minutes after any real traffic
/// to it — a probe measuring a model nobody is asking for is worth less than the session.
///
/// Process-wide on purpose: the gateway and the ping passes are hosted in one process, and this is
/// traffic accounting, not state anyone can hold a reference to.
/// </summary>
public static class UserTrafficClock
{
    private static readonly ConcurrentDictionary<string, DateTimeOffset> Last = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Records that a request for the user (not a probe) was just sent to the provider.</summary>
    public static void Record(string providerId) => Last[providerId] = DateTimeOffset.UtcNow;

    /// <summary>True while the provider has had user traffic inside <paramref name="window"/>.</summary>
    public static bool IsBusy(string providerId, TimeSpan window) =>
        Last.TryGetValue(providerId, out var at) && DateTimeOffset.UtcNow - at < window;

    /// <summary>Forgets everything. Tests only.</summary>
    public static void Reset() => Last.Clear();
}
