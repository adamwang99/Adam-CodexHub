using System.Collections.Concurrent;

namespace AdamCodexHub.Core.Domain;

/// <summary>
/// How long the background passes must leave a provider alone.
///
/// When a provider answers 429 ("rate limited") the key is not broken — the provider is asking for less
/// traffic. The hub now hands that 429 straight back to Codex, which backs off and retries, and parks
/// only the probes here: a probe is the request nobody is waiting for, and probing through a provider
/// that just asked for less traffic is what turned a live session into "Reconnecting …" (2026-09-11).
/// </summary>
public static class ProviderBackoff
{
    private static readonly ConcurrentDictionary<string, DateTimeOffset> ParkedUntil = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Leaves the provider's probes alone until <paramref name="window"/> has passed.</summary>
    public static void Park(string providerId, TimeSpan window) =>
        ParkedUntil[providerId] = DateTimeOffset.UtcNow + window;

    /// <summary>True while the provider's probes are parked.</summary>
    public static bool IsParked(string providerId) =>
        ParkedUntil.TryGetValue(providerId, out var until) && until > DateTimeOffset.UtcNow;

    /// <summary>Forgets everything. Tests only.</summary>
    public static void Reset() => ParkedUntil.Clear();
}
