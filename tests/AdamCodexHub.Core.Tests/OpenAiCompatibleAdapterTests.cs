using System.Net;
using System.Net.Http.Headers;
using System.Text;
using AdamCodexHub.Core.Domain;
using AdamCodexHub.Providers.Adapters;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AdamCodexHub.Core.Tests;

public sealed class OpenAiCompatibleAdapterTests
{
    [Fact]
    public async Task ListModelsParsesDataDeduplicatesAndAppliesHeaders()
    {
        var handler = new RecordingHandler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://api.example.test/v1/models", request.RequestUri?.ToString());
            Assert.Equal(new AuthenticationHeaderValue("Bearer", "secret-key"), request.Headers.Authorization);
            Assert.True(request.Headers.TryGetValues("X-Client", out var values));
            Assert.Contains("Adam CodexHub Tests", values);

            return Task.FromResult(Json(HttpStatusCode.OK, """
                {"data":[{"id":"model-b"},{"id":"model-a"},{"id":"MODEL-A"},{"id":""}]}
                """));
        });
        var adapter = CreateAdapter(handler);

        var models = await adapter.ListModelsAsync(
            CreateProvider(extraHeaders: new Dictionary<string, string>
            {
                ["X-Client"] = "Adam CodexHub Tests"
            }),
            "secret-key");

        Assert.Equal(new[] { "model-a", "model-b" }, models.Select(x => x.RemoteId));
        Assert.All(models, model =>
        {
            Assert.Equal("provider", model.ProviderId);
            Assert.Equal(ModelLifecycleState.Discovered, model.State);
            Assert.NotNull(model.LastSeenAt);
        });
    }

    [Fact]
    public async Task ListModelsParsesAlternativeModelsAndNameShape()
    {
        var adapter = CreateAdapter(new RecordingHandler(_ => Task.FromResult(Json(
            HttpStatusCode.OK,
            """
            {"models":[{"name":"gemini-a"},{"id":"gemini-b"}]}
            """))));

        var models = await adapter.ListModelsAsync(CreateProvider(), "key");

        Assert.Equal(new[] { "gemini-a", "gemini-b" }, models.Select(x => x.RemoteId));
    }

    [Fact]
    public async Task ProbeReturnsFailureInsteadOfThrowingOnUnauthorized()
    {
        var adapter = CreateAdapter(new RecordingHandler(_ => Task.FromResult(Json(
            HttpStatusCode.Unauthorized,
            "{\"error\":\"invalid key\"}"))));

        var result = await adapter.ProbeAsync(CreateProvider(), "bad-key");

        Assert.False(result.Success);
        Assert.Contains("401", result.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(result.SupportedEndpoints);
    }

    [Fact]
    public async Task TestModelProbesResponsesStreamingToolsAndStructuredJson()
    {
        var requestBodies = new List<string>();
        var handler = new RecordingHandler(async request =>
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync();
            requestBodies.Add(body);

            if (body.Contains("\"stream\":true", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("data: {\"type\":\"response.output_text.delta\"}\n\n", Encoding.UTF8, "text/event-stream")
                };
            }

            if (body.Contains("\"tools\"", StringComparison.Ordinal))
            {
                return Json(HttpStatusCode.OK, "{\"tool_calls\":[{\"id\":\"call-1\"}]}");
            }

            return Json(HttpStatusCode.OK, "{\"output_text\":\"OK\"}");
        });
        var adapter = CreateAdapter(handler);
        var provider = CreateProvider(
            responsesEndpoint: "/responses",
            chatEndpoint: null);

        var result = await adapter.TestModelAsync(provider, "model-a", "secret-key");

        Assert.True(result.Text);
        Assert.True(result.Responses);
        Assert.False(result.ChatCompletions);
        Assert.True(result.Streaming);
        Assert.True(result.ToolCalling);
        Assert.True(result.StructuredJson);
        Assert.False(result.Vision);
        Assert.Equal(90, result.Score);
        Assert.Equal(4, requestBodies.Count);
    }

    [Fact]
    public async Task ResponsesAdapterDelegatesToCompatibleAdapter()
    {
        var handler = new RecordingHandler(_ => Task.FromResult(Json(
            HttpStatusCode.OK,
            "{\"data\":[{\"id\":\"delegated-model\"}]}")));
        var compatible = CreateAdapter(handler);
        var adapter = new OpenAiResponsesAdapter(compatible);

        var models = await adapter.ListModelsAsync(CreateProvider(), "key");

        Assert.Equal("openai-responses", adapter.AdapterId);
        Assert.Single(models);
        Assert.Equal("delegated-model", models[0].RemoteId);
        Assert.Single(handler.Requests);
    }

    private static OpenAiCompatibleAdapter CreateAdapter(HttpMessageHandler handler)
    {
        var services = new ServiceCollection();
        services.AddHttpClient(nameof(OpenAiCompatibleAdapter))
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        var provider = services.BuildServiceProvider();
        return new OpenAiCompatibleAdapter(provider.GetRequiredService<IHttpClientFactory>());
    }

    private static ProviderProfile CreateProvider(
        string? responsesEndpoint = "/responses",
        string? chatEndpoint = "/chat/completions",
        IReadOnlyDictionary<string, string>? extraHeaders = null) => new()
        {
            Id = "provider",
            Name = "Provider",
            Adapter = "openai-compatible",
            BaseUrl = "https://api.example.test/v1",
            AuthType = "bearer",
            AuthHeaderName = "Authorization",
            ModelsEndpoint = "/models",
            ResponsesEndpoint = responsesEndpoint,
            ChatCompletionsEndpoint = chatEndpoint,
            ExtraHeaders = extraHeaders ?? new Dictionary<string, string>()
        };

    private static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _respond;

        public RecordingHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond)
        {
            _respond = respond;
        }

        public List<(HttpMethod Method, Uri? Uri)> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add((request.Method, request.RequestUri));
            return await _respond(request);
        }
    }
}
