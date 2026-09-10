namespace AdamCodexHub.Core.Domain;

/// <summary>
/// Raised when the provider has no usable key at this moment — every key is disabled, parked by an
/// outage, or sitting out a rate-limit cooldown.
///
/// This is deliberately its own type: probing without a key would answer 401 and store a verdict
/// that says the *model* is unusable, which hides a perfectly good model for the whole six-hour
/// verdict window. Callers leave the provider alone instead.
/// </summary>
public sealed class ProviderKeyUnavailableException : Exception
{
    public ProviderKeyUnavailableException(string message)
        : base(message)
    {
    }
}
