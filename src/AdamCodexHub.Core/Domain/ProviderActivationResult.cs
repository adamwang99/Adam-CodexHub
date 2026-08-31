namespace AdamCodexHub.Core.Domain;

public sealed record ProviderActivationResult(
    ProviderProfile Provider,
    ModelDescriptor? Model,
    SessionSwitchPlan? SessionPlan,
    string Message);
