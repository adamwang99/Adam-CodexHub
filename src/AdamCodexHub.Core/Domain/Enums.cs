namespace AdamCodexHub.Core.Domain;

public enum ProviderHealth
{
    Unknown,
    Healthy,
    Warning,
    RateLimited,
    QuotaEmpty,
    Unauthorized,
    Offline,
    Disabled
}

public enum KeyHealth
{
    Unknown,
    Healthy,
    RateLimited,
    Cooldown,
    QuotaEmpty,
    Unauthorized,
    Disabled,
    Offline
}

public enum ModelLifecycleState
{
    Discovered,
    Testing,
    Verified,
    Enabled,
    Disabled,
    Unavailable,
    Deprecated,
    Failed
}

public enum SessionBindingStatus
{
    Active,
    Stale,
    Legacy,
    Unavailable
}

public enum SyncLevel
{
    Light,
    Normal,
    Full
}

public enum ProviderTrustLevel
{
    Official,
    Verified,
    Community,
    Experimental,
    Custom
}
