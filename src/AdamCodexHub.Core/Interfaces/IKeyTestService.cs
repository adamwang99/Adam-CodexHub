using AdamCodexHub.Core.Domain;

namespace AdamCodexHub.Core.Interfaces;

public interface IKeyTestService
{
    Task<ProviderProbeResult> TestAsync(
        string providerId,
        string keyId,
        CancellationToken cancellationToken = default);
}
