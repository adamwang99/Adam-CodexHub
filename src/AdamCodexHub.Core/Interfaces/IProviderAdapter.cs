using AdamCodexHub.Core.Domain;

namespace AdamCodexHub.Core.Interfaces;

public interface IProviderAdapter
{
    string AdapterId { get; }

    Task<ProviderProbeResult> ProbeAsync(
        ProviderProfile provider,
        string? apiKey,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ModelDescriptor>> ListModelsAsync(
        ProviderProfile provider,
        string? apiKey,
        CancellationToken cancellationToken = default);

    Task<CompatibilityResult> TestModelAsync(
        ProviderProfile provider,
        string modelId,
        string? apiKey,
        CancellationToken cancellationToken = default);
}
