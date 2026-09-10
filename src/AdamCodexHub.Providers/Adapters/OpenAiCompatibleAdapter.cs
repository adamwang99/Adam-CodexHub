using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AdamCodexHub.Core.Domain;
using AdamCodexHub.Core.Interfaces;

namespace AdamCodexHub.Providers.Adapters;

public sealed class OpenAiCompatibleAdapter : IProviderAdapter
{
    /// <summary>
    /// Budget for a single probe request. It must NOT be short: on a slow gateway a model that
    /// works perfectly well can take 60-90s to answer (measured 2026-09-10: HHTech returns
    /// claude-opus-4-7 in 77s while other models on the same provider answer in 4s). The old
    /// 30s cap marked those models as failed. 150s leaves headroom above the slowest observed
    /// response while still bounding the probe.
    /// </summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(150);

    /// <summary>Bytes read from a streaming response before giving up on finding an SSE event.
    /// The streaming probe stops at the first `data:` line, this only bounds pathological streams.</summary>
    private const int StreamingReadCapBytes = 64 * 1024;

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
        IProgress<ModelTestProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        var notes = new List<string>();
        ProbeResponse? responses = null;
        ProbeResponse? chat = null;

        if (!string.IsNullOrWhiteSpace(provider.ResponsesEndpoint))
        {
            progress?.Report(new ModelTestProgress("Responses API", ModelTestStepStatus.Running));
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
            progress?.Report(new ModelTestProgress(
                "Responses API",
                responses.Success ? ModelTestStepStatus.Passed : ModelTestStepStatus.Failed,
                responses.Success ? null : responses.Error));
        }

        if (!string.IsNullOrWhiteSpace(provider.ChatCompletionsEndpoint))
        {
            progress?.Report(new ModelTestProgress("Chat Completions", ModelTestStepStatus.Running));
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
            progress?.Report(new ModelTestProgress(
                "Chat Completions",
                chat.Success ? ModelTestStepStatus.Passed : ModelTestStepStatus.Failed,
                chat.Success ? null : chat.Error));
        }

        var responsesSupported = responses?.Success == true;
        var chatSupported = chat?.Success == true;
        var text = responsesSupported || chatSupported;
        var streaming = false;
        int? firstByteMs = null;
        if (text)
        {
            (streaming, firstByteMs) = await TestStreamingAsync(
                provider,
                modelId,
                apiKey,
                preferResponses: responsesSupported,
                progress,
                cancellationToken);
        }

        var toolCalling = text && await TestToolCallingAsync(
            provider,
            modelId,
            apiKey,
            preferResponses: responsesSupported,
            progress,
            cancellationToken);
        var structuredJson = text && await TestStructuredJsonAsync(
            provider,
            modelId,
            apiKey,
            preferChat: chatSupported,
            progress,
            cancellationToken);

        var score =
            (text ? 30 : 0) +
            (responsesSupported ? 15 : 0) +
            (chatSupported ? 10 : 0) +
            (streaming ? 15 : 0) +
            (toolCalling ? 20 : 0) +
            (structuredJson ? 10 : 0);

        // TotalMs = the whole non-stream request. When both the responses and the chat probe ran,
        // report the faster successful one — the classification only needs "is this provider/model
        // fast or slow", and the faster path is what the gateway would use.
        int? totalMs = null;
        if (responses is { Success: true, TotalMs: { } responsesMs })
        {
            totalMs = responsesMs;
        }

        if (chat is { Success: true, TotalMs: { } chatMs } &&
            (totalMs is null || chatMs < totalMs))
        {
            totalMs = chatMs;
        }

        totalMs ??= responses?.TotalMs ?? chat?.TotalMs;

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
            Notes = notes.Count == 0 ? null : string.Join(" ", notes),
            FirstByteMs = firstByteMs,
            TotalMs = totalMs
        };
    }

    private async Task<(bool Ok, int? FirstByteMs)> TestStreamingAsync(
        ProviderProfile provider,
        string modelId,
        string? apiKey,
        bool preferResponses,
        IProgress<ModelTestProgress>? progress,
        CancellationToken cancellationToken)
    {
        var endpoint = preferResponses
            ? provider.ResponsesEndpoint
            : provider.ChatCompletionsEndpoint;
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return (false, null);
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

        progress?.Report(new ModelTestProgress("Streaming", ModelTestStepStatus.Running));
        var result = await SendStreamingProbeAsync(provider, apiKey, endpoint, payload, cancellationToken);
        var ok = result.Success &&
            (result.ContentType?.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase) == true ||
             result.Body?.Contains("data:", StringComparison.OrdinalIgnoreCase) == true);
        progress?.Report(new ModelTestProgress(
            "Streaming",
            ok ? ModelTestStepStatus.Passed : ModelTestStepStatus.Failed,
            ok ? null : (result.Success ? "No SSE stream detected." : result.Error)));
        return (ok, result.FirstByteMs);
    }

    private async Task<bool> TestToolCallingAsync(
        ProviderProfile provider,
        string modelId,
        string? apiKey,
        bool preferResponses,
        IProgress<ModelTestProgress>? progress,
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
        var ok = result.Success &&
            (result.Body?.Contains("tool_calls", StringComparison.OrdinalIgnoreCase) == true ||
             result.Body?.Contains("function_call", StringComparison.OrdinalIgnoreCase) == true);
        progress?.Report(new ModelTestProgress(
            "Tool Calling",
            ok ? ModelTestStepStatus.Passed : ModelTestStepStatus.Failed,
            ok ? null : (result.Success ? "No tool call detected." : result.Error)));
        return ok;
    }

    private async Task<bool> TestStructuredJsonAsync(
        ProviderProfile provider,
        string modelId,
        string? apiKey,
        bool preferChat,
        IProgress<ModelTestProgress>? progress,
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

        progress?.Report(new ModelTestProgress("Structured JSON", ModelTestStepStatus.Running));
        var ok = (await SendProbeAsync(provider, apiKey, endpoint, payload, cancellationToken)).Success;
        progress?.Report(new ModelTestProgress(
            "Structured JSON",
            ok ? ModelTestStepStatus.Passed : ModelTestStepStatus.Failed,
            ok ? null : "No valid JSON response."));
        return ok;
    }

    private async Task<ProbeResponse> SendProbeAsync(
        ProviderProfile provider,
        string? apiKey,
        string endpoint,
        object payload,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
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
        timeout.CancelAfter(ProbeTimeout);

        try
        {
            var client = _httpClientFactory.CreateClient(nameof(OpenAiCompatibleAdapter));
            // The factory hands out a fresh HttpClient per call, so its own timeout can be raised
            // to the probe budget: the default 100s would silently cut a 150s budget short.
            client.Timeout = ProbeTimeout;
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
                    $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}",
                    null,
                    ElapsedMs(stopwatch));
            }

            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            return new ProbeResponse(
                true,
                body,
                response.Content.Headers.ContentType?.MediaType,
                null,
                null,
                ElapsedMs(stopwatch));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new ProbeResponse(
                false,
                null,
                null,
                $"Timed out after {ProbeTimeout.TotalSeconds:0} seconds.",
                null,
                ElapsedMs(stopwatch));
        }
        catch (HttpRequestException ex)
        {
            return new ProbeResponse(false, null, null, ex.Message, null, ElapsedMs(stopwatch));
        }
    }

    /// <summary>
    /// Streaming variant of <see cref="SendProbeAsync"/>: only the response headers are awaited
    /// first, then the body is read incrementally so the time to the FIRST streamed byte can be
    /// measured (the number the UI shows as "byte đầu 37,7s"). Reading stops as soon as a `data:`
    /// SSE line has been seen, which keeps the probe short on a model that streams for minutes;
    /// the pass/fail semantics are unchanged (success + an SSE stream).
    /// </summary>
    private async Task<ProbeResponse> SendStreamingProbeAsync(
        ProviderProfile provider,
        string? apiKey,
        string endpoint,
        object payload,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
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
        timeout.CancelAfter(ProbeTimeout);

        try
        {
            var client = _httpClientFactory.CreateClient(nameof(OpenAiCompatibleAdapter));
            client.Timeout = ProbeTimeout;
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);

            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (!response.IsSuccessStatusCode)
            {
                return new ProbeResponse(
                    false,
                    null,
                    contentType,
                    $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}",
                    null,
                    ElapsedMs(stopwatch));
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            var buffer = new byte[1024];
            var body = new StringBuilder();
            int? firstByteMs = null;
            while (body.Length < StreamingReadCapBytes)
            {
                var read = await stream.ReadAsync(buffer, timeout.Token);
                if (read <= 0)
                {
                    break;
                }

                firstByteMs ??= ElapsedMs(stopwatch);
                body.Append(Encoding.UTF8.GetString(buffer, 0, read));
                if (body.ToString().Contains("data:", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
            }

            return new ProbeResponse(
                true,
                body.ToString(),
                contentType,
                null,
                firstByteMs,
                ElapsedMs(stopwatch));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new ProbeResponse(
                false,
                null,
                null,
                $"Timed out after {ProbeTimeout.TotalSeconds:0} seconds.",
                null,
                ElapsedMs(stopwatch));
        }
        catch (HttpRequestException ex)
        {
            return new ProbeResponse(false, null, null, ex.Message, null, ElapsedMs(stopwatch));
        }
    }

    private static int ElapsedMs(Stopwatch stopwatch) => (int)Math.Min(
        int.MaxValue,
        Math.Round(stopwatch.Elapsed.TotalMilliseconds));

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
        string? Error,
        int? FirstByteMs,
        int? TotalMs);
}
