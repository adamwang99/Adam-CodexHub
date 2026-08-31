using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AdamCodexHub.Core.Domain;
using AdamCodexHub.Core.Interfaces;

namespace AdamCodexHub.Providers.Adapters;

public sealed class OpenAiCompatibleAdapter : IProviderAdapter
{
    private readonly IHttpClientFactory _httpClientFactory;

    public OpenAiCompatibleAdapter(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public string AdapterId => "openai-compatible";

    public async Task<ProviderProbeResult> ProbeAsync(
        ProviderProfile provider,
        string? apiKey,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var models = await ListModelsAsync(provider, apiKey, cancellationToken);
            return new ProviderProbeResult(
                true,
                $"Connected. {models.Count} model(s) discovered.",
                new[] { provider.ModelsEndpoint ?? "/models" },
                provider.DeclaredCapabilities);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ProviderProbeResult(
                false,
                ex.Message,
                Array.Empty<string>(),
                Array.Empty<string>());
        }
    }

    public async Task<IReadOnlyList<ModelDescriptor>> ListModelsAsync(
        ProviderProfile provider,
        string? apiKey,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            Join(provider.BaseUrl, provider.ModelsEndpoint ?? "/models"));

        ApplyAuth(request, provider, apiKey);

        foreach (var header in provider.ExtraHeaders)
        {
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        var client = _httpClientFactory.CreateClient(nameof(OpenAiCompatibleAdapter));
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var ids = new List<string>();

        if (json.RootElement.TryGetProperty("data", out var data) &&
            data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
            {
                if (item.TryGetProperty("id", out var id) &&
                    id.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(id.GetString()))
                {
                    ids.Add(id.GetString()!);
                }
            }
        }
        else if (json.RootElement.TryGetProperty("models", out var models) &&
                 models.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in models.EnumerateArray())
            {
                if (item.TryGetProperty("id", out var id) &&
                    id.ValueKind == JsonValueKind.String)
                {
                    ids.Add(id.GetString()!);
                }
                else if (item.TryGetProperty("name", out var name) &&
                         name.ValueKind == JsonValueKind.String)
                {
                    ids.Add(name.GetString()!);
                }
            }
        }

        return ids.Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .Select(id => new ModelDescriptor
            {
                ProviderId = provider.Id,
                RemoteId = id,
                DisplayName = id,
                State = ModelLifecycleState.Discovered,
                LastSeenAt = DateTimeOffset.UtcNow
            })
            .ToArray();
    }

    public async Task<CompatibilityResult> TestModelAsync(
        ProviderProfile provider,
        string modelId,
        string? apiKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        var notes = new List<string>();
        ProbeResponse? responses = null;
        ProbeResponse? chat = null;

        if (!string.IsNullOrWhiteSpace(provider.ResponsesEndpoint))
        {
            responses = await SendProbeAsync(
                provider,
                apiKey,
                provider.ResponsesEndpoint,
                new
                {
                    model = modelId,
                    input = "Reply with OK.",
                    max_output_tokens = 16,
                    stream = false
                },
                cancellationToken);

            if (!responses.Success)
            {
                notes.Add($"Responses API: {responses.Error}");
            }
        }

        if (!string.IsNullOrWhiteSpace(provider.ChatCompletionsEndpoint))
        {
            chat = await SendProbeAsync(
                provider,
                apiKey,
                provider.ChatCompletionsEndpoint,
                new
                {
                    model = modelId,
                    messages = new[]
                    {
                        new { role = "user", content = "Reply with OK." }
                    },
                    max_tokens = 16,
                    stream = false
                },
                cancellationToken);

            if (!chat.Success)
            {
                notes.Add($"Chat Completions: {chat.Error}");
            }
        }

        var responsesSupported = responses?.Success == true;
        var chatSupported = chat?.Success == true;
        var text = responsesSupported || chatSupported;
        var streaming = text && await TestStreamingAsync(
            provider,
            modelId,
            apiKey,
            preferResponses: responsesSupported,
            cancellationToken);
        var toolCalling = text && await TestToolCallingAsync(
            provider,
            modelId,
            apiKey,
            preferResponses: responsesSupported,
            cancellationToken);
        var structuredJson = text && await TestStructuredJsonAsync(
            provider,
            modelId,
            apiKey,
            preferChat: chatSupported,
            cancellationToken);

        var score =
            (text ? 30 : 0) +
            (responsesSupported ? 15 : 0) +
            (chatSupported ? 10 : 0) +
            (streaming ? 15 : 0) +
            (toolCalling ? 20 : 0) +
            (structuredJson ? 10 : 0);

        return new CompatibilityResult
        {
            ProviderId = provider.Id,
            ModelId = modelId,
            VerifiedAt = DateTimeOffset.UtcNow,
            Text = text,
            Responses = responsesSupported,
            ChatCompletions = chatSupported,
            Streaming = streaming,
            ToolCalling = toolCalling,
            StructuredJson = structuredJson,
            Vision = false,
            Score = score,
            Notes = notes.Count == 0 ? null : string.Join(" ", notes)
        };
    }

    private async Task<bool> TestStreamingAsync(
        ProviderProfile provider,
        string modelId,
        string? apiKey,
        bool preferResponses,
        CancellationToken cancellationToken)
    {
        var endpoint = preferResponses
            ? provider.ResponsesEndpoint
            : provider.ChatCompletionsEndpoint;
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return false;
        }

        object payload = preferResponses
            ? new
            {
                model = modelId,
                input = "Reply with OK.",
                max_output_tokens = 16,
                stream = true
            }
            : new
            {
                model = modelId,
                messages = new[]
                {
                    new { role = "user", content = "Reply with OK." }
                },
                max_tokens = 16,
                stream = true
            };

        var result = await SendProbeAsync(provider, apiKey, endpoint, payload, cancellationToken);
        return result.Success &&
            (result.ContentType?.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase) == true ||
             result.Body?.Contains("data:", StringComparison.OrdinalIgnoreCase) == true);
    }

    private async Task<bool> TestToolCallingAsync(
        ProviderProfile provider,
        string modelId,
        string? apiKey,
        bool preferResponses,
        CancellationToken cancellationToken)
    {
        var endpoint = preferResponses
            ? provider.ResponsesEndpoint
            : provider.ChatCompletionsEndpoint;
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return false;
        }

        var parameters = new
        {
            type = "object",
            properties = new { },
            required = Array.Empty<string>(),
            additionalProperties = false
        };

        object payload = preferResponses
            ? new
            {
                model = modelId,
                input = "Call the ping function now.",
                tools = new[]
                {
                    new
                    {
                        type = "function",
                        name = "ping",
                        description = "Returns a health check.",
                        parameters,
                        strict = true
                    }
                },
                tool_choice = "required",
                max_output_tokens = 32
            }
            : new
            {
                model = modelId,
                messages = new[]
                {
                    new { role = "user", content = "Call the ping function now." }
                },
                tools = new[]
                {
                    new
                    {
                        type = "function",
                        function = new
                        {
                            name = "ping",
                            description = "Returns a health check.",
                            parameters,
                            strict = true
                        }
                    }
                },
                tool_choice = "required",
                max_tokens = 32
            };

        var result = await SendProbeAsync(provider, apiKey, endpoint, payload, cancellationToken);
        return result.Success &&
            (result.Body?.Contains("tool_calls", StringComparison.OrdinalIgnoreCase) == true ||
             result.Body?.Contains("function_call", StringComparison.OrdinalIgnoreCase) == true);
    }

    private async Task<bool> TestStructuredJsonAsync(
        ProviderProfile provider,
        string modelId,
        string? apiKey,
        bool preferChat,
        CancellationToken cancellationToken)
    {
        var endpoint = preferChat
            ? provider.ChatCompletionsEndpoint
            : provider.ResponsesEndpoint;
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return false;
        }

        object payload = preferChat
            ? new
            {
                model = modelId,
                messages = new[]
                {
                    new { role = "user", content = "Return a JSON object with ok set to true." }
                },
                response_format = new { type = "json_object" },
                max_tokens = 32
            }
            : new
            {
                model = modelId,
                input = "Return a JSON object with ok set to true.",
                text = new { format = new { type = "json_object" } },
                max_output_tokens = 32
            };

        return (await SendProbeAsync(provider, apiKey, endpoint, payload, cancellationToken)).Success;
    }

    private async Task<ProbeResponse> SendProbeAsync(
        ProviderProfile provider,
        string? apiKey,
        string endpoint,
        object payload,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Join(provider.BaseUrl, endpoint))
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json")
        };
        ApplyAuth(request, provider, apiKey);

        foreach (var header in provider.ExtraHeaders)
        {
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));

        try
        {
            var client = _httpClientFactory.CreateClient(nameof(OpenAiCompatibleAdapter));
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseContentRead,
                timeout.Token);

            if (!response.IsSuccessStatusCode)
            {
                return new ProbeResponse(
                    false,
                    null,
                    response.Content.Headers.ContentType?.MediaType,
                    $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
            }

            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            return new ProbeResponse(
                true,
                body,
                response.Content.Headers.ContentType?.MediaType,
                null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new ProbeResponse(false, null, null, "Timed out after 30 seconds.");
        }
        catch (HttpRequestException ex)
        {
            return new ProbeResponse(false, null, null, ex.Message);
        }
    }

    private static void ApplyAuth(
        HttpRequestMessage request,
        ProviderProfile provider,
        string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || provider.AuthType == "none")
        {
            return;
        }

        if (provider.AuthType.Equals("x-api-key", StringComparison.OrdinalIgnoreCase))
        {
            request.Headers.TryAddWithoutValidation(
                provider.AuthHeaderName ?? "x-api-key",
                apiKey);
            return;
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    private static string Join(string baseUrl, string relative) =>
        $"{baseUrl.TrimEnd('/')}/{relative.TrimStart('/')}";

    private sealed record ProbeResponse(
        bool Success,
        string? Body,
        string? ContentType,
        string? Error);
}
