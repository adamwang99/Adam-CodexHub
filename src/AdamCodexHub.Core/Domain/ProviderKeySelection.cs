namespace AdamCodexHub.Core.Domain;

public sealed record ProviderKeySelection(
    ProviderKeyInfo Key,
    string Secret);
