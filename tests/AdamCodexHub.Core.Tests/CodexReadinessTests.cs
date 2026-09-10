using System.Net;
using System.Text;
using AdamCodexHub.Core.Domain;
using AdamCodexHub.Core.Interfaces;
using AdamCodexHub.Core.Services;
using AdamCodexHub.Providers;
using Xunit;

namespace AdamCodexHub.Core.Tests;

/// <summary>
/// The rule that keeps Codex Desktop's model picker honest: only models with a fresh Ready verdict
/// from the Codex-shaped probe may be published, and the probe itself must accept a model only
/// when it really answers that request with a tool call.
/// </summary>
public sealed class CodexReadinessTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SelectPublished_KeepsOnlyFreshReadyModels()
    {
        var verdicts = new[]
        {
            Verdict("claude-sonnet-5", ready: true, checkedAt: Now.AddMinutes(-5)),
            Verdict("claude-5-5", ready: false, checkedAt: Now.AddMinutes(-1)),
        };

        var published = CodexCatalogPolicy.SelectPublished(
            new[] { "claude-sonnet-5", "claude-5-5", "never-checked" },
            verdicts,
            Now);

        Assert.Equal(new[] { "claude-sonnet-5" }, published);
    }

    [Fact]
    public void SelectPublished_DropsVerdictsThatWentStale()
    {
        var stale = Verdict("old-model", ready: true, checkedAt: Now - CodexReadiness.FreshFor - TimeSpan.FromMinutes(1));
        var fresh = Verdict("new-model", ready: true, checkedAt: Now.AddMinutes(-5));

        var published = CodexCatalogPolicy.SelectPublished(
            new[] { "old-model", "new-model" },
            new[] { stale, fresh },
            Now);

        Assert.Equal(new[] { "new-model" }, published);
    }

    [Fact]
    public void SelectPublished_FallsBackToEveryEnabledModelBeforeTheFirstVerdict()
    {
        // A fresh install (or a provider that was just switched) has no verdicts at all: the
        // picker must still list the enabled models instead of going empty.
        var published = CodexCatalogPolicy.SelectPublished(
            new[] { "a", "b" },
            Array.Empty<CodexReadiness>(),
            Now);

        Assert.Equal(new[] { "a", "b" }, published);
    }

    [Fact]
    public void SelectPublished_IgnoresVerdictsOfModelsThatAreNotEnabled()
    {
        var published = CodexCatalogPolicy.SelectPublished(
            new[] { "enabled" },
            new[] { Verdict("disabled-model", ready: true, checkedAt: Now.AddMinutes(-1)) },
            Now);

        Assert.Equal(new[] { "enabled" }, published);
    }

    [Fact]
    public void IsPublishable_RequiresAFreshReadyVerdict()
    {
        var verdicts = new[] { Verdict("model", ready: true, checkedAt: Now.AddMinutes(-2)) };

        Assert.True(CodexCatalogPolicy.IsPublishable("model", verdicts, Now));
        Assert.False(CodexCatalogPolicy.IsPublishable("other", verdicts, Now));
        Assert.False(CodexCatalogPolicy.IsPublishable("model", verdicts, Now + CodexReadiness.FreshFor + TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public async Task Checker_IsReadyOnlyWhenTheModelCallsTheToolOnEveryAttempt()
    {
        var handler = new StubHandler(_ => ToolCallResponse());
        var checker = new CodexReadinessChecker(new StubGateway(), new StubHttpClientFactory(handler));

        var verdict = await checker.CheckAsync("hhtech", "claude-sonnet-5");

        Assert.True(verdict.Ready);
        Assert.Equal(CodexReadinessChecker.Attempts, handler.Requests.Count);
        Assert.Null(verdict.Detail);
    }

    [Fact]
    public async Task Checker_SendsTheCodexShapedRequestThroughTheGateway()
    {
        var handler = new StubHandler(_ => ToolCallResponse());
        var checker = new CodexReadinessChecker(new StubGateway(), new StubHttpClientFactory(handler));

        await checker.CheckAsync("hhtech", "claude-sonnet-5");

        Assert.Equal(CodexReadinessChecker.Attempts, handler.Requests.Count);
        var request = handler.Requests[0];
        Assert.Equal("http://127.0.0.1:20129/v1/responses", request.Uri);
        Assert.Equal("Bearer", request.AuthorizationScheme);
        Assert.Contains("\"tool_choice\":\"required\"", request.Body, StringComparison.Ordinal);
        Assert.Contains("\"name\":\"shell\"", request.Body, StringComparison.Ordinal);
        Assert.Contains("\"claude-sonnet-5\"", request.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Checker_IsNotReadyWhenTheModelAnswersWithoutCallingTheTool()
    {
        var handler = new StubHandler(_ => Json("""{"output":[{"type":"message","content":[]}]}"""));
        var checker = new CodexReadinessChecker(new StubGateway(), new StubHttpClientFactory(handler));

        var verdict = await checker.CheckAsync("hhtech", "claude-5-5");

        Assert.False(verdict.Ready);
        Assert.Contains("did not call the tool", verdict.Detail ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Checker_IsNotReadyWhenTheProviderErrors()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var checker = new CodexReadinessChecker(new StubGateway(), new StubHttpClientFactory(handler));

        var verdict = await checker.CheckAsync("hhtech", "claude-6-astra");

        Assert.False(verdict.Ready);
        Assert.Contains("HTTP 500", verdict.Detail ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Checker_DoesNotProbeWhenTheGatewayIsDown()
    {
        var handler = new StubHandler(_ => ToolCallResponse());
        var checker = new CodexReadinessChecker(
            new StubGateway { IsRunning = false },
            new StubHttpClientFactory(handler));

        var verdict = await checker.CheckAsync("hhtech", "claude-sonnet-5");

        Assert.False(verdict.Ready);
        Assert.Equal("gateway not running", verdict.Detail);
        Assert.Empty(handler.Requests);
    }

    private static CodexReadiness Verdict(string modelId, bool ready, DateTimeOffset checkedAt) =>
        new("hhtech", modelId, ready, checkedAt, ready ? 1200 : null, ready ? null : "HTTP 500");

    private static HttpResponseMessage ToolCallResponse() =>
        Json("""{"output":[{"type":"function_call","name":"shell","arguments":"{\"command\":\"echo ok\"}"}]}""");

    private static HttpResponseMessage Json(string payload) =>
        new(HttpStatusCode.OK) { Content = new StringContent(payload, Encoding.UTF8, "application/json") };

    private sealed class StubGateway : IGatewayService
    {
        public bool IsRunning { get; init; } = true;
        public int Port { get; init; } = 20129;
        public string LocalToken { get; init; } = "test-token";
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public StubHttpClientFactory(HttpMessageHandler handler)
        {
            _handler = handler;
        }

        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        public List<CapturedRequest> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new CapturedRequest(
                request.RequestUri?.ToString() ?? string.Empty,
                request.Headers.Authorization?.Scheme,
                body));
            return _responder(request);
        }
    }

    private sealed record CapturedRequest(string Uri, string? AuthorizationScheme, string Body);
}
