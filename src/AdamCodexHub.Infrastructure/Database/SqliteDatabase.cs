using AdamCodexHub.Infrastructure.Paths;
using Microsoft.Data.Sqlite;

namespace AdamCodexHub.Infrastructure.Database;

public sealed class SqliteDatabase
{
    private readonly AppPaths _paths;
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private bool _initialized;

    public SqliteDatabase(AppPaths paths)
    {
        _paths = paths;
    }

    public string ConnectionString =>
        new SqliteConnectionStringBuilder
        {
            DataSource = _paths.DatabaseFile,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            return;
        }

        await _initializationGate.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
            {
                return;
            }

            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync(cancellationToken);

            var command = connection.CreateCommand();
            command.CommandText = @"
PRAGMA journal_mode=WAL;

CREATE TABLE IF NOT EXISTS providers (
    id TEXT PRIMARY KEY COLLATE NOCASE,
    name TEXT NOT NULL,
    adapter TEXT NOT NULL,
    base_url TEXT NOT NULL,
    trust_level TEXT NOT NULL,
    enabled INTEGER NOT NULL,
    health TEXT NOT NULL,
    auth_type TEXT NOT NULL,
    auth_header_name TEXT NULL,
    models_endpoint TEXT NULL,
    responses_endpoint TEXT NULL,
    chat_completions_endpoint TEXT NULL,
    extra_headers_json TEXT NOT NULL,
    declared_capabilities_json TEXT NOT NULL,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS app_state (
    key TEXT PRIMARY KEY,
    value TEXT NOT NULL,
    updated_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS models (
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

CREATE INDEX IF NOT EXISTS idx_models_provider_state
ON models(provider_id, state, enabled);

CREATE TABLE IF NOT EXISTS compatibility_results (
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

CREATE INDEX IF NOT EXISTS idx_compatibility_latest
ON compatibility_results(provider_id, model_id, verified_at DESC);

CREATE TABLE IF NOT EXISTS provider_keys (
    id TEXT PRIMARY KEY,
    provider_id TEXT NOT NULL,
    label TEXT NOT NULL,
    secret_reference TEXT NOT NULL,
    priority INTEGER NOT NULL,
    enabled INTEGER NOT NULL,
    health TEXT NOT NULL,
    cooldown_until TEXT NULL,
    last_test_at TEXT NULL,
    last_success_at TEXT NULL,
    last_failure_at TEXT NULL,
    failure_reason TEXT NULL,
    masked_display TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_provider_keys_provider_priority
ON provider_keys(provider_id, priority);
";

            await command.ExecuteNonQueryAsync(cancellationToken);
            _initialized = true;
        }
        finally
        {
            _initializationGate.Release();
        }
    }
}
