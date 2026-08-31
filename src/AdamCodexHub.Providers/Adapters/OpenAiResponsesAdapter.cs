using AdamCodexHub.Core.Domain;
using AdamCodexHub.Core.Interfaces;

namespace AdamCodexHub.Providers.Adapters;

public sealed class OpenAiResponsesAdapter : IProviderAdapter
{
    private readonly OpenAiCompatibleAdapter _fallback;

    public OpenAiResponsesAdapter(OpenAiCompatibleAdapter fallback)
    {
        _fallback = fallback;
    }

    public string AdapterId => "openai-responses";

    public Task<ProviderProbeResult> ProbeAsync(
        ProviderProfile provider,
        string? apiKey,
        CancellationToken cancellationToken = default) =>
        _fallback.ProbeAsync(provider, apiKey, cancellationToken);

    public Task<IReadOnlyList<ModelDescriptor>> ListModelsAsync(
        ProviderProfile provider,
        string? apiKey,
        CancellationToken cancellationToken = default) =>
        _fallback.ListModelsAsync(provider, apiKey, cancellationToken);

    public Task<CompatibilityResult> TestModelAsync(
        ProviderProfile provider,
        string modelId,
        string? apiKey,
        CancellationToken cancellationToken = default) =>
        _fallback.TestModelAsync(provider, modelId, apiKey, cancellationToken);
}
