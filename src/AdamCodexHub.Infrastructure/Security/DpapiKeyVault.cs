using System.Security.Cryptography;
using System.Text;
using AdamCodexHub.Core.Interfaces;
using AdamCodexHub.Infrastructure.Paths;

namespace AdamCodexHub.Infrastructure.Security;

public sealed class DpapiKeyVault : IKeyVault
{
    private readonly AppPaths _paths;

    public DpapiKeyVault(AppPaths paths)
    {
        _paths = paths;
    }

    public async Task<string> StoreAsync(
        string providerId,
        string secret,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);

        var id = Guid.NewGuid().ToString("N");
        var reference = $"{providerId}-{id}.secret";
        var path = Path.Combine(_paths.Secrets, reference);

        var plain = Encoding.UTF8.GetBytes(secret);
        var protectedBytes = ProtectedData.Protect(
            plain,
            optionalEntropy: null,
            DataProtectionScope.CurrentUser);

        await File.WriteAllBytesAsync(path, protectedBytes, cancellationToken);
        CryptographicOperations.ZeroMemory(plain);

        return reference;
    }

    public async Task<string?> RetrieveAsync(
        string secretReference,
        CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(_paths.Secrets, Path.GetFileName(secretReference));
        if (!File.Exists(path))
        {
            return null;
        }

        var protectedBytes = await File.ReadAllBytesAsync(path, cancellationToken);
        var plain = ProtectedData.Unprotect(
            protectedBytes,
            optionalEntropy: null,
            DataProtectionScope.CurrentUser);

        try
        {
            return Encoding.UTF8.GetString(plain);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public Task DeleteAsync(
        string secretReference,
        CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(_paths.Secrets, Path.GetFileName(secretReference));
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }
}
