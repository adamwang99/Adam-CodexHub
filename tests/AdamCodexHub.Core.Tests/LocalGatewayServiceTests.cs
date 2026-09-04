using System.Net;
using System.Net.Http.Headers;
using System.Text;
using AdamCodexHub.Core.Domain;
using AdamCodexHub.Core.Interfaces;
using AdamCodexHub.Gateway;
using AdamCodexHub.Infrastructure.Database;
using AdamCodexHub.Infrastructure.Keys;
using AdamCodexHub.Infrastructure.Models;
using AdamCodexHub.Infrastructure.Paths;
using AdamCodexHub.Infrastructure.Providers;
using AdamCodexHub.Providers;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AdamCodexHub.Core.Tests;

public sealed class LocalGatewayServiceTests
{
    [Fact]
    public async Task GatewayFailsOverKeysAndRelaysStreamingResponse()
    {
        await using var fixture = new GatewayFixture();
        await fixture.InitializeAsync();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
        {
            Content = new StringContent(
                "{\"model\":\"enabled-model\",\"input\":\"hello\",\"stream\":true}",
                Encoding.UTF8,
                "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            fixture.Gateway.LocalToken);

        using var response = await fixture.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("response.output_text.delta", body, StringComparison.Ordinal);
        Assert.Equal(new[] { "first-secret", "second-secret" }, fixture.Handler.SeenTokens);

        var keys = await fixture.KeyPool.ListAsync("upstream");
        Assert.Equal(KeyHealth.Cooldown, keys[0].Health);
        Assert.Equal(KeyHealth.Healthy, keys[1].Health);
    }

    [Fact]
    public async Task GatewayRequiresLocalTokenAndListsOnlyEnabledModels()
    {
        await using var fixture = new GatewayFixture();
        await fixture.InitializeAsync();

        using var unauthorized = await fixture.Client.GetAsync("/v1/models");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        Assert.Empty(fixture.Handler.SeenTokens);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/models");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            fixture.Gateway.LocalToken);
        using var response = await fixture.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("enabled-model", body, StringComparison.Ordinal);
        Assert.DoesNotContain("disabled-model", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GatewayRotatesLocalTokenWhenRestarted()
    {
        await using var fixture = new GatewayFixture();
        await fixture.InitializeAsync();
        var firstToken = fixture.Gateway.LocalToken;

        await fixture.Gateway.StopAsync();
        await fixture.Gateway.StartAsync();

        Assert.NotEqual(firstToken, fixture.Gateway.LocalToken);
        Assert.Equal(64, fixture.Gateway.LocalToken.Length);
    }

    private sealed class GatewayFixture : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "AdamCodexHub.Tests",
            Guid.NewGuid().ToString("N"));
        private readonly SqliteDatabase _database;

        public GatewayFixture()
        {
            _database = new SqliteDatabase(AppPaths.ForRoot(_root));
            var vault = new MemoryKeyVault();
            KeyPool = new SqliteKeyPoolService(_database, vault);
            var providerManager = new ProviderManager(
                new GatewayRegistry(),
                new SqliteProviderStore(_database));
            var models = new SqliteModelStore(_database);
            Handler = new FailoverHandler();
            Gateway = new LocalGatewayService(
                providerManager,
                KeyPool,
                models,
                new FakeHttpClientFactory(Handler));
            ProviderManager = providerManager;
            Models = models;
        }

        public ProviderManager ProviderManager { get; }
        public SqliteKeyPoolService KeyPool { get; }
        public SqliteModelStore Models { get; }
        public FailoverHandler Handler { get; }
        public LocalGatewayService Gateway { get; }
        public HttpClient Client { get; private set; } = null!;

        public async Task InitializeAsync()
        {
            await ProviderManager.InitializeAsync();
            await ProviderManager.SetActiveAsync("upstream");
            await KeyPool.AddAsync("upstream", "First", "first-secret", 1);
            await KeyPool.AddAsync("upstream", "Second", "second-secret", 2);
            await Models.UpsertAsync(CreateModel("enabled-model", enabled: true));
            await Models.UpsertAsync(CreateModel("disabled-model", enabled: false));
            await Gateway.StartAsync();
            Client = new HttpClient
            {
                BaseAddress = new Uri($"http://127.0.0.1:{Gateway.Port}")
            };
        }

        public async ValueTask DisposeAsync()
        {
            Client?.Dispose();
            await Gateway.DisposeAsync();
            SqliteConnection.ClearAllPools();

            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        private static ModelDescriptor CreateModel(string id, bool enabled) => new()
        {
            ProviderId = "upstream",
            RemoteId = id,
            DisplayName = id,
            Enabled = enabled,
            State = enabled ? ModelLifecycleState.Enabled : ModelLifecycleState.Disabled,
            LastSeenAt = DateTimeOffset.UtcNow,
            LastVerifiedAt = DateTimeOffset.UtcNow,
            CompatibilityScore = 100
        };
    }

    private sealed class GatewayRegistry : IProviderRegistryService
    {
        public Task<IReadOnlyList<ProviderProfile>> GetBuiltInAsync(
            CancellationToken cancellationToken = default)
        {
            IReadOnlyList<ProviderProfile> providers = new[]
            {
                new ProviderProfile
                {
                    Id = "upstream",
                    Name = "Upstream",
                    Adapter = "openai-compatible",
                    BaseUrl = "https://upstream.example.test/v1",
                    AuthType = "bearer",
                    ResponsesEndpoint = "/responses",
                    ChatCompletionsEndpoint = "/chat/completions"
                }
            };
            return Task.FromResult(providers);
        }
    }

    private sealed class MemoryKeyVault : IKeyVault
    {
        private readonly Dictionary<string, string> _secrets = new();

        public Task<string> StoreAsync(
            string providerId,
            string secret,
            CancellationToken cancellationToken = default)
        {
            var reference = Guid.NewGuid().ToString("N");
            _secrets[reference] = secret;
            return Task.FromResult(reference);
        }

        public Task<string?> RetrieveAsync(
            string secretReference,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_secrets.GetValueOrDefault(secretReference));

        public Task DeleteAsync(
            string secretReference,
            CancellationToken cancellationToken = default)
        {
            _secrets.Remove(secretReference);
            return Task.CompletedTask;
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

    private sealed class FailoverHandler : HttpMessageHandler
    {
        public List<string> SeenTokens { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var token = request.Headers.Authorization?.Parameter ?? string.Empty;
            SeenTokens.Add(token);

            if (token == "first-secret")
            {
                var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                limited.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(1));
                return Task.FromResult(limited);
            }

            var success = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "data: {\"type\":\"response.output_text.delta\"}\n\n")
            };
            success.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            return Task.FromResult(success);
        }
    }
}
