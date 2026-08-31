namespace AdamCodexHub.Core.Interfaces;

public interface IKeyVault
{
    Task<string> StoreAsync(string providerId, string secret, CancellationToken cancellationToken = default);
    Task<string?> RetrieveAsync(string secretReference, CancellationToken cancellationToken = default);
    Task DeleteAsync(string secretReference, CancellationToken cancellationToken = default);
}
