namespace AdamCodexHub.Core.Domain;

/// <summary>
/// How usable a model is right now, derived from its latest compatibility result
/// (capabilities + measured latency). Drives the yellow/red badges in the UI and the
/// "callable vs skip" grouping the user asked for.
/// </summary>
public enum Callability
{
    /// <summary>Never probed, failed to probe, or the stored result is older than the TTL.</summary>
    Unknown,

    /// <summary>Text + responses + streaming all work and latency is under the slow threshold.</summary>
    Callable,

    /// <summary>Everything works, but the first byte or the whole request takes longer than
    /// <see cref="ModelCallability.SlowThresholdMs"/> (a working-but-slow model).</summary>
    Slow,

    /// <summary>A required capability failed — do not offer this model to the user.</summary>
    Skip
}

/// <summary>
/// Pure, side-effect-free classification of a <see cref="CompatibilityResult"/> into a
/// <see cref="Callability"/>. Kept in Core (no IO, no clock inside) so it is trivially
/// unit-testable: the caller passes the "now" timestamp and the TTL.
/// </summary>
public static class ModelCallability
{
    /// <summary>How long a stored compatibility result stays trustworthy. A few hours: long
    /// enough that a background refresh is rarely needed, short enough that a result from
    /// yesterday is not presented as current truth.</summary>
    public static readonly TimeSpan VerificationTtl = TimeSpan.FromHours(6);

    /// <summary>A model slower than this on the first byte or on the whole non-stream request is
    /// classified <see cref="Callability.Slow"/> instead of <see cref="Callability.Callable"/>.
    /// Models that answer in ~4s stay green; the 60-90s Claude models on the HHTech gateway
    /// turn amber instead of being dropped.</summary>
    public const int SlowThresholdMs = 15_000;

    /// <summary>
    /// Classify a stored result.
    /// <list type="bullet">
    /// <item>no result, or older than <paramref name="ttl"/> => <see cref="Callability.Unknown"/></item>
    /// <item>text / responses / streaming not all supported => <see cref="Callability.Skip"/></item>
    /// <item>first byte or total latency above <see cref="SlowThresholdMs"/> => <see cref="Callability.Slow"/></item>
    /// <item>otherwise => <see cref="Callability.Callable"/></item>
    /// </list>
    /// </summary>
    public static Callability Classify(CompatibilityResult? result, DateTimeOffset verifiedAt, TimeSpan ttl)
    {
        if (result is null || result.VerifiedAt == default)
        {
            return Callability.Unknown;
        }

        // A negative age (result written "in the future" by a clock skew) is not stale.
        if (verifiedAt - result.VerifiedAt > ttl)
        {
            return Callability.Unknown;
        }

        if (!result.Text || !result.Responses || !result.Streaming)
        {
            return Callability.Skip;
        }

        return IsSlow(result) ? Callability.Slow : Callability.Callable;
    }

    /// <summary>Convenience overload using <see cref="VerificationTtl"/>.</summary>
    public static Callability Classify(CompatibilityResult? result, DateTimeOffset verifiedAt) =>
        Classify(result, verifiedAt, VerificationTtl);

    /// <summary>True when the result carries a latency sample above the slow threshold.</summary>
    public static bool IsSlow(CompatibilityResult result) =>
        (result.FirstByteMs ?? 0) > SlowThresholdMs || (result.TotalMs ?? 0) > SlowThresholdMs;

    /// <summary>Sort key used to keep the model lists grouped "callable, slow, unknown, skip".</summary>
    public static int SortOrder(Callability callability) => callability switch
    {
        Callability.Callable => 0,
        Callability.Slow => 1,
        Callability.Unknown => 2,
        _ => 3
    };
}
