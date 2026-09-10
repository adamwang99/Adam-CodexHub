namespace AdamCodexHub.Core.Domain;

/// <summary>
/// The text a probe gets back when the *provider* was the problem rather than the model: it refused
/// (5xx/429) or could not be reached at all (DNS, connection, timeout). None of that is a statement
/// about a model, and storing it as a verdict is how working models get marked unusable — on
/// 2026-09-11 a momentary failure to resolve `hhtechapi.com` was recorded as a score-0 verdict for
/// every HHTech model, after which the gateway answered "Model … is not enabled for provider" to
/// every request and the user's session fell apart.
/// </summary>
public static class ProviderTrouble
{
    private static readonly string[] Marks =
    {
        "429",
        "500",
        "502",
        "503",
        "504",
        "rate limited",
        "No such host is known",
        "Timed out",
        "actively refused",
        "could not be reached",
        "connection"
    };

    /// <summary>True when <paramref name="notes"/> describe provider trouble, not the model.</summary>
    public static bool IsNotAMeasurement(string? notes) =>
        !string.IsNullOrWhiteSpace(notes) &&
        Marks.Any(mark => notes.Contains(mark, StringComparison.OrdinalIgnoreCase));
}
