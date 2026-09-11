using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AdamCodexHub.Core.Domain;
using AdamCodexHub.Core.Interfaces;
using AdamCodexHub.Core.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace AdamCodexHub.Gateway;

public sealed class LocalGatewayService : IGatewayService
{
    // Codex sends the whole conversation with every turn, and a long thread with screenshots in it is
    // tens of megabytes: measured 2026-09-11, a real chat hit the old 10 MB ceiling and the gateway
    // answered 413 "Request body exceeds the 10 MB gateway limit." (Adam, `POST /v1/responses`). The
    // ceiling is a guard against a runaway body, not a budget for a working session.
    private const long MaxRequestBodySize = 64 * 1024 * 1024;

    /// <summary>Header the background probes send, so the gateway can tell them apart from the user's
    /// own Codex traffic and keep the key's health accounting to the latter.</summary>
    public const string ProbeHeaderName = "x-adam-codexhub-probe";

    /// <summary>
    /// How often the same model may be refused inside <see cref="StuckTurnWindow"/> before the hub says
    /// so. Codex retries a failed turn with the model that turn started on, so picking a new model in
    /// the UI does not change the requests: on 2026-09-11 thirteen requests in sixteen seconds all
    /// asked for `gpt-5.6-luna` while the picker, the thread and the config all said
    /// `claude-opus-4-7[1M]`, and the user reasonably concluded the model he picked was not connected.
    /// </summary>
    private const int StuckTurnRefusals = 3;

    private static readonly TimeSpan StuckTurnWindow = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<string, (DateTimeOffset First, DateTimeOffset Last, int Count)> _refusals = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, DateTimeOffset> _fallbackLogged = new(StringComparer.Ordinal);

    /// <summary>Reads the model the user picked in Codex — the picker only records it in Codex's own state.</summary>
    private readonly ICodexSessionModelReader _sessionModels;

    private static bool IsProbeRequest(HttpContext context) =>
        context.Request.Headers.ContainsKey(ProbeHeaderName);

    /// <summary>User-Agent, flattened and capped: it is what tells a Codex app-server request apart
    /// from a plain curl when reading the log.</summary>
    private static string Trim(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "(none)";
        }

        var flat = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length <= 80 ? flat : flat[..80];
    }
    private const int MaxKeyAttempts = 3;
    private const int DefaultGatewayPort = 20129;
    private readonly IProviderManager _providers;
    private readonly IKeyPoolService _keys;
    private readonly IModelStore _models;
    private readonly ICodexReadinessStore _readiness;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private string _localToken = string.Empty;
    private WebApplication? _app;

    public LocalGatewayService(
        IProviderManager providers,
        IKeyPoolService keys,
        IModelStore models,
        ICodexReadinessStore readiness,
        ICodexSessionModelReader sessionModels,
        IHttpClientFactory httpClientFactory)
    {
        _providers = providers;
        _keys = keys;
        _models = models;
        _readiness = readiness;
        _sessionModels = sessionModels;
        _httpClientFactory = httpClientFactory;
    }

    public bool IsRunning => _app is not null;

    public event Action<string>? LogMessage;

    /// <inheritdoc cref="IGatewayService.TurnContinued"/>
    public event Action<string, string>? TurnContinued;

    /// <summary>One line per interesting request; logging must never break the gateway.</summary>
    private void Log(string message)
    {
        try
        {
            LogMessage?.Invoke(message);
        }
        catch
        {
            // ignored on purpose
        }
    }

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

            // A stable loopback port + persisted token keep the Codex Desktop overlay
            // (~/.codex/config.toml base_url/bearer) valid across app restarts — an ephemeral
            // port or a fresh token every run would strand Codex Desktop on a dead gateway and
            // silently drop it back to the account model list.
            _localToken = LoadOrCreateLocalToken();
            WebApplication? app = null;
            foreach (var url in new[] { $"http://127.0.0.1:{DefaultGatewayPort}", "http://127.0.0.1:0" })
            {
                var builder = WebApplication.CreateSlimBuilder();
                builder.WebHost.UseUrls(url);
                builder.WebHost.ConfigureKestrel(options =>
                {
                    options.Limits.MaxRequestBodySize = MaxRequestBodySize;
                    options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15);
                });

                var candidate = builder.Build();
                MapEndpoints(candidate);

                try
                {
                    await candidate.StartAsync(cancellationToken);
                    app = candidate;
                    break;
                }
                catch (IOException)
                {
                    // Preferred port is taken (e.g. another instance) — fall back to an
                    // ephemeral port so the gateway still comes up.
                    await candidate.DisposeAsync();
                }
            }

            if (app is null)
            {
                throw new InvalidOperationException("Gateway could not bind a loopback address.");
            }

            var address = app.Urls.FirstOrDefault()
                ?? throw new InvalidOperationException("Gateway did not publish a loopback address.");
            Port = new Uri(address).Port;
            _app = app;
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
        var enabled = models
            .Where(x => x.Enabled && x.State == ModelLifecycleState.Enabled)
            .ToArray();

        // Codex may only be offered models that proved they can serve a real Codex request
        // (tools + tool_choice = required) — providers like HHTech list OpenAI/Codex entries
        // renamed with a "claude-" prefix that fail that request. A model that stops answering
        // drops out within one 5-minute readiness tick and returns as soon as it works again;
        // until the first verdicts exist the full enabled set is published so a fresh install
        // never sees an empty picker (CodexCatalogPolicy.SelectPublished).
        var verdicts = await _readiness.GetAllAsync(provider.Id, context.RequestAborted);
        var publishable = CodexCatalogPolicy
            .SelectPublished(enabled.Select(x => x.RemoteId), verdicts, DateTimeOffset.UtcNow)
            .ToHashSet(StringComparer.Ordinal);
        // Image endpoints are never usable by a Codex turn (the probe skips them for the same reason),
        // so they are not offered: they would sit in the picker forever marked "(U)" — never measured,
        // never usable.
        var published = enabled
            .Where(x => publishable.Contains(x.RemoteId) && !ModelCallability.IsImageEndpoint(x.RemoteId))
            .ToArray();

        // Index the picker the way the user reads it: the model that answers fastest first, the
        // slow ones below it, then whatever has no fresh measurement. Codex cannot colour its
        // rows, so the same classification rides along as a one-letter tag (F/N/S/U) in
        // display_name — the only text of a model entry the hub controls.
        var speed = new Dictionary<string, (Callability Callability, int Key, string Tag)>(
            StringComparer.Ordinal);
        foreach (var model in published)
        {
            CompatibilityResult? latest = null;
            try
            {
                latest = await _models.GetLatestCompatibilityAsync(
                    provider.Id, model.RemoteId, context.RequestAborted);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // An unreadable stored result must not hide a model from the picker.
            }

            var callability = ModelCallability.Classify(latest, DateTimeOffset.UtcNow);
            speed[model.RemoteId] = (
                callability,
                ModelCallability.SpeedKey(latest?.FirstByteMs, latest?.TotalMs),
                ModelCallability.StatusTag(callability, latest?.FirstByteMs, latest?.TotalMs));
        }

        var ordered = published
            .OrderBy(x => ModelCallability.SortOrder(speed[x.RemoteId].Callability))
            .ThenBy(x => speed[x.RemoteId].Key)
            .ThenBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // Codex (app-server) gọi /v1/models?client_version=… và cần shape {"models":[{slug,…}]};
        // các client khác vẫn nhận shape OpenAI {"object":"list","data":[…]}.
        if (context.Request.Query.ContainsKey("client_version"))
        {
            // The one line that answers "is Codex reading our catalogue or its own?": this request is
            // Codex asking us for the model list, and this is what we hand back.
            Log(
                $"models: Codex asked (client_version={context.Request.Query["client_version"]}, " +
                $"ua=\"{Trim(context.Request.Headers.UserAgent)}\") -> {ordered.Length} model(s)");
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync(
                CodexModelCatalog.Build(ordered.Select(x => (
                    x.RemoteId,
                    // The model name as stored. A provider prefix was tried while the catalogue was
                    // being dropped for a missing field (2026-09-11) and Codex fell back to its own
                    // account list; with the entry parseable again the picker shows this catalogue
                    // alone, so the name stays clean and the (F/N/S/U/X) tag does the distinguishing.
                    x.DisplayName,
                    x.ContextWindow,
                    speed[x.RemoteId].Tag))),
                context.RequestAborted);
            return;
        }

        await context.Response.WriteAsJsonAsync(
            new
            {
                @object = "list",
                data = ordered
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

        ArraySegment<byte> body;
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

            var isProbe = IsProbeRequest(context);
            attempts++;
            if (selection is not null)
            {
                excluded.Add(selection.Key.Id);
            }

            // Codex re-runs an unfinished turn with the model that turn started on, and it keeps retrying
            // whatever answer comes back — a non-retryable 400 does not stop it either (measured: fifteen
            // requests in twenty seconds after a restart). The only way out of that loop is for the
            // request to succeed, so a model the provider keeps refusing is served for that turn by the
            // fastest model of the same provider that is known to answer. Never silent: a log line and
            // `x-adam-codexhub-fallback` carry what actually served it.
            var outgoing = body;
            if (!isProbe)
            {
                // The model the user picked in Codex is honoured as soon as the model Codex is asking for
                // has been refused once: switching models is how they carry on with the work, and Codex
                // keeps the old model on an unfinished turn. The generic "some model that answers"
                // fallback below waits for a run of refusals, because serving a single hiccup with a
                // different model is not what the user asked for.
                string? fallback = null;
                if (RefusedRecently(provider.Id, modelId))
                {
                    fallback = await PickedModelAsync(provider.Id, modelId, context.RequestAborted);
                }

                if (fallback is null && RefusingRepeatedly(provider.Id, modelId))
                {
                    fallback = await PickFallbackAsync(provider.Id, modelId, context.RequestAborted);
                }

                if (fallback is not null)
                {
                    outgoing = SwapModel(body, fallback);
                    context.Response.Headers["x-adam-codexhub-fallback"] = fallback;
                    TurnContinued?.Invoke(modelId, fallback);
                    if (!_fallbackLogged.TryGetValue($"{provider.Id}/{modelId}", out var seen) ||
                        DateTimeOffset.UtcNow - seen > TimeSpan.FromMinutes(1))
                    {
                        _fallbackLogged[$"{provider.Id}/{modelId}"] = DateTimeOffset.UtcNow;
                        Log(
                            $"{modelId} is not answering — continuing this turn with {fallback}. The turn is " +
                            "pinned to the model it started on; the next turn runs on the model picked in " +
                            "Codex.");
                    }
                }
            }

            // The client's identity prompt ("You are Codex, an agent based on GPT-6 …") is replaced with an
            // honest harness framing: a model from another family reads the claim as a false system prompt
            // and refuses the tools it is offered (measured 2026-09-11 with a served Claude model). This runs
            // after the fallback swap so a continued turn is neutralised too.
            outgoing = HarnessPrompt.Neutralise(outgoing, out var replacedClaim);
            if (replacedClaim is not null &&
                (!_fallbackLogged.TryGetValue($"harness/{provider.Id}", out var claimedAt) ||
                 DateTimeOffset.UtcNow - claimedAt > TimeSpan.FromMinutes(5)))
            {
                _fallbackLogged[$"harness/{provider.Id}"] = DateTimeOffset.UtcNow;
                Log(
                    $"replaced the client's identity prompt before forwarding (was \"{replacedClaim}\"). Models " +
                    "from another family read that claim as a false system prompt and stop using the tools.");
            }

            // What the model is actually offered decides whether it can work: a session whose request carries
            // no tool list answers "I have no exec here" no matter which model serves it. Logged once per
            // model every five minutes so a checkout of the tool surface is one grep away.
            if (!_fallbackLogged.TryGetValue($"tools/{modelId}", out var toolsAt) ||
                DateTimeOffset.UtcNow - toolsAt > TimeSpan.FromMinutes(5))
            {
                _fallbackLogged[$"tools/{modelId}"] = DateTimeOffset.UtcNow;
                Log($"tools offered to {modelId}: {DescribeTools(outgoing)}");
            }

            using var upstreamRequest = CreateUpstreamRequest(
                context.Request,
                provider,
                endpoint,
                outgoing,
                selection?.Secret);

            HttpResponseMessage upstreamResponse;
            // A background probe is a request we make to measure models, not the user's own traffic.
            // When a probe hits a 429/5xx the provider is throttling *us*, and parking the key for it
            // takes the key away from the session the user is actually typing in — that is how a
            // working Codex session turned into "Reconnecting … No usable API key remains".
            if (!isProbe)
            {
                // The background passes must know the user is here: they spend the same key.
                UserTrafficClock.Record(provider.Id);
            }

            // Every turn is logged with the model Codex actually asked for. "I picked claude but it
            // answered about luna" cannot be settled from the outside: the log tells apart Codex
            // sending luna from the provider answering about a different model than we sent.
            async Task RelayLoggedAsync(HttpResponseMessage response)
            {
                var status = (int)response.StatusCode;
                if (status >= 400)
                {
                    // Safe to buffer: an error body is a small JSON document, and its text is the clue
                    // ("Model \"gpt-5.6-luna\" đang hết người khả dụng …").
                    var text = await response.Content.ReadAsStringAsync(context.RequestAborted);
                    Log($"responses: model={modelId} provider={provider.Id} -> {status} {Snippet(text)}");

                    // A model the provider keeps refusing, asked again and again, is Codex retrying an
                    // unfinished turn. A 503/429 invites another retry, so the loop never ends and the
                    // model the user picked never gets a turn. When there is a model to continue with,
                    // the next attempt is served by it (see the fallback above) and the client is not
                    // poisoned with a non-retryable answer; when there is nothing to continue with, say
                    // so plainly instead of looping.
                    if (!isProbe && status is 429 or 503 && NoteRefusal(provider.Id, modelId))
                    {
                        var alternative = await PickFallbackAsync(provider.Id, modelId, context.RequestAborted);
                        if (alternative is null)
                        {
                            await WriteErrorAsync(
                                context,
                                StatusCodes.Status400BadRequest,
                                $"Model '{modelId}' cannot serve this request right now — {Snippet(text)} " +
                                "Codex has already retried this turn several times; pick another model, or start " +
                                "a new chat, to continue.",
                                "adam_codexhub_model_unavailable");
                            return;
                        }
                    }

                    response.Content = new StringContent(text, Encoding.UTF8, "application/json");
                }
                else
                {
                    Log($"responses: model={modelId} provider={provider.Id} -> {status}");
                }

                await RelayResponseAsync(context, response);
            }

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
                    if (!isProbe)
                    {
                        // Same reasoning as a 5xx: the connection failed, the key did not.
                        await WriteErrorAsync(
                            context,
                            StatusCodes.Status502BadGateway,
                            $"Provider '{provider.Name}' could not be reached: {ex.Message}",
                            "adam_codexhub_provider_unreachable");
                        return;
                    }

                    continue;
                }

                break;
            }

            using (upstreamResponse)
            {
                // A 5xx (or a dropped connection) is the provider or the network being busy, not the
                // key being wrong. Marking the key for it took the provider out of service for the
                // rest of the session: one 503 from upstream became "No usable API key remains" and
                // Codex showed "Reconnecting …" while it switched models (2026-09-11). Hand the
                // upstream answer straight to the client instead — Codex retries a 503 by itself —
                // and leave the key's health to the answers that really are about the key (401/402/429).
                if ((int)upstreamResponse.StatusCode >= 500)
                {
                    await RelayLoggedAsync(upstreamResponse);
                    return;
                }

                if (selection is not null && IsRetryable(upstreamResponse.StatusCode))
                {
                    var failure = MapFailure(upstreamResponse);
                    lastFailure = failure.Message;

                    // A 429 is the provider asking for less traffic, not a broken key. Fail over to
                    // another key when there is one; when this is the last key, hand the 429 back —
                    // Codex backs off and retries — and park only the probes, which are the requests
                    // nobody is waiting for. Marking the last key is what left the session reading
                    // "No usable API key remains" (2026-09-11).
                    if (upstreamResponse.StatusCode == HttpStatusCode.TooManyRequests)
                    {
                        ProviderBackoff.Park(provider.Id, failure.Cooldown ?? TimeSpan.FromSeconds(60));

                        var tried = new HashSet<string>(StringComparer.Ordinal) { selection.Key.Id };
                        var alternative = await _keys.GetActiveAsync(
                            provider.Id,
                            tried,
                            context.RequestAborted);
                        if (alternative is null || isProbe)
                        {
                            await RelayLoggedAsync(upstreamResponse);
                            return;
                        }
                    }

                    if (!isProbe)
                    {
                        await _keys.MarkFailureAsync(
                            selection.Key.Id,
                            failure.Health,
                            failure.Message,
                            failure.Cooldown,
                            context.RequestAborted);
                    }

                    continue;
                }

                if (selection is not null && upstreamResponse.IsSuccessStatusCode)
                {
                    await _keys.MarkSuccessAsync(selection.Key.Id, context.RequestAborted);
                }

                await RelayLoggedAsync(upstreamResponse);
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

    private static string LoadOrCreateLocalToken()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AdamCodexHub",
            "data");
        var path = Path.Combine(directory, "gateway-token");

        try
        {
            if (File.Exists(path))
            {
                var existing = File.ReadAllText(path).Trim();
                if (existing.Length == 64)
                {
                    return existing;
                }
            }
        }
        catch (IOException)
        {
            // Fall through and regenerate.
        }

        var token = CreateLocalToken();
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(path, token);
        }
        catch (IOException)
        {
            // Persisting is best-effort; a fresh in-memory token still keeps this run working.
        }

        return token;
    }

    private static bool IsLoopback(HttpContext context) =>
        context.Connection.RemoteIpAddress is { } address && IPAddress.IsLoopback(address);

    /// <summary>
    /// Reads the turn into memory, once. It cannot be streamed straight through: a turn may be retried
    /// with the next key, or re-sent under a different model when the picked one is refused, and both
    /// need the same bytes again. What it must not do is pay for those bytes twice — a naive
    /// <c>MemoryStream</c> + <c>ToArray()</c> costs 2× the body at peak plus the doubling it did while
    /// growing, which on a 64 MB ceiling is real memory. Sizing the buffer from Content-Length and
    /// handing back its own array removes both.
    /// </summary>
    private static async Task<ArraySegment<byte>> ReadRequestBodyAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        if (request.ContentLength > MaxRequestBodySize)
        {
            throw new BadHttpRequestException(
                $"Request body exceeds the {MaxRequestBodySize / (1024 * 1024)} MB gateway limit.",
                StatusCodes.Status413PayloadTooLarge);
        }

        var capacity = request.ContentLength is > 0 and <= MaxRequestBodySize
            ? (int)request.ContentLength.Value
            : 0;
        using var buffer = new MemoryStream(capacity);
        await request.Body.CopyToAsync(buffer, cancellationToken);
        if (buffer.Length > MaxRequestBodySize)
        {
            throw new BadHttpRequestException(
                $"Request body exceeds the {MaxRequestBodySize / (1024 * 1024)} MB gateway limit.",
                StatusCodes.Status413PayloadTooLarge);
        }

        // GetBuffer avoids the second full-size copy ToArray would make; the segment carries the real
        // length so the slack at the end is never sent.
        return new ArraySegment<byte>(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private static HttpRequestMessage CreateUpstreamRequest(
        HttpRequest incoming,
        ProviderProfile provider,
        string endpoint,
        ArraySegment<byte> body,
        string? apiKey)
    {
        var uri = BuildUpstreamUri(provider, endpoint, apiKey);
        var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new ByteArrayContent(body.Array!, body.Offset, body.Count)
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

    /// <summary>First line of a provider error body, short enough for one log line.</summary>
    private static string Snippet(string text)
    {
        var single = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return single.Length <= 240 ? single : single[..240] + "…";
    }

    /// <summary>
    /// Says out loud when one model is refused again and again: that is Codex retrying an unfinished
    /// turn with the model that turn started on, and changing models in the UI does not touch it.
    /// Returns true the moment the count crosses the line, for the log line and the last-resort answer.
    /// </summary>
    private bool NoteRefusal(string providerId, string modelId)
    {
        var key = $"{providerId}/{modelId}";
        var now = DateTimeOffset.UtcNow;
        var state = _refusals.AddOrUpdate(
            key,
            _ => (now, now, 1),
            (_, previous) => now - previous.Last > StuckTurnWindow
                ? (now, now, 1)
                : (previous.First, now, previous.Count + 1));

        if (state.Count != StuckTurnRefusals)
        {
            return false;
        }

        Log(
            $"{modelId} has been refused {state.Count}× in {StuckTurnWindow.TotalSeconds:0}s: Codex is " +
            "retrying a turn that started on this model, so picking another model in the UI will not " +
            "change these requests.");
        return true;
    }

    /// <summary>True while a model is being refused over and over, i.e. Codex is stuck on one turn.</summary>
    private bool RefusingRepeatedly(string providerId, string modelId) =>
        RefusedRecently(providerId, modelId, StuckTurnRefusals);

    /// <summary>True when the model was refused at least <paramref name="atLeast"/> times inside the window.</summary>
    private bool RefusedRecently(string providerId, string modelId, int atLeast = 1) =>
        _refusals.TryGetValue($"{providerId}/{modelId}", out var state) &&
        state.Count >= atLeast &&
        DateTimeOffset.UtcNow - state.Last <= StuckTurnWindow;

    /// <summary>
    /// The model to serve a stuck turn with: first the model the user just picked in Codex — switching
    /// models means carrying on with the work, and Codex keeps the old model for an unfinished turn — then
    /// the same provider's fastest model that is known to answer. Null when there is nothing to use.
    /// </summary>
    private async Task<string?> PickFallbackAsync(
        string providerId,
        string refusedModelId,
        CancellationToken cancellationToken)
    {
        var picked = await PickedModelAsync(providerId, refusedModelId, cancellationToken);
        if (picked is not null)
        {
            return picked;
        }

        var models = await _models.GetAllAsync(providerId, cancellationToken);
        string? best = null;
        var bestTotal = int.MaxValue;

        foreach (var model in models)
        {
            if (!model.Enabled || model.State != ModelLifecycleState.Enabled)
            {
                continue;
            }

            if (string.Equals(model.RemoteId, refusedModelId, StringComparison.OrdinalIgnoreCase) ||
                ModelCallability.IsImageEndpoint(model.RemoteId))
            {
                continue;
            }

            CompatibilityResult? latest;
            try
            {
                latest = await _models.GetLatestCompatibilityAsync(providerId, model.RemoteId, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                continue;
            }

            if (latest is null || latest.Score <= 0)
            {
                continue;
            }

            var total = latest.TotalMs ?? int.MaxValue;
            if (total < bestTotal)
            {
                bestTotal = total;
                best = model.RemoteId;
            }
        }

        return best;
    }

    /// <summary>Same request body with a different model — only ever used for a stuck turn.</summary>
    private static ArraySegment<byte> SwapModel(ArraySegment<byte> body, string model)
    {
        if (JsonNode.Parse(Encoding.UTF8.GetString(body.Array!, body.Offset, body.Count))
            is not JsonObject json)
        {
            return body;
        }

        json["model"] = model;
        return Encoding.UTF8.GetBytes(json.ToJsonString());
    }

    /// <summary>The tool names a request offers the model — "none" means the session cannot work.</summary>
    private static string DescribeTools(ArraySegment<byte> body)
    {
        try
        {
            using var json = JsonDocument.Parse(new ReadOnlyMemory<byte>(body.Array!, body.Offset, body.Count));
            if (!json.RootElement.TryGetProperty("tools", out var tools) ||
                tools.ValueKind != JsonValueKind.Array)
            {
                return "none";
            }

            var names = new List<string>();
            foreach (var tool in tools.EnumerateArray())
            {
                if (tool.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var name = tool.TryGetProperty("name", out var named) && named.ValueKind == JsonValueKind.String
                    ? named.GetString()
                    : tool.TryGetProperty("type", out var typed) && typed.ValueKind == JsonValueKind.String
                        ? typed.GetString()
                        : null;
                if (!string.IsNullOrWhiteSpace(name))
                {
                    names.Add(name);
                }
            }

            return names.Count == 0 ? "none" : $"{names.Count} ({string.Join(", ", names.Take(12))})";
        }
        catch (JsonException)
        {
            return "unreadable";
        }
    }

    /// <summary>
    /// The model the user picked inside Codex, when it is a real alternative: enabled, not an image
    /// endpoint, not the model being refused, and known to answer. Codex's picker never writes
    /// <c>config.toml</c>, so this value comes from Codex's own session state — and it is what makes
    /// "I switched the model, carry on with my work" actually happen for a turn Codex pinned to the old
    /// model.
    /// </summary>
    private async Task<string?> PickedModelAsync(
        string providerId,
        string refusedModelId,
        CancellationToken cancellationToken)
    {
        string? picked;
        try
        {
            picked = _sessionModels.Read()?.ModelId;
        }
        catch
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(picked) ||
            string.Equals(picked, refusedModelId, StringComparison.OrdinalIgnoreCase) ||
            ModelCallability.IsImageEndpoint(picked))
        {
            return null;
        }

        try
        {
            var model = await _models.GetAsync(providerId, picked, cancellationToken);
            if (model is not { Enabled: true } || model.State != ModelLifecycleState.Enabled)
            {
                return null;
            }

            var latest = await _models.GetLatestCompatibilityAsync(providerId, picked, cancellationToken);
            if (latest is null || latest.Score <= 0)
            {
                return null;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }

        Log($"using the model picked in Codex ({picked}) for this turn instead of {refusedModelId}");
        return picked;
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
