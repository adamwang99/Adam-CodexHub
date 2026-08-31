using System.Reflection;
using System.Text.Json;
using AdamCodexHub.Core.Domain;
using AdamCodexHub.Core.Interfaces;

namespace AdamCodexHub.Providers.Registry;

public sealed class EmbeddedProviderRegistryService : IProviderRegistryService
{
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<IReadOnlyList<ProviderProfile>> GetBuiltInAsync(
        CancellationToken cancellationToken = default)
    {
        var assembly = typeof(EmbeddedProviderRegistryService).Assembly;
        var names = assembly.GetManifestResourceNames()
            .Where(x => x.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x)
            .ToArray();

        var providers = new List<ProviderProfile>();

        foreach (var name in names)
        {
            await using var stream = assembly.GetManifestResourceStream(name);
            if (stream is null)
            {
                continue;
            }

            var definition = await JsonSerializer.DeserializeAsync<ProviderDefinition>(
                stream,
                _json,
                cancellationToken);

            if (definition is null || string.IsNullOrWhiteSpace(definition.Id))
            {
                continue;
            }

            providers.Add(ToProfile(definition));
        }

        return providers;
    }

    private static ProviderProfile ToProfile(ProviderDefinition d)
    {
        _ = Enum.TryParse<ProviderTrustLevel>(
            d.TrustLevel,
            ignoreCase: true,
            out var trustLevel);

        return new ProviderProfile
        {
            Id = d.Id,
            Name = d.Name,
            Adapter = d.Adapter,
            BaseUrl = d.BaseUrl.TrimEnd('/'),
            TrustLevel = trustLevel,
            AuthType = d.Auth.Type,
            AuthHeaderName = d.Auth.HeaderName,
            ModelsEndpoint = d.Endpoints.Models,
            ResponsesEndpoint = d.Endpoints.Responses,
            ChatCompletionsEndpoint = d.Endpoints.ChatCompletions,
            ExtraHeaders = d.ExtraHeaders,
            DeclaredCapabilities = d.Capabilities
        };
    }
}
