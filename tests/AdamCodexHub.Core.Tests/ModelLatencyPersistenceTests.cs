using AdamCodexHub.Core.Domain;
using AdamCodexHub.Infrastructure.Database;
using AdamCodexHub.Infrastructure.Models;
using AdamCodexHub.Infrastructure.Paths;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AdamCodexHub.Core.Tests;

/// <summary>
/// The latency columns (first_byte_ms / total_ms) must survive a round-trip through
/// compatibility_results, and the migration that adds them to an EXISTING database must be
/// idempotent and must not touch existing rows.
/// </summary>
public sealed class ModelLatencyPersistenceTests
{
    [Fact]
    public async Task LatencyColumnsRoundTripNullsIncluded()
    {
        await using var fixture = new DatabaseFixture();
        await fixture.Store.UpsertAsync(Model("slow-model"));
        await fixture.Store.UpsertAsync(Model("unmeasured-model"));

        var slow = new CompatibilityResult
        {
            ProviderId = "hhtech",
            ModelId = "slow-model",
            VerifiedAt = DateTimeOffset.UtcNow,
            Text = true,
            Responses = true,
            Streaming = true,
            Score = 90,
            FirstByteMs = 37_700,
            TotalMs = 77_000
        };
        var unmeasured = slow with { ModelId = "unmeasured-model", FirstByteMs = null, TotalMs = null };
        await fixture.Store.SaveCompatibilityAsync(slow);
        await fixture.Store.SaveCompatibilityAsync(unmeasured);

        // A fresh store instance reads what a fresh app launch would read.
        var restarted = fixture.CreateStore();
        var readBack = await restarted.GetLatestCompatibilityAsync("hhtech", "slow-model");
        Assert.NotNull(readBack);
        Assert.Equal(37_700, readBack.FirstByteMs);
        Assert.Equal(77_000, readBack.TotalMs);

        var nullRoundTrip = await restarted.GetLatestCompatibilityAsync("hhtech", "unmeasured-model");
        Assert.NotNull(nullRoundTrip);
        Assert.Null(nullRoundTrip.FirstByteMs);
        Assert.Null(nullRoundTrip.TotalMs);

        // And the classifier can act on what came back (77s total => Slow, still usable).
        Assert.Equal(Callability.Slow, ModelCallability.Classify(readBack, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task LegacyDatabaseWithoutLatencyColumnsIsMigratedInPlace()
    {
        await using var fixture = new DatabaseFixture(createSchema: false);
        await CreateLegacyDatabaseAsync(fixture.Paths.DatabaseFile);

        var database = new SqliteDatabase(fixture.Paths);
        await database.InitializeAsync();
        // Second initialize must be a no-op, not a "duplicate column name" failure.
        await database.InitializeAsync();

        var columns = await ReadColumnsAsync(fixture.Paths.DatabaseFile);
        Assert.Contains("first_byte_ms", columns);
        Assert.Contains("total_ms", columns);

        // The pre-existing row is untouched and readable with null latency.
        var store = new SqliteModelStore(new SqliteDatabase(fixture.Paths));
        var legacy = await store.GetLatestCompatibilityAsync("hhtech", "legacy-model");
        Assert.NotNull(legacy);
        Assert.Equal(60, legacy.Score);
        Assert.Null(legacy.FirstByteMs);
        Assert.Null(legacy.TotalMs);
    }

    private static ModelDescriptor Model(string remoteId) => new()
    {
        ProviderId = "hhtech",
        RemoteId = remoteId,
        DisplayName = remoteId,
        State = ModelLifecycleState.Enabled,
        Enabled = true
    };

    /// <summary>Creates the pre-1.4.0 shape of the database: the models + compatibility_results
    /// tables exactly as they shipped before the latency columns existed, plus one legacy row.</summary>
    private static async Task CreateLegacyDatabaseAsync(string databaseFile)
    {
        await using var connection = new SqliteConnection($"Data Source={databaseFile}");
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = @"
CREATE TABLE models (
    provider_id TEXT NOT NULL COLLATE NOCASE,
    remote_id TEXT NOT NULL COLLATE NOCASE,
    display_name TEXT NOT NULL,
    state TEXT NOT NULL,
    enabled INTEGER NOT NULL,
    input_modalities_json TEXT NOT NULL,
    capabilities_json TEXT NOT NULL,
    context_window INTEGER NULL,
    last_seen_at TEXT NULL,
    last_verified_at TEXT NULL,
    compatibility_score INTEGER NULL,
    PRIMARY KEY (provider_id, remote_id)
);

CREATE TABLE compatibility_results (
    provider_id TEXT NOT NULL COLLATE NOCASE,
    model_id TEXT NOT NULL COLLATE NOCASE,
    verified_at TEXT NOT NULL,
    text_supported INTEGER NOT NULL,
    responses_supported INTEGER NOT NULL,
    chat_completions_supported INTEGER NOT NULL,
    streaming_supported INTEGER NOT NULL,
    tool_calling_supported INTEGER NOT NULL,
    structured_json_supported INTEGER NOT NULL,
    vision_supported INTEGER NOT NULL,
    score INTEGER NOT NULL,
    notes TEXT NULL,
    PRIMARY KEY (provider_id, model_id, verified_at)
);

INSERT INTO models
(provider_id, remote_id, display_name, state, enabled, input_modalities_json,
 capabilities_json, context_window, last_seen_at, last_verified_at, compatibility_score)
VALUES ('hhtech', 'legacy-model', 'legacy-model', 'Enabled', 1, '[""text""]', '[]', NULL, NULL, NULL, 60);

INSERT INTO compatibility_results
(provider_id, model_id, verified_at, text_supported, responses_supported,
 chat_completions_supported, streaming_supported, tool_calling_supported,
 structured_json_supported, vision_supported, score, notes)
VALUES ('hhtech', 'legacy-model', '2026-09-10T00:00:00.0000000+00:00', 1, 1, 0, 1, 1, 1, 0, 60, NULL);
";
        await command.ExecuteNonQueryAsync();
        SqliteConnection.ClearAllPools();
    }

    private static async Task<IReadOnlyList<string>> ReadColumnsAsync(string databaseFile)
    {
        var columns = new List<string>();
        await using var connection = new SqliteConnection($"Data Source={databaseFile}");
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(compatibility_results);";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }

    private sealed class DatabaseFixture : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "AdamCodexHub.Tests",
            Guid.NewGuid().ToString("N"));
        private readonly SqliteDatabase _database;

        public DatabaseFixture(bool createSchema = true)
        {
            Paths = AppPaths.ForRoot(_root);
            _database = new SqliteDatabase(Paths);
            if (createSchema)
            {
                // Touch the schema up front so tests that need tables can use the store directly.
                _database.InitializeAsync().GetAwaiter().GetResult();
            }

            Store = new SqliteModelStore(_database);
        }

        public AppPaths Paths { get; }

        public SqliteModelStore Store { get; }

        public SqliteModelStore CreateStore() => new(_database);

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();

            if (Directory.Exists(_root))
            {
                try
                {
                    Directory.Delete(_root, recursive: true);
                }
                catch
                {
                    // Temp cleanup is best-effort.
                }
            }

            return ValueTask.CompletedTask;
        }
    }
}
