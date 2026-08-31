using AdamCodexHub.Core.Domain;

namespace AdamCodexHub.Core.Interfaces;

public interface IModelStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ModelDescriptor>> GetAllAsync(
        string providerId,
        CancellationToken cancellationToken = default);
    Task<ModelDescriptor?> GetAsync(
        string providerId,
        string modelId,
        CancellationToken cancellationToken = default);
    Task UpsertAsync(ModelDescriptor model, CancellationToken cancellationToken = default);
    Task MarkUnavailableExceptAsync(
        string providerId,
        IReadOnlySet<string> seenModelIds,
        CancellationToken cancellationToken = default);
    Task SetEnabledAsync(
        string providerId,
        string modelId,
        bool enabled,
        CancellationToken cancellationToken = default);
    Task SaveCompatibilityAsync(
        CompatibilityResult result,
        CancellationToken cancellationToken = default);
    Task<CompatibilityResult?> GetLatestCompatibilityAsync(
        string providerId,
        string modelId,
        CancellationToken cancellationToken = default);
}
