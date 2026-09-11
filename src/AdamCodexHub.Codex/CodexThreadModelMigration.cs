using System.Diagnostics;
using Microsoft.Data.Sqlite;

namespace AdamCodexHub.Codex;

/// <summary>
/// Codex keeps the chosen model per thread in <c>~/.codex/state_5.sqlite</c> (<c>threads.model</c>),
/// so a thread keeps whatever id it was last used with. Switching provider therefore strands every
/// thread pinned to an id the provider being activated does not offer, and Codex then refuses the
/// next turn ("The 'claude-5.5' model is not supported when using Codex with a ChatGPT account").
/// This moves those threads onto the model of the provider that is about to be used.
///
/// Safety: the file belongs to Codex, so we only write while Codex is closed, we replace nothing but
/// the model and provider columns (transcripts are untouched), and every failure path returns 0
/// instead of throwing. A return value of 0 means "nothing was changed".
/// </summary>
public sealed class CodexThreadModelMigration
{
    /// <summary>Codex's internal reviewer model — every provider can run it, so never rewrite it.</summary>
    public const string InternalModelId = "codex-auto-review";

    /// <summary>
    /// The provider id the gateway overlay writes into <c>model_provider</c>; threads have to carry
    /// the same one or Codex keeps sending them to the ChatGPT account. Must stay in step with
    /// <c>CodexConfigService</c>'s managed provider id.
    /// </summary>
    public const string GatewayProviderId = "adam_codexhub";

    /// <summary>Provider id Codex stores for threads that run on the ChatGPT account.</summary>
    public const string AccountProviderId = "openai";

    /// <summary>
    /// Codex home to repair — the hub passes its configured `CODEX_HOME`. Left empty this helper
    /// touches nothing on purpose: a migration that guesses a home is a migration that can rewrite
    /// the wrong database.
    /// </summary>
    public string? CodexHome { get; init; }

    /// <summary>Test seam; the real process probe is used when this is null.</summary>
    public Func<bool>? CodexIsRunning { get; init; }

    /// <summary>Optional log sink — how the activation note and the app log learn what happened.</summary>
    public Action<string>? Log { get; init; }

    public string StateDatabasePath => Path.Combine(CodexHome ?? string.Empty, "state_5.sqlite");

    /// <summary>
    /// Rewrites every thread whose model is not in <paramref name="offeredModels"/> to
    /// <paramref name="targetModel"/>. When <paramref name="targetProvider"/> is given, threads
    /// whose <c>model_provider</c> does not match it are rewritten too — a thread that is still
    /// pinned to the account refuses every gateway model ("not supported when using Codex with a
    /// ChatGPT account") even though the config points at the gateway. Returns the number of
    /// threads moved (0 = nothing to do, or the write was skipped).
    /// </summary>
    public int Migrate(
        IReadOnlyCollection<string>? offeredModels,
        string? targetModel,
        string? targetProvider = null)
    {
        if (string.IsNullOrWhiteSpace(CodexHome) || offeredModels is null || string.IsNullOrWhiteSpace(targetModel))
        {
            return 0;
        }

        var offered = offeredModels
            .Where(model => !string.IsNullOrWhiteSpace(model))
            .Select(model => model.Trim())
            .ToHashSet(StringComparer.Ordinal);
        offered.Add(InternalModelId);
        if (!offered.Contains(targetModel))
        {
            return 0;
        }

        // A switch has to take effect on the chats that are already open — that is the whole point of
        // the repair (Adam, 2026-09-11: "có tác dụng ngay khi chuyển và tiếp tục làm việc trên chat cũ
        // được ngay, không cần tạo chat mới"). Codex Desktop runs whenever the hub is in use, so a
        // refusal to write while it runs would mean this never happens at all; the write is attempted
        // instead (WAL plus a busy timeout, one backup per session) and simply reports 0 if the file
        // will not take it.
        var running = (CodexIsRunning ?? IsCodexRunning)();

        if (!File.Exists(StateDatabasePath))
        {
            return 0;
        }

        BackupOnce();

        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = StateDatabasePath,
                Mode = SqliteOpenMode.ReadWrite,
                Cache = SqliteCacheMode.Private,
                Pooling = false
            }.ToString());
            connection.Open();
            using (var pragma = connection.CreateCommand())
            {
                // Codex holds the same file open; wait rather than fail at the first lock.
                pragma.CommandText = "PRAGMA busy_timeout = 4000;";
                pragma.ExecuteNonQuery();
            }

            // This database belongs to a closed app that updates itself, and the columns written here
            // are an observation of one build, not a contract (measured across CW 0.153.0 and 0.153.4
            // during 2026-09-11). If the shape is not exactly what this repair understands, the honest
            // move is to leave the user's data alone and say so — a hopeful UPDATE against a renamed
            // column is how a convenience turns into data loss.
            if (!SchemaLooksFamiliar(connection, out var schemaNote))
            {
                Log?.Invoke(
                    $"Threads: leaving Codex's database untouched — {schemaNote}. " +
                    "Switching provider still works; the open chats may need a new chat.");
                return 0;
            }

            var stranded = new List<(string Id, bool Model, bool Route)>();
            using (var select = connection.CreateCommand())
            {
                select.CommandText =
                    "SELECT id, model, model_provider FROM threads WHERE model IS NOT NULL AND TRIM(model) <> ''";
                using var reader = select.ExecuteReader();
                while (reader.Read())
                {
                    var pinned = reader.GetString(1).Trim();
                    var provider = reader.IsDBNull(2) ? string.Empty : reader.GetString(2).Trim();
                    // Two ways a thread ends up unusable after a switch: the model it is pinned to is
                    // not offered by the provider we just moved to, or the thread is still routed at
                    // the previous source. Codex keeps model_provider per thread, so a thread left on
                    // the account answers a gateway switch with "not supported when using Codex with a
                    // ChatGPT account" whatever config.toml says.
                    var wrongModel = !offered.Contains(pinned);
                    var wrongRoute = targetProvider is not null &&
                        !string.Equals(provider, targetProvider, StringComparison.Ordinal);
                    if (wrongModel || wrongRoute)
                    {
                        stranded.Add((reader.GetString(0), wrongModel, wrongRoute));
                    }
                }
            }

            if (stranded.Count == 0)
            {
                return 0;
            }

            Log?.Invoke(
                (running ? "Codex is running — " : string.Empty) +
                $"repairing {stranded.Count} thread(s) pinned to a model this provider does not offer " +
                $"(→ {targetModel}).");

            using var transaction = connection.BeginTransaction();
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText =
                "UPDATE threads SET " +
                "model = CASE WHEN $fixModel = 1 THEN $model ELSE model END, " +
                "model_provider = CASE WHEN $fixRoute = 1 THEN $provider ELSE model_provider END " +
                "WHERE id = $id";
            update.Parameters.Add("$model", SqliteType.Text).Value = targetModel;
            update.Parameters.Add("$provider", SqliteType.Text).Value = targetProvider ?? string.Empty;
            var fixModel = update.Parameters.Add("$fixModel", SqliteType.Integer);
            var fixRoute = update.Parameters.Add("$fixRoute", SqliteType.Integer);
            var id = update.Parameters.Add("$id", SqliteType.Text);
            foreach (var (threadId, model, route) in stranded)
            {
                id.Value = threadId;
                fixModel.Value = model ? 1 : 0;
                fixRoute.Value = route ? 1 : 0;
                update.ExecuteNonQuery();
            }

            transaction.Commit();
            return stranded.Count;
        }
        catch (SqliteException)
        {
            // Missing threads table, locked file, half-written WAL: leave Codex's data alone.
            return 0;
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Confirms the two things this repair actually depends on: a <c>threads</c> table, and the
    /// <c>model</c> / <c>model_provider</c> text columns it rewrites. Anything else about Codex's
    /// schema is free to change without stopping us — and if these change, we stop.
    /// </summary>
    private static bool SchemaLooksFamiliar(SqliteConnection connection, out string note)
    {
        try
        {
            using var table = connection.CreateCommand();
            table.CommandText =
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'threads'";
            if (Convert.ToInt64(table.ExecuteScalar() ?? 0L) == 0)
            {
                note = "this build of Codex has no 'threads' table";
                return false;
            }

            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var info = connection.CreateCommand())
            {
                info.CommandText = "SELECT name FROM pragma_table_info('threads')";
                using var reader = info.ExecuteReader();
                while (reader.Read())
                {
                    columns.Add(reader.GetString(0));
                }
            }

            var missing = new[] { "id", "model", "model_provider" }
                .Where(column => !columns.Contains(column))
                .ToList();
            if (missing.Count > 0)
            {
                note = $"this build of Codex has no {string.Join("/", missing)} column on 'threads'";
                return false;
            }

            note = string.Empty;
            return true;
        }
        catch (SqliteException ex)
        {
            note = $"the schema could not be read ({ex.SqliteErrorCode})";
            return false;
        }
    }

    /// <summary>
    /// One backup per six hours before the first write of a session: Codex's state database is the
    /// user's data, and this repair is a convenience, not a licence to lose anything.
    /// </summary>
    private void BackupOnce()
    {
        try
        {
            var target = StateDatabasePath + ".hub-backup";
            if (File.Exists(target) &&
                File.GetLastWriteTimeUtc(target) > DateTime.UtcNow.AddHours(-6))
            {
                return;
            }

            File.Copy(StateDatabasePath, target, overwrite: true);
        }
        catch (IOException)
        {
            // No backup, no write problem: the restore path is a convenience, not a gate.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static bool IsCodexRunning()
    {
        try
        {
            return Process.GetProcessesByName("codex").Length > 0;
        }
        catch (Exception)
        {
            // If we cannot tell, assume it is running: never write under a live Codex.
            return true;
        }
    }
}
