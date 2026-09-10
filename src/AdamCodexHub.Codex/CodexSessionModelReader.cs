using AdamCodexHub.Core.Domain;
using AdamCodexHub.Core.Interfaces;
using Microsoft.Data.Sqlite;

namespace AdamCodexHub.Codex;

/// <summary>
/// Reads the newest Codex session's model out of Codex's own database
/// (<c>~/.codex/state_5.sqlite</c>, table <c>threads</c>).
///
/// Why this exists: switching the model inside Codex Desktop's "Select model" picker does NOT
/// rewrite <c>~/.codex/config.toml</c> — Codex keeps the choice per thread. Reading that table is
/// therefore the only way to answer "which model is Codex using right now?".
///
/// The file is opened read-only with a private cache so a running Codex (WAL mode) is never
/// disturbed, and every failure path returns <c>null</c> instead of throwing.
/// </summary>
public sealed class CodexSessionModelReader : ICodexSessionModelReader
{
    private const string RichQuery =
        "SELECT id, model, model_provider, COALESCE(updated_at_ms, updated_at * 1000) " +
        "FROM threads " +
        "WHERE archived = 0 AND model IS NOT NULL AND TRIM(model) <> '' " +
        "ORDER BY COALESCE(updated_at_ms, updated_at * 1000) DESC, created_at DESC LIMIT 1";

    // Older Codex builds have no updated_at_ms / archived columns.
    private const string LegacyQuery =
        "SELECT id, model, model_provider, updated_at * 1000 " +
        "FROM threads " +
        "WHERE model IS NOT NULL AND TRIM(model) <> '' " +
        "ORDER BY updated_at DESC LIMIT 1";

    public CodexSessionModelReader(string? codexHome = null)
    {
        var home = string.IsNullOrWhiteSpace(codexHome)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".codex")
            : codexHome;
        StateDatabasePath = Path.Combine(home, "state_5.sqlite");
    }

    /// <summary>Full path of the Codex state database being read (useful for diagnostics).</summary>
    public string StateDatabasePath { get; }

    /// <inheritdoc />
    public CodexSessionModel? Read()
    {
        try
        {
            if (!File.Exists(StateDatabasePath))
            {
                return null;
            }

            using var connection = new SqliteConnection(BuildConnectionString());
            connection.Open();
            return ReadWith(connection, RichQuery) ?? ReadWith(connection, LegacyQuery);
        }
        catch (SqliteException)
        {
            // Missing columns / locked file / half-written WAL: not fatal, we just have no answer.
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private string BuildConnectionString() => new SqliteConnectionStringBuilder
    {
        DataSource = StateDatabasePath,
        Mode = SqliteOpenMode.ReadOnly,
        Cache = SqliteCacheMode.Private,
        Pooling = false
    }.ToString();

    private static CodexSessionModel? ReadWith(SqliteConnection connection, string query)
    {
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = query;
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            var threadId = reader.IsDBNull(0) ? null : reader.GetString(0);
            var model = reader.IsDBNull(1) ? null : reader.GetString(1);
            var providerId = reader.IsDBNull(2) ? null : reader.GetString(2);
            var stampMs = reader.IsDBNull(3) ? 0L : reader.GetInt64(3);
            if (string.IsNullOrWhiteSpace(model))
            {
                return null;
            }

            DateTimeOffset? observedAt = stampMs > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(stampMs).ToLocalTime()
                : null;
            return new CodexSessionModel(model.Trim(), threadId, providerId, observedAt);
        }
        catch (SqliteException)
        {
            return null;
        }
    }
}
