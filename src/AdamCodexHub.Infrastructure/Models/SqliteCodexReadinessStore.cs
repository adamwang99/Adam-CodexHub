using System.Globalization;
using AdamCodexHub.Core.Domain;
using AdamCodexHub.Core.Interfaces;
using AdamCodexHub.Infrastructure.Database;
using Microsoft.Data.Sqlite;

namespace AdamCodexHub.Infrastructure.Models;

/// <summary>SQLite persistence for Codex readiness verdicts (one row per provider + model).</summary>
public sealed class SqliteCodexReadinessStore : ICodexReadinessStore
{
    private const string SelectColumns =
        "SELECT provider_id, model_id, ready, checked_at, latency_ms, detail FROM codex_readiness";

    private readonly SqliteDatabase _database;

    public SqliteCodexReadinessStore(SqliteDatabase database)
    {
        _database = database;
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        _database.InitializeAsync(cancellationToken);

    public async Task<IReadOnlyList<CodexReadiness>> GetAllAsync(
        string providerId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        await InitializeAsync(cancellationToken);

        var verdicts = new List<CodexReadiness>();
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        var command = connection.CreateCommand();
        command.CommandText = $"{SelectColumns} WHERE provider_id = $providerId ORDER BY model_id COLLATE NOCASE;";
        command.Parameters.AddWithValue("$providerId", providerId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            verdicts.Add(Read(reader));
        }

        return verdicts;
    }

    public async Task<CodexReadiness?> GetAsync(
        string providerId,
        string modelId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        await InitializeAsync(cancellationToken);

        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        var command = connection.CreateCommand();
        command.CommandText = $"{SelectColumns} WHERE provider_id = $providerId AND model_id = $modelId LIMIT 1;";
        command.Parameters.AddWithValue("$providerId", providerId);
        command.Parameters.AddWithValue("$modelId", modelId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task SaveAsync(CodexReadiness readiness, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(readiness);
        ArgumentException.ThrowIfNullOrWhiteSpace(readiness.ProviderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(readiness.ModelId);
        await InitializeAsync(cancellationToken);

        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO codex_readiness (provider_id, model_id, ready, checked_at, latency_ms, detail)
            VALUES ($providerId, $modelId, $ready, $checkedAt, $latencyMs, $detail)
            ON CONFLICT(provider_id, model_id) DO UPDATE SET
                ready = excluded.ready,
                checked_at = excluded.checked_at,
                latency_ms = excluded.latency_ms,
                detail = excluded.detail;
            """;
        command.Parameters.AddWithValue("$providerId", readiness.ProviderId);
        command.Parameters.AddWithValue("$modelId", readiness.ModelId);
        command.Parameters.AddWithValue("$ready", readiness.Ready ? 1 : 0);
        command.Parameters.AddWithValue("$checkedAt", readiness.CheckedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$latencyMs", (object?)readiness.LatencyMs ?? DBNull.Value);
        command.Parameters.AddWithValue("$detail", (object?)readiness.Detail ?? DBNull.Value);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static CodexReadiness Read(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetInt64(2) != 0,
        DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        reader.IsDBNull(4) ? null : (int)reader.GetInt64(4),
        reader.IsDBNull(5) ? null : reader.GetString(5));
}
