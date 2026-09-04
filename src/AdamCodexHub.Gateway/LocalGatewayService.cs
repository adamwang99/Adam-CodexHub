using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AdamCodexHub.Core.Domain;
using AdamCodexHub.Core.Interfaces;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace AdamCodexHub.Gateway;

public sealed class LocalGatewayService : IGatewayService
{
    private const long MaxRequestBodySize = 10 * 1024 * 1024;
    private const int MaxKeyAttempts = 3;
    private readonly IProviderManager _providers;
    private readonly IKeyPoolService _keys;
    private readonly IModelStore _models;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private string _localToken = CreateLocalToken();
    private WebApplication? _app;

    public LocalGatewayService(
        IProviderManager providers,
        IKeyPoolService keys,
        IModelStore models,
        IHttpClientFactory httpClientFactory)
    {
        _providers = providers;
        _keys = keys;
        _models = models;
        _httpClientFactory = httpClientFactory;
    }

    public bool IsRunning => _app is not null;
    public int Port { get; private set; }
    public string LocalToken => _localToken;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            if (_app is not null)
            {
                return;
            }

            _localToken = CreateLocalToken();
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.Limits.MaxRequestBodySize = MaxRequestBodySize;
                options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15);
            });

            var app = builder.Build();
            MapEndpoints(app);

            try
            {
                await app.StartAsync(cancellationToken);
                var address = app.Urls.FirstOrDefault()
                    ?? throw new InvalidOperationException("Gateway did not publish a loopback address.");
                Port = new Uri(address).Port;
                _app = app;
            }
            catch
            {
                await app.DisposeAsync();
                throw;
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            if (_app is null)
            {
                return;
            }

            var app = _app;
            _app = null;
            Port = 0;
            await app.StopAsync(cancellationToken);
            await app.DisposeAsync();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }

    private void MapEndpoints(WebApplication app)
    {
        app.MapGet("/health", (HttpContext context) =>
        {
            if (!IsLoopback(context))
            {
                return Results.NotFound();
            }

            return Results.Ok(new
            {
                service = "Adam CodexHub Gateway",
                status = "healthy",
                port = Port
            });
        });

        app.MapGet("/v1/models", GetModelsAsync);
        app.MapPost("/v1/responses", context => ForwardAsync(context, GatewayWireApi.Responses));
        app.MapPost("/v1/chat/completions", context => ForwardAsync(context, GatewayWireApi.ChatCompletions));
    }

    private async Task GetModelsAsync(HttpContext context)
    {
        if (!await AuthorizeAsync(context))
        {
            return;
        }

        var provider = await _providers.GetActiveAsync(context.RequestAborted);
        if (provider is null || provider.Adapter == "codex-account")
        {
            await WriteErrorAsync(
                context,
                StatusCodes.Status409Conflict,
                "No API provider is active.",
                "adam_codexhub_provider_required");
            return;
        }

        var models = await _models.GetAllAsync(provider.Id, context.RequestAborted);
        await context.Response.WriteAsJsonAsync(
            new
            {
                @object = "list",
                data = models
                    .Where(x => x.Enabled && x.State == ModelLifecycleState.Enabled)
                    .Select(x => new
                    {
                        id = x.RemoteId,
                        @object = "model",
                        owned_by = provider.Id
                    })
                    .ToArray()
            },
            context.RequestAborted);
    }

    private async Task ForwardAsync(HttpContext context, GatewayWireApi wireApi)
    {
        if (!await AuthorizeAsync(context))
        {
            return;
        }

        byte[] body;
        try
        {
            body = await ReadRequestBodyAsync(context.Request, context.RequestAborted);
        }
        catch (BadHttpRequestException ex)
        {
            await WriteErrorAsync(
                context,
                ex.StatusCode,
                ex.Message,
                "adam_codexhub_invalid_request");
            return;
        }

        string modelId;
        try
        {
            using var json = JsonDocument.Parse(body);
            modelId = json.RootElement.TryGetProperty("model", out var modelElement) &&
                      modelElement.ValueKind == JsonValueKind.String
                ? modelElement.GetString() ?? string.Empty
                : string.Empty;
        }
        catch (JsonException)
        {
            await WriteErrorAsync(
                context,
                StatusCodes.Status400BadRequest,
                "Request body must be valid JSON.",
                "adam_codexhub_invalid_json");
            return;
        }

        if (string.IsNullOrWhiteSpace(modelId))
        {
            await WriteErrorAsync(
                context,
                StatusCodes.Status400BadRequest,
                "A model id is required.",
                "adam_codexhub_model_required");
            return;
        }

        var provider = await _providers.GetActiveAsync(context.RequestAborted);
        if (provider is null || provider.Adapter == "codex-account")
        {
            await WriteErrorAsync(
                context,
                StatusCodes.Status409Conflict,
                "No API provider is active.",
                "adam_codexhub_provider_required");
            return;
        }

        if (!provider.Enabled)
        {
            await WriteErrorAsync(
                context,
                StatusCodes.Status409Conflict,
                $"Provider '{provider.Name}' is disabled.",
                "adam_codexhub_provider_disabled");
            return;
        }

        var model = await _models.GetAsync(provider.Id, modelId, context.RequestAborted);
        if (model is not { Enabled: true, State: ModelLifecycleState.Enabled })
        {
            await WriteErrorAsync(
                context,
                StatusCodes.Status400BadRequest,
                $"Model '{modelId}' is not enabled for provider '{provider.Name}'.",
                "adam_codexhub_model_not_enabled");
            return;
        }

        var endpoint = wireApi == GatewayWireApi.Responses
            ? provider.ResponsesEndpoint
            : provider.ChatCompletionsEndpoint;
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            await WriteErrorAsync(
                context,
                StatusCodes.Status501NotImplemented,
                $"Provider '{provider.Name}' does not declare the requested API endpoint.",
                "adam_codexhub_endpoint_unsupported");
            return;
        }

        var requiresKey = !provider.AuthType.Equals("none", StringComparison.OrdinalIgnoreCase);
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var attempts = 0;
        string? lastFailure = null;

        while (attempts < MaxKeyAttempts)
        {
            var selection = requiresKey
                ? await _keys.GetActiveAsync(provider.Id, excluded, context.RequestAborted)
                : null;
            if (requiresKey && selection is null)
            {
                break;
            }

            attempts++;
            if (selection is not null)
            {
                excluded.Add(selection.Key.Id);
            }

            using var upstreamRequest = CreateUpstreamRequest(
                context.Request,
                provider,
                endpoint,
                body,
                selection?.Secret);

            HttpResponseMessage upstreamResponse;
            try
            {
                var client = _httpClientFactory.CreateClient(nameof(LocalGatewayService));
                upstreamResponse = await client.SendAsync(
                    upstreamRequest,
                    HttpCompletionOption.ResponseHeadersRead,
                    context.RequestAborted);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                return;
            }
            catch (HttpRequestException ex)
            {
                lastFailure = ex.Message;
                if (selection is not null)
                {
                    await _keys.MarkFailureAsync(
                        selection.Key.Id,
                        KeyHealth.Offline,
                        "Provider connection failed.",
                        TimeSpan.FromSeconds(10),
                        context.RequestAborted);
                    continue;
                }

                break;
            }

            using (upstreamResponse)
            {
                if (selection is not null && IsRetryable(upstreamResponse.StatusCode))
                {
                    var failure = MapFailure(upstreamResponse);
                    lastFailure = failure.Message;
                    await _keys.MarkFailureAsync(
                        selection.Key.Id,
                        failure.Health,
                        failure.Message,
                        failure.Cooldown,
                        context.RequestAborted);
                    continue;
                }

                if (selection is not null && upstreamResponse.IsSuccessStatusCode)
                {
                    await _keys.MarkSuccessAsync(selection.Key.Id, context.RequestAborted);
                }

                await RelayResponseAsync(context, upstreamResponse);
                return;
            }
        }

        await WriteErrorAsync(
            context,
            StatusCodes.Status503ServiceUnavailable,
            lastFailure is null
                ? $"No usable API key remains for provider '{provider.Name}'."
                : $"Provider '{provider.Name}' is unavailable after {attempts} attempt(s): {lastFailure}",
            "adam_codexhub_no_usable_key");
    }

    private async Task<bool> AuthorizeAsync(HttpContext context)
    {
        if (!IsLoopback(context))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return false;
        }

        if (!HasValidLocalToken(context.Request.Headers.Authorization.ToString()))
        {
            context.Response.Headers.WWWAuthenticate = "Bearer";
            await WriteErrorAsync(
                context,
                StatusCodes.Status401Unauthorized,
                "A valid local gateway token is required.",
                "adam_codexhub_local_auth_failed");
            return false;
        }

        return true;
    }

    private bool HasValidLocalToken(string authorization)
    {
        if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var providedToken = authorization["Bearer ".Length..].Trim();
        if (providedToken.Length == 0)
        {
            return false;
        }

        var providedHash = SHA256.HashData(Encoding.UTF8.GetBytes(providedToken));
        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(_localToken));
        return CryptographicOperations.FixedTimeEquals(providedHash, expectedHash);
    }

    private static string CreateLocalToken() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    private static bool IsLoopback(HttpContext context) =>
        context.Connection.RemoteIpAddress is { } address && IPAddress.IsLoopback(address);

    private static async Task<byte[]> ReadRequestBodyAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        if (request.ContentLength > MaxRequestBodySize)
        {
            throw new BadHttpRequestException(
                "Request body exceeds the 10 MB gateway limit.",
                StatusCodes.Status413PayloadTooLarge);
        }

        await using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, cancellationToken);
        if (buffer.Length > MaxRequestBodySize)
        {
            throw new BadHttpRequestException(
                "Request body exceeds the 10 MB gateway limit.",
                StatusCodes.Status413PayloadTooLarge);
        }

        return buffer.ToArray();
    }

    private static HttpRequestMessage CreateUpstreamRequest(
        HttpRequest incoming,
        ProviderProfile provider,
        string endpoint,
        byte[] body,
        string? apiKey)
    {
        var uri = BuildUpstreamUri(provider, endpoint, apiKey);
        var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new ByteArrayContent(body)
        };
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(
            incoming.ContentType ?? "application/json");

        CopyHeader(incoming, request, "Accept");
        CopyHeader(incoming, request, "Accept-Encoding");
        CopyHeader(incoming, request, "User-Agent");
        CopyHeader(incoming, request, "OpenAI-Beta");

        if (!string.IsNullOrWhiteSpace(apiKey) &&
            !provider.AuthType.Equals("none", StringComparison.OrdinalIgnoreCase) &&
            !provider.AuthType.Equals("query", StringComparison.OrdinalIgnoreCase))
        {
            if (provider.AuthType.Equals("x-api-key", StringComparison.OrdinalIgnoreCase))
            {
                request.Headers.TryAddWithoutValidation(
                    provider.AuthHeaderName ?? "x-api-key",
                    apiKey);
            }
            else
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }
        }

        foreach (var header in provider.ExtraHeaders)
        {
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return request;
    }

    private static Uri BuildUpstreamUri(
        ProviderProfile provider,
        string endpoint,
        string? apiKey)
    {
        var uri = new Uri($"{provider.BaseUrl.TrimEnd('/')}/{endpoint.TrimStart('/')}");
        if (!provider.AuthType.Equals("query", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(apiKey))
        {
            return uri;
        }

        var builder = new UriBuilder(uri);
        var parameterName = Uri.EscapeDataString(provider.AuthHeaderName ?? "key");
        var separator = string.IsNullOrWhiteSpace(builder.Query) ? string.Empty : "&";
        builder.Query = $"{builder.Query.TrimStart('?')}{separator}{parameterName}={Uri.EscapeDataString(apiKey)}";
        return builder.Uri;
    }

    private static void CopyHeader(
        HttpRequest incoming,
        HttpRequestMessage outgoing,
        string name)
    {
        if (incoming.Headers.TryGetValue(name, out StringValues values))
        {
            outgoing.Headers.TryAddWithoutValidation(name, values.ToArray());
        }
    }

    private static bool IsRetryable(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.PaymentRequired or
        HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
        (int)statusCode >= 500;

    private static KeyFailure MapFailure(HttpResponseMessage response)
    {
        return response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => new KeyFailure(
                KeyHealth.Unauthorized,
                "API key was rejected by the provider.",
                null),
            HttpStatusCode.PaymentRequired => new KeyFailure(
                KeyHealth.QuotaEmpty,
                "API key quota is exhausted.",
                null),
            HttpStatusCode.TooManyRequests => new KeyFailure(
                KeyHealth.Cooldown,
                "API key is rate limited.",
                GetRetryAfter(response) ?? TimeSpan.FromSeconds(60)),
            _ => new KeyFailure(
                KeyHealth.Offline,
                $"Provider returned HTTP {(int)response.StatusCode} {response.ReasonPhrase}.",
                TimeSpan.FromSeconds(10))
        };
    }

    private static TimeSpan? GetRetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        var duration = retryAfter?.Delta ??
            (retryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : null);

        return duration.HasValue
            ? TimeSpan.FromSeconds(Math.Clamp(duration.Value.TotalSeconds, 1, 600))
            : null;
    }

    private static async Task RelayResponseAsync(
        HttpContext context,
        HttpResponseMessage upstream)
    {
        context.Response.StatusCode = (int)upstream.StatusCode;

        foreach (var header in upstream.Headers)
        {
            if (!IsHopByHopHeader(header.Key))
            {
                context.Response.Headers[header.Key] = new StringValues(header.Value.ToArray());
            }
        }

        foreach (var header in upstream.Content.Headers)
        {
            if (!IsHopByHopHeader(header.Key))
            {
                context.Response.Headers[header.Key] = new StringValues(header.Value.ToArray());
            }
        }

        context.Response.Headers.Remove("transfer-encoding");
        await upstream.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
    }

    private static bool IsHopByHopHeader(string name) =>
        name.Equals("Connection", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Proxy-Authenticate", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("TE", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Trailer", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Upgrade", StringComparison.OrdinalIgnoreCase);

    private static Task WriteErrorAsync(
        HttpContext context,
        int statusCode,
        string message,
        string type)
    {
        context.Response.StatusCode = statusCode;
        return context.Response.WriteAsJsonAsync(new
        {
            error = new
            {
                message,
                type
            }
        });
    }

    private enum GatewayWireApi
    {
        Responses,
        ChatCompletions
    }

    private sealed record KeyFailure(
        KeyHealth Health,
        string Message,
        TimeSpan? Cooldown);
}
