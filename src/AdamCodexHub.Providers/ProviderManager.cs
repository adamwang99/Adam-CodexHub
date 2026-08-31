using AdamCodexHub.Core.Domain;
using AdamCodexHub.Core.Interfaces;

namespace AdamCodexHub.Providers;

public sealed class ProviderManager : IProviderManager
{
    public const string CodexAccountProviderId = "codex-account";

    private static readonly HashSet<string> SensitiveHeaderNames = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "Authorization",
        "Proxy-Authorization",
        "Api-Key",
        "X-Api-Key",
        "Cookie",
        "Set-Cookie"
    };

    private readonly IProviderRegistryService _registry;
    private readonly IProviderStore _store;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<ProviderProfile> _providers = new();
    private readonly HashSet<string> _builtInProviderIds = new(StringComparer.OrdinalIgnoreCase);
    private string? _activeProviderId;
    private bool _initialized;

    public ProviderManager(IProviderRegistryService registry, IProviderStore store)
    {
        _registry = registry;
        _store = store;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ProviderProfile>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedAsync(cancellationToken);
            return _providers
                .OrderByDescending(x => x.Id == CodexAccountProviderId)
                .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ProviderProfile?> GetAsync(
        string providerId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedAsync(cancellationToken);
            return Find(providerId);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(
        ProviderProfile provider,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var normalized = NormalizeAndValidate(provider);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedAsync(cancellationToken);
            await _store.UpsertAsync(normalized, cancellationToken);
            ReplaceOrAdd(normalized);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetEnabledAsync(
        string providerId,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedAsync(cancellationToken);
            var provider = Find(providerId)
                ?? throw new InvalidOperationException($"Provider '{providerId}' was not found.");

            if (!enabled && provider.Id == CodexAccountProviderId)
            {
                throw new InvalidOperationException("Codex Account cannot be disabled.");
            }

            var updated = provider with
            {
                Enabled = enabled,
                Health = enabled && provider.Health == ProviderHealth.Disabled
                    ? ProviderHealth.Unknown
                    : enabled
                        ? provider.Health
                        : ProviderHealth.Disabled
            };

            await _store.UpsertAsync(updated, cancellationToken);
            ReplaceOrAdd(updated);

            if (!enabled && string.Equals(_activeProviderId, provider.Id, StringComparison.OrdinalIgnoreCase))
            {
                await SetActiveCoreAsync(CodexAccountProviderId, cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> DeleteAsync(
        string providerId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedAsync(cancellationToken);

            if (_builtInProviderIds.Contains(providerId))
            {
                throw new InvalidOperationException("Built-in providers can be disabled but not deleted.");
            }

            var provider = Find(providerId);
            if (provider is null)
            {
                return false;
            }

            if (string.Equals(_activeProviderId, provider.Id, StringComparison.OrdinalIgnoreCase))
            {
                await SetActiveCoreAsync(CodexAccountProviderId, cancellationToken);
            }

            var deleted = await _store.DeleteAsync(provider.Id, cancellationToken);
            if (deleted)
            {
                _providers.Remove(provider);
            }

            return deleted;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetActiveAsync(
        string providerId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedAsync(cancellationToken);
            await SetActiveCoreAsync(providerId, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ProviderProfile?> GetActiveAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedAsync(cancellationToken);
            return _activeProviderId is null ? null : Find(_activeProviderId);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await _store.InitializeAsync(cancellationToken);

        var defaults = new List<ProviderProfile> { CreateCodexAccountProvider() };
        defaults.AddRange(await _registry.GetBuiltInAsync(cancellationToken));

        var persisted = (await _store.GetAllAsync(cancellationToken))
            .ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);

        foreach (var definition in defaults.Select(NormalizeAndValidate))
        {
            _builtInProviderIds.Add(definition.Id);

            var merged = persisted.TryGetValue(definition.Id, out var saved)
                ? definition with
                {
                    Enabled = saved.Enabled,
                    Health = saved.Health
                }
                : definition;

            _providers.Add(merged);
            await _store.UpsertAsync(merged, cancellationToken);
            persisted.Remove(definition.Id);
        }

        foreach (var customProvider in persisted.Values.Select(NormalizeAndValidate))
        {
            _providers.Add(customProvider);
        }

        var savedActiveId = await _store.GetActiveProviderIdAsync(cancellationToken);
        var savedActive = savedActiveId is null ? null : Find(savedActiveId);
        _activeProviderId = savedActive is { Enabled: true }
            ? savedActive.Id
            : CodexAccountProviderId;

        await _store.SetActiveProviderIdAsync(_activeProviderId, cancellationToken);
        _initialized = true;
    }

    private async Task SetActiveCoreAsync(string providerId, CancellationToken cancellationToken)
    {
        var provider = Find(providerId)
            ?? throw new InvalidOperationException($"Provider '{providerId}' was not found.");

        if (!provider.Enabled)
        {
            throw new InvalidOperationException($"Provider '{provider.Name}' is disabled.");
        }

        await _store.SetActiveProviderIdAsync(provider.Id, cancellationToken);
        _activeProviderId = provider.Id;
    }

    private ProviderProfile? Find(string providerId) =>
        _providers.FirstOrDefault(x =>
            string.Equals(x.Id, providerId, StringComparison.OrdinalIgnoreCase));

    private void ReplaceOrAdd(ProviderProfile provider)
    {
        var existing = Find(provider.Id);
        if (existing is not null)
        {
            _providers[_providers.IndexOf(existing)] = provider;
            return;
        }

        _providers.Add(provider);
    }

    private static ProviderProfile NormalizeAndValidate(ProviderProfile provider)
    {
        var id = provider.Id.Trim().ToLowerInvariant();
        if (id.Length == 0 || !char.IsLetterOrDigit(id[0]) || id.Any(c =>
                !char.IsLetterOrDigit(c) && c is not '.' and not '_' and not '-'))
        {
            throw new ArgumentException("Provider id must match ^[a-z0-9][a-z0-9._-]*$.", nameof(provider));
        }

        if (string.IsNullOrWhiteSpace(provider.Name))
        {
            throw new ArgumentException("Provider name is required.", nameof(provider));
        }

        if (string.IsNullOrWhiteSpace(provider.Adapter))
        {
            throw new ArgumentException("Provider adapter is required.", nameof(provider));
        }

        var baseUrl = provider.BaseUrl.Trim().TrimEnd('/');
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ||
            (id != CodexAccountProviderId && uri.Scheme is not "http" and not "https"))
        {
            throw new ArgumentException("Provider base URL must be an absolute HTTP or HTTPS URI.", nameof(provider));
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in provider.ExtraHeaders)
        {
            if (SensitiveHeaderNames.Contains(name))
            {
                throw new ArgumentException(
                    $"Secret-bearing header '{name}' must use the key vault, not provider metadata.",
                    nameof(provider));
            }

            headers[name.Trim()] = value;
        }

        return provider with
        {
            Id = id,
            Name = provider.Name.Trim(),
            Adapter = provider.Adapter.Trim(),
            BaseUrl = baseUrl,
            AuthType = provider.AuthType.Trim(),
            AuthHeaderName = NullIfWhiteSpace(provider.AuthHeaderName),
            ModelsEndpoint = NullIfWhiteSpace(provider.ModelsEndpoint),
            ResponsesEndpoint = NullIfWhiteSpace(provider.ResponsesEndpoint),
            ChatCompletionsEndpoint = NullIfWhiteSpace(provider.ChatCompletionsEndpoint),
            ExtraHeaders = headers,
            DeclaredCapabilities = provider.DeclaredCapabilities
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()
        };
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static ProviderProfile CreateCodexAccountProvider() => new()
    {
        Id = CodexAccountProviderId,
        Name = "Codex Account",
        Adapter = "codex-account",
        BaseUrl = "native://codex-account",
        TrustLevel = ProviderTrustLevel.Official,
        Health = ProviderHealth.Unknown,
        AuthType = "native",
        DeclaredCapabilities = new[]
        {
            "native-login",
            "first-party"
        }
    };
}
