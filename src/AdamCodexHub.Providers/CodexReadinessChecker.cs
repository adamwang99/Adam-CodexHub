using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AdamCodexHub.Core.Domain;
using AdamCodexHub.Core.Interfaces;

namespace AdamCodexHub.Providers;

/// <summary>
/// Runs the "would Codex actually work with this model?" probe.
///
/// The request is deliberately the shape Codex Desktop sends — instructions, a user turn and a
/// single function tool with <c>tool_choice = "required"</c> — and it travels through the hub's
/// own local gateway, so the probe exercises the exact path Codex uses (gateway → adapter →
/// provider) instead of a private shortcut. A verdict is <see cref="CodexReadiness.Ready"/> only
/// when <em>every</em> attempt returns HTTP 200 with an actual <c>function_call</c>; one failed or
/// tool-less answer marks the model not ready, and the 5-minute refresher retries it shortly.
/// </summary>
public sealed class CodexReadinessChecker : ICodexReadinessChecker
{
    /// <summary>Per-attempt budget. Slow "Claude 4.7 class" models need room for a first byte.</summary>
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(150);

    /// <summary>Attempts per check. Both must succeed for a Ready verdict.</summary>
    public const int Attempts = 2;

    private const string ToolName = "shell";

    private readonly IGatewayService _gateway;
    private readonly IHttpClientFactory _httpClientFactory;

    public CodexReadinessChecker(IGatewayService gateway, IHttpClientFactory httpClientFactory)
    {
        _gateway = gateway;
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>Checks one model and never throws: transport failures become a not-ready verdict.</summary>
    public async Task<CodexReadiness> CheckAsync(
        string providerId,
        string modelId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        var total = Stopwatch.StartNew();
        string? lastDetail = null;
        int? firstLatency = null;

        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_gateway.IsRunning || _gateway.Port <= 0 || string.IsNullOrEmpty(_gateway.LocalToken))
            {
                return new CodexReadiness(
                    providerId,
                    modelId,
                    Ready: false,
                    CheckedAt: DateTimeOffset.UtcNow,
                    LatencyMs: null,
                    Detail: "gateway not running");
            }

            var outcome = await TryOnceAsync(modelId, attempt, cancellationToken).ConfigureAwait(false);
            firstLatency ??= outcome.LatencyMs;
            if (!outcome.Ok)
            {
                lastDetail = outcome.Detail;
                break;
            }
        }

        var ready = lastDetail is null;
        return new CodexReadiness(
            providerId,
            modelId,
            ready,
            DateTimeOffset.UtcNow,
            ready ? firstLatency : null,
            ready ? null : lastDetail);
    }

    private async Task<(bool Ok, int? LatencyMs, string? Detail)> TryOnceAsync(
        string modelId,
        int attempt,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(AttemptTimeout);
        var elapsed = Stopwatch.StartNew();

        try
        {
            var client = _httpClientFactory.CreateClient("codex-readiness");
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"http://127.0.0.1:{_gateway.Port}/v1/responses")
            {
                Content = new StringContent(BuildBody(modelId), Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _gateway.LocalToken);

            using var response = await client
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token)
                .ConfigureAwait(false);
            var payload = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return (false, null, $"HTTP {(int)response.StatusCode} on attempt {attempt}");
            }

            var toolCall = HasToolCall(payload);
            return toolCall
                ? (true, (int)elapsed.ElapsedMilliseconds, null)
                : (false, (int)elapsed.ElapsedMilliseconds, "HTTP 200 but the model did not call the tool");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (false, null, $"timed out after {AttemptTimeout.TotalSeconds:0}s on attempt {attempt}");
        }
        catch (Exception ex)
        {
            return (false, null, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>The minimal Codex turn: one tool, mandatory, plus a user instruction to use it.</summary>
    private static string BuildBody(string modelId)
    {
        var body = new
        {
            model = modelId,
            stream = false,
            instructions = "You are a coding agent. Use the provided tool when the user asks.",
            tool_choice = "required",
            tools = new object[]
            {
                new
                {
                    type = "function",
                    name = ToolName,
                    description = "Run a shell command.",
                    parameters = new
                    {
                        type = "object",
                        properties = new { command = new { type = "string" } },
                        required = new[] { "command" }
                    }
                }
            },
            input = new object[]
            {
                new
                {
                    type = "message",
                    role = "user",
                    content = new object[]
                    {
                        new { type = "input_text", text = "Call the shell tool with command 'echo ok'. Do not explain." }
                    }
                }
            }
        };

        return JsonSerializer.Serialize(body);
    }

    /// <summary>True when the response carries a function call (any shape the adapters emit).</summary>
    private static bool HasToolCall(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (root.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in output.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object &&
                        item.TryGetProperty("type", out var type) &&
                        type.ValueKind == JsonValueKind.String &&
                        type.GetString() is "function_call" or "function_call_output" or "tool_call")
                    {
                        return true;
                    }
                }
            }

            // Chat-completions style payloads (some providers answer the responses route that way).
            if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
            {
                foreach (var choice in choices.EnumerateArray())
                {
                    if (choice.ValueKind == JsonValueKind.Object &&
                        choice.TryGetProperty("message", out var message) &&
                        message.ValueKind == JsonValueKind.Object &&
                        message.TryGetProperty("tool_calls", out var calls) &&
                        calls.ValueKind == JsonValueKind.Array &&
                        calls.GetArrayLength() > 0)
                    {
                        return true;
                    }
                }
            }

            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
