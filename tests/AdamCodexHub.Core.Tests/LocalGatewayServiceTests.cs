using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
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
    public async Task GatewayIndexesModelsFastestFirstAndTagsThemForCodex()
    {
        await using var fixture = new GatewayFixture();
        await fixture.InitializeAsync();
        await fixture.AddEnabledModelAsync("slow-model");
        await fixture.SaveLatencyAsync("enabled-model", firstByteMs: 1500);
        await fixture.SaveLatencyAsync("slow-model", firstByteMs: 30_000);

        // Codex (app-server) shape: it cannot colour its rows, so the speed tag must ride along
        // in display_name — and the model that answers fastest must come first.
        using var request = new HttpRequestMessage(
            HttpMethod.Get, "/v1/models?client_version=0.153.4");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", fixture.Gateway.LocalToken);
        using var response = await fixture.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        using var doc = JsonDocument.Parse(body);
        var models = doc.RootElement.GetProperty("models").EnumerateArray().ToArray();
        Assert.True(models.Length == 2, body);

        Assert.Equal("enabled-model", models[0].GetProperty("slug").GetString());
        Assert.Equal("enabled-model (F)", models[0].GetProperty("display_name").GetString());
        Assert.Equal(1, models[0].GetProperty("priority").GetInt32());
        Assert.Equal("slow-model", models[1].GetProperty("slug").GetString());
        Assert.Equal("slow-model (S)", models[1].GetProperty("display_name").GetString());
        Assert.Equal(2, models[1].GetProperty("priority").GetInt32());
    }

    /// <summary>
    /// The provider answers 503 → the client gets that 503, and the keys stay usable. A provider hiccup
    /// used to mark the key Offline, so the user's session fell through to the gateway's own
    /// "No usable API key remains" 503 and Codex showed "Reconnecting …" while switching models
    /// (2026-09-11). 503 proves nothing about the key, so the key must not be punished for it.
    /// </summary>
    [Fact]
    public async Task AnUpstreamServerErrorIsHandedBackWithoutBlamingTheKey()
    {
        await using var fixture = new GatewayFixture();
        await fixture.InitializeAsync();
        fixture.Handler.EveryTokenStatus = HttpStatusCode.ServiceUnavailable;

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
        {
            Content = new StringContent(
                "{\"model\":\"enabled-model\",\"input\":\"hello\",\"stream\":false}",
                Encoding.UTF8,
                "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            fixture.Gateway.LocalToken);

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(new[] { "first-secret" }, fixture.Handler.SeenTokens);

        var keys = await fixture.KeyPool.ListAsync("upstream");
        Assert.All(keys, key =>
        {
            // Untouched: no Offline mark, no cooldown. The provider's own 503 was the answer.
            Assert.Equal(KeyHealth.Unknown, key.Health);
            Assert.Null(key.CooldownUntil);
        });
    }

    /// <summary>The background passes read this clock: if the user is in a session, they stand down.</summary>
    [Fact]
    public async Task ServingTheUserMarksTheProviderAsBusyForTheBackgroundPasses()
    {
        UserTrafficClock.Reset();
        await using var fixture = new GatewayFixture();
        await fixture.InitializeAsync();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/models");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            fixture.Gateway.LocalToken);
        using var catalogue = await fixture.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, catalogue.StatusCode);

        // A catalogue read is Codex opening, not the user typing: it must not park the probe passes.
        Assert.False(UserTrafficClock.IsBusy("upstream", TimeSpan.FromMinutes(3)));

        using var turn = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
        {
            Content = new StringContent(
                "{\"model\":\"enabled-model\",\"input\":\"hello\",\"stream\":true}",
                Encoding.UTF8,
                "application/json")
        };
        turn.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            fixture.Gateway.LocalToken);
        using var replied = await fixture.Client.SendAsync(turn);
        Assert.Equal(HttpStatusCode.OK, replied.StatusCode);

        Assert.True(UserTrafficClock.IsBusy("upstream", TimeSpan.FromMinutes(3)));
        UserTrafficClock.Reset();
    }

    /// <summary>A probe is the hub measuring models; it must not look like the user being present.</summary>
    [Fact]
    public async Task AProbeRequestDoesNotCountAsUserTraffic()
    {
        UserTrafficClock.Reset();
        await using var fixture = new GatewayFixture();
        await fixture.InitializeAsync();
        // Answer every key: this test is about the traffic clock, not about failover.
        fixture.Handler.EveryTokenStatus = HttpStatusCode.OK;

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
        {
            Content = new StringContent(
                "{\"model\":\"enabled-model\",\"input\":\"probe\",\"stream\":true}",
                Encoding.UTF8,
                "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            fixture.Gateway.LocalToken);
        request.Headers.TryAddWithoutValidation(
            LocalGatewayService.ProbeHeaderName,
            "1");

        using var response = await fixture.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.False(UserTrafficClock.IsBusy("upstream", TimeSpan.FromMinutes(3)));
        UserTrafficClock.Reset();
    }

    /// <summary>
    /// A 429 with only one key left is handed back to Codex (which backs off) instead of taking the key
    /// away from the session: the hub parks its own probes and leaves the key usable.
    /// </summary>
    [Fact]
    public async Task ARateLimitWithTheLastKeyIsHandedBackRatherThanParkingTheKey()
    {
        ProviderBackoff.Reset();
        await using var fixture = new GatewayFixture();
        await fixture.InitializeAsync();
        fixture.Handler.EveryTokenStatus = HttpStatusCode.TooManyRequests;

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
        {
            Content = new StringContent(
                "{\"model\":\"enabled-model\",\"input\":\"hello\",\"stream\":false}",
                Encoding.UTF8,
                "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            fixture.Gateway.LocalToken);

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.True(ProviderBackoff.IsParked("upstream"));

        var keys = await fixture.KeyPool.ListAsync("upstream");
        // The first key is spent (failover tried it), but the key the client was on stays usable.
        Assert.Equal(KeyHealth.Cooldown, keys[0].Health);
        Assert.Equal(KeyHealth.Unknown, keys[1].Health);
        ProviderBackoff.Reset();
    }

    /// <summary>
    /// Codex retries a failed turn with the model that turn started on, so a user who picks another
    /// model sees every request still asking for the old one and concludes the pick is not connected.
    /// The hub says it out loud instead of leaving that to be guessed.
    /// </summary>
    [Fact]
    public async Task RepeatedRefusalsOfOneModelAreCalledOut()
    {
        await using var fixture = new GatewayFixture();
        await fixture.InitializeAsync();
        fixture.Handler.EveryTokenStatus = HttpStatusCode.ServiceUnavailable;

        var logs = new List<string>();
        fixture.Gateway.LogMessage += message =>
        {
            lock (logs)
            {
                logs.Add(message);
            }
        };

        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
            {
                Content = new StringContent(
                    "{\"model\":\"enabled-model\",\"input\":\"tiếp\",\"stream\":false}",
                    Encoding.UTF8,
                    "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                fixture.Gateway.LocalToken);
            using var response = await fixture.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }

        // The third answer is non-retryable: that is what ends the turn Codex is stuck on, so a new
        // turn can run on the model the user actually picked.
        using (var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
        {
            Content = new StringContent(
                "{\"model\":\"enabled-model\",\"input\":\"tiếp\",\"stream\":false}",
                Encoding.UTF8,
                "application/json")
        })
        {
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                fixture.Gateway.LocalToken);
            using var response = await fixture.Client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("cannot serve this request right now", body, StringComparison.Ordinal);
        }

        Assert.Contains(logs, line => line.Contains("retrying a turn", StringComparison.Ordinal));
    }

    /// <summary>
    /// The loop can only end one way: the request has to succeed. Codex ignores non-retryable answers and
    /// keeps re-running the turn with the model it started on, so a model the provider keeps refusing is
    /// served, for that turn, by another model of the same provider that is known to answer — logged, and
    /// named in the response header rather than hidden.
    /// </summary>
    [Fact]
    public async Task AStuckTurnIsServedByAnotherModelSoTheLoopCanEnd()
    {
        await using var fixture = new GatewayFixture();
        await fixture.InitializeAsync();
        await fixture.Models.UpsertAsync(GatewayFixture.CreateModel("fallback-model"));
        await fixture.Models.SaveCompatibilityAsync(new CompatibilityResult
        {
            ProviderId = "upstream",
            ModelId = "fallback-model",
            VerifiedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            Text = true,
            Responses = true,
            Streaming = true,
            Score = 90,
            FirstByteMs = 300,
            TotalMs = 400
        });

        fixture.Handler.EveryTokenStatus = HttpStatusCode.ServiceUnavailable;
        fixture.Handler.ServedModel = "fallback-model";

        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var attemptRequest = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
            {
                Content = new StringContent(
                    "{\"model\":\"enabled-model\",\"input\":\"tiếp\",\"stream\":true}",
                    Encoding.UTF8,
                    "application/json")
            };
            attemptRequest.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                fixture.Gateway.LocalToken);
            using var attemptResponse = await fixture.Client.SendAsync(attemptRequest);
            Assert.NotEqual(HttpStatusCode.OK, attemptResponse.StatusCode);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
        {
            Content = new StringContent(
                "{\"model\":\"enabled-model\",\"input\":\"tiếp\",\"stream\":true}",
                Encoding.UTF8,
                "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            fixture.Gateway.LocalToken);
        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            "fallback-model",
            response.Headers.GetValues("x-adam-codexhub-fallback").Single());
        Assert.Contains(
            fixture.Handler.SeenBodies,
            body => body.Contains("\"model\":\"fallback-model\"", StringComparison.Ordinal));
    }

    /// <summary>
    /// Switching models in Codex has to carry the work on. The turn Codex pinned to the old model is
    /// served by the model the user picked — even when a quicker model is available — because the point
    /// of picking one is to get the work done with it.
    /// </summary>
    [Fact]
    public async Task AStuckTurnIsServedByTheModelTheUserPicked()
    {
        await using var fixture = new GatewayFixture();
        await fixture.InitializeAsync();

        await fixture.Models.UpsertAsync(GatewayFixture.CreateModel("picked-model"));
        await fixture.Models.SaveCompatibilityAsync(new CompatibilityResult
        {
            ProviderId = "upstream",
            ModelId = "picked-model",
            VerifiedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            Text = true,
            Responses = true,
            Streaming = true,
            Score = 90,
            FirstByteMs = 800,
            TotalMs = 900
        });

        await fixture.Models.UpsertAsync(GatewayFixture.CreateModel("quicker-model"));
        await fixture.Models.SaveCompatibilityAsync(new CompatibilityResult
        {
            ProviderId = "upstream",
            ModelId = "quicker-model",
            VerifiedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            Text = true,
            Responses = true,
            Streaming = true,
            Score = 90,
            FirstByteMs = 50,
            TotalMs = 60
        });

        // What the user just chose inside Codex.
        fixture.SessionModel.ModelId = "picked-model";
        fixture.Handler.EveryTokenStatus = HttpStatusCode.ServiceUnavailable;
        fixture.Handler.ServedModel = "picked-model";

        var continued = new List<(string Requested, string Served)>();
        fixture.Gateway.TurnContinued += (requested, served) =>
        {
            lock (continued)
            {
                continued.Add((requested, served));
            }
        };

        // One refusal is enough for the user's own pick: they chose it to carry on with.
        using (var first = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
        {
            Content = new StringContent(
                "{\"model\":\"enabled-model\",\"input\":\"tiếp\",\"stream\":true}",
                Encoding.UTF8,
                "application/json")
        })
        {
            first.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                fixture.Gateway.LocalToken);
            using var firstResponse = await fixture.Client.SendAsync(first);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, firstResponse.StatusCode);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
        {
            Content = new StringContent(
                "{\"model\":\"enabled-model\",\"input\":\"tiếp\",\"stream\":true}",
                Encoding.UTF8,
                "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            fixture.Gateway.LocalToken);
        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("picked-model", response.Headers.GetValues("x-adam-codexhub-fallback").Single());

        // The tray line comes from this event, so the user can see which model is really working.
        Assert.Contains(continued, entry => entry is { Requested: "enabled-model", Served: "picked-model" });
    }

    /// <summary>
    /// An image endpoint is enabled and has a verdict, but a Codex turn can never use it — it is only
    /// there to be picked and fail, and it would sit in the picker marked "(U)" forever because the
    /// probes deliberately skip those ids.
    /// </summary>
    [Fact]
    public async Task GatewayDoesNotOfferImageEndpoints()
    {
        await using var fixture = new GatewayFixture();
        await fixture.InitializeAsync();
        await fixture.Models.UpsertAsync(GatewayFixture.CreateModel("gpt-image-2"));

        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/models?client_version=0.153.4");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            fixture.Gateway.LocalToken);
        using var response = await fixture.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("gpt-image-2", body, StringComparison.Ordinal);
        Assert.Contains("enabled-model", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GatewayKeepsLocalTokenStableAcrossRestarts()
    {
        await using var fixture = new GatewayFixture();
        await fixture.InitializeAsync();
        var firstToken = fixture.Gateway.LocalToken;

        await fixture.Gateway.StopAsync();
        await fixture.Gateway.StartAsync();

        // The overlay Codex Desktop reads must survive app restarts; a rotated token would
        // strand it on the old credential.
        Assert.Equal(firstToken, fixture.Gateway.LocalToken);
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
                new SqliteCodexReadinessStore(_database),
                SessionModel,
                new FakeHttpClientFactory(Handler));
            ProviderManager = providerManager;
            Models = models;
        }

        /// <summary>Model Codex is set to — the one the user picked in Codex's own picker.</summary>
        public FakeSessionModelReader SessionModel { get; } = new();

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

        public static ModelDescriptor CreateModel(string id, bool enabled = true) => new()
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

        public Task AddEnabledModelAsync(string id) =>
            Models.UpsertAsync(CreateModel(id, enabled: true));

        /// <summary>A stored result where text, responses and streaming all work, so the model is
        /// classified purely on the latency it reports. Score &gt; 0 keeps the model enabled.</summary>
        public Task SaveLatencyAsync(string modelId, int firstByteMs) =>
            Models.SaveCompatibilityAsync(new CompatibilityResult
            {
                ProviderId = "upstream",
                ModelId = modelId,
                VerifiedAt = DateTimeOffset.UtcNow,
                Text = true,
                Responses = true,
                Streaming = true,
                Score = 90,
                FirstByteMs = firstByteMs,
                TotalMs = firstByteMs
            });
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

    private sealed class FakeSessionModelReader : ICodexSessionModelReader
    {
        public string? ModelId { get; set; }

        public CodexSessionModel? Read() => ModelId is null ? null : new CodexSessionModel(ModelId);
    }

    private sealed class FailoverHandler : HttpMessageHandler
    {
        public List<string> SeenTokens { get; } = new();

        /// <summary>Request bodies the hub sent upstream, so a test can see which model served it.</summary>
        public List<string> SeenBodies { get; } = new();

        /// <summary>When set, every token gets this answer — used for the provider-side failure cases.</summary>
        public HttpStatusCode? EveryTokenStatus { get; set; }

        /// <summary>Model that answers 200 even when <see cref="EveryTokenStatus"/> says otherwise.</summary>
        public string? ServedModel { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var token = request.Headers.Authorization?.Parameter ?? string.Empty;
            SeenTokens.Add(token);

            var payload = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            SeenBodies.Add(payload);

            if (ServedModel is not null &&
                payload.Contains($"\"model\":\"{ServedModel}\"", StringComparison.Ordinal))
            {
                return Stub(HttpStatusCode.OK);
            }

            if (EveryTokenStatus is { } status)
            {
                return Stub(status);
            }

            if (token == "first-secret")
            {
                var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                limited.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(1));
                return limited;
            }

            return Stub(HttpStatusCode.OK);
        }

        private static HttpResponseMessage Stub(HttpStatusCode status)
        {
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(
                    "data: {\"type\":\"response.output_text.delta\"}\n\n")
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            return response;
        }
    }
}
