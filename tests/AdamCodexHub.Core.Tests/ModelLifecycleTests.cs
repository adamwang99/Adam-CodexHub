using System.Net;
using System.Net.Http.Headers;
using AdamCodexHub.Core.Domain;
using AdamCodexHub.Infrastructure.Database;
using AdamCodexHub.Infrastructure.Models;
using AdamCodexHub.Infrastructure.Paths;
using AdamCodexHub.Providers.Adapters;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AdamCodexHub.Core.Tests;

public sealed class ModelLifecycleTests
{
    [Fact]
    public async Task ModelMustBeVerifiedBeforeEnableAndStatePersists()
    {
        await using var fixture = new ModelFixture();
        await fixture.Store.UpsertAsync(CreateModel("model-a"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Store.SetEnabledAsync("provider", "model-a", true));

        var result = new CompatibilityResult
        {
            ProviderId = "provider",
            ModelId = "model-a",
            VerifiedAt = DateTimeOffset.UtcNow,
            Text = true,
            Responses = true,
            Streaming = true,
            Score = 60
        };
        await fixture.Store.SaveCompatibilityAsync(result);
        await fixture.Store.SetEnabledAsync("provider", "model-a", true);

        var restarted = fixture.CreateStore();
        var restored = await restarted.GetAsync("provider", "model-a");
        Assert.NotNull(restored);
        Assert.True(restored.Enabled);
        Assert.Equal(ModelLifecycleState.Enabled, restored.State);
        Assert.Equal(60, restored.CompatibilityScore);
        Assert.Equal(result.VerifiedAt, (await restarted.GetLatestCompatibilityAsync("provider", "model-a"))?.VerifiedAt);
    }

    [Fact]
    public async Task MissingRemoteModelBecomesUnavailableInsteadOfBeingDeleted()
    {
        await using var fixture = new ModelFixture();
        await fixture.Store.UpsertAsync(CreateModel("still-present"));
        await fixture.Store.UpsertAsync(CreateModel("missing"));

        await fixture.Store.MarkUnavailableExceptAsync(
            "provider",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "still-present" });

        var missing = await fixture.Store.GetAsync("provider", "missing");
        Assert.NotNull(missing);
        Assert.False(missing.Enabled);
        Assert.Equal(ModelLifecycleState.Unavailable, missing.State);
    }

    [Fact]
    public async Task CompatibilityProbeVerifiesRealResponseStreamingToolsAndJson()
    {
        var handler = new CompatibilityHandler();
        var adapter = new OpenAiCompatibleAdapter(new FakeHttpClientFactory(handler));
        var provider = new ProviderProfile
        {
            Id = "provider",
            Name = "Provider",
            Adapter = "openai-compatible",
            BaseUrl = "https://api.example.test/v1",
            AuthType = "bearer",
            ResponsesEndpoint = "/responses",
            ChatCompletionsEndpoint = "/chat/completions"
        };

        var result = await adapter.TestModelAsync(provider, "model-a", "test-secret");

        Assert.True(result.Text);
        Assert.True(result.Responses);
        Assert.True(result.ChatCompletions);
        Assert.True(result.Streaming);
        Assert.True(result.ToolCalling);
        Assert.True(result.StructuredJson);
        Assert.Equal(100, result.Score);
        Assert.Equal(5, handler.RequestCount);
        Assert.True(handler.AllRequestsAuthenticated);
    }

    private static ModelDescriptor CreateModel(string id) => new()
    {
        ProviderId = "provider",
        RemoteId = id,
        DisplayName = id,
        State = ModelLifecycleState.Discovered,
        LastSeenAt = DateTimeOffset.UtcNow
    };

    private sealed class ModelFixture : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "AdamCodexHub.Tests",
            Guid.NewGuid().ToString("N"));
        private readonly SqliteDatabase _database;

        public ModelFixture()
        {
            _database = new SqliteDatabase(AppPaths.ForRoot(_root));
            Store = CreateStore();
        }

        public SqliteModelStore Store { get; }
        public SqliteModelStore CreateStore() => new(_database);

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();

            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public FakeHttpClientFactory(HttpMessageHandler handler)
        {
            _handler = handler;
        }

        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private sealed class CompatibilityHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public bool AllRequestsAuthenticated { get; private set; } = true;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            AllRequestsAuthenticated &=
                string.Equals(
                    request.Headers.Authorization?.Scheme,
                    "Bearer",
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    request.Headers.Authorization?.Parameter,
                    "test-secret",
                    StringComparison.Ordinal);

            var payload = await request.Content!.ReadAsStringAsync(cancellationToken);
            if (payload.Contains("\"stream\":true", StringComparison.Ordinal))
            {
                return CreateResponse("data: {\"type\":\"response.output_text.delta\"}\n\n", "text/event-stream");
            }

            if (payload.Contains("\"tools\":", StringComparison.Ordinal))
            {
                return CreateResponse("{\"type\":\"function_call\",\"name\":\"ping\"}");
            }

            return CreateResponse("{\"output_text\":\"OK\"}");
        }

        private static HttpResponseMessage CreateResponse(
            string content,
            string mediaType = "application/json") =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(content)
                {
                    Headers = { ContentType = new MediaTypeHeaderValue(mediaType) }
                }
            };
    }
}
