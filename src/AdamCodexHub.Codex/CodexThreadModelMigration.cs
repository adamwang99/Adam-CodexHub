using System.Diagnostics;
using Microsoft.Data.Sqlite;

namespace AdamCodexHub.Codex;

/// <summary>
/// Repairs the per-thread state Codex keeps in <c>~/.codex/state_5.sqlite</c> so a chat keeps working
/// after the hub changes which provider is in use.
///
/// Two independent problems live in that table, both measured on 2026-09-11:
///
/// <list type="number">
/// <item><b>Model pin.</b> A thread stores the model it last ran with, so switching provider strands it
/// on an id the new provider does not offer and Codex refuses the next turn ("The 'claude-5.5' model is
/// not supported when using Codex with a ChatGPT account"). Fixed by rewriting <c>model</c> /
/// <c>model_provider</c>.</item>
/// <item><b>Permission profile.</b> <c>approval_mode</c> and <c>sandbox_policy</c> are stored as a
/// <i>pair</i>. Every working chat measured carries <c>on-request</c> + <c>managed</c> (or
/// <c>danger-full-access</c>); the two chats that offered the model <b>zero tools</b> — the model could
/// only see a clock and answered "I have no exec here" — carried <c>never</c> +
/// <c>{"type":"disabled"}</c>, with every other column (source, project, cwd, memory mode) identical.
/// A chat with no tools cannot do any work at all, which is exactly the symptom that kept coming back
/// as "the window does nothing".</item>
/// </list>
///
/// Safety, in order of importance: the file belongs to Codex, so only those four columns are ever
/// written and the transcripts are untouched; the schema this depends on is <i>verified</i> before any
/// write and the repair stands down with a log line if it does not match; a backup is taken first; the
/// write happens under a busy timeout because Codex holds the same file open; and every failure path
/// returns 0 instead of throwing. A return value of 0 means "nothing was changed".
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

    /// <summary>The <c>sandbox_policy</c> half of the profile that measured zero tools.</summary>
    public const string ToolLessPolicy = "{\"type\":\"disabled\"}";

    /// <summary>The <c>approval_mode</c> half of the profile that measured zero tools.</summary>
    public const string ToolLessApproval = "never";

    /// <summary>What working chats carry instead: no sandbox restriction and no approval prompts.</summary>
    public const string WorkingPolicy = "{\"type\":\"danger-full-access\"}";

    /// <summary>Paired with <see cref="WorkingPolicy"/>; the two are only ever written together.</summary>
    public const string WorkingApproval = "on-request";

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
    /// ChatGPT account") even though the config points at the gateway. Threads sitting on the
    /// profile that offers no tools are moved onto the working pair in the same pass.
    /// Returns the number of threads changed (0 = nothing to do, or the write was skipped).
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
            if (!ReadSchema(connection, out var hasPermissions, out var schemaNote))
            {
                Log?.Invoke(
                    $"Threads: leaving Codex's database untouched — {schemaNote}. " +
                    "Switching provider still works; the open chats may need a new chat.");
                return 0;
            }

            var stranded = new List<StrandedThread>();
            using (var select = connection.CreateCommand())
            {
                select.CommandText = hasPermissions
                    ? "SELECT id, model, model_provider, approval_mode, sandbox_policy FROM threads " +
                      "WHERE model IS NOT NULL AND TRIM(model) <> ''"
                    : "SELECT id, model, model_provider FROM threads " +
                      "WHERE model IS NOT NULL AND TRIM(model) <> ''";
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

                    var toolLess = false;
                    if (hasPermissions)
                    {
                        var approval = reader.IsDBNull(3) ? string.Empty : reader.GetString(3).Trim();
                        var policy = reader.IsDBNull(4) ? string.Empty : reader.GetString(4).Trim();
                        toolLess = IsToolLessProfile(approval, policy);
                    }

                    if (wrongModel || wrongRoute || toolLess)
                    {
                        stranded.Add(new StrandedThread(reader.GetString(0), wrongModel, wrongRoute, toolLess));
                    }
                }
            }

            if (stranded.Count == 0)
            {
                return 0;
            }

            var moved = stranded.Count(thread => thread.Model || thread.Route);
            var permissionFixed = stranded.Count(thread => thread.Permissions);

            Log?.Invoke(
                (running ? "Codex is running — " : string.Empty) +
                $"repairing {stranded.Count} thread(s) pinned to a model this provider does not offer " +
                $"(→ {targetModel})." +
                (permissionFixed > 0
                    ? $" {permissionFixed} of them had the permission profile that offers no tools " +
                      $"({ToolLessApproval} + {ToolLessPolicy}) and were moved to {WorkingApproval} + " +
                      $"{WorkingPolicy}."
                    : string.Empty));

            using var transaction = connection.BeginTransaction();
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = hasPermissions
                ? "UPDATE threads SET " +
                  "model = CASE WHEN $fixModel = 1 THEN $model ELSE model END, " +
                  "model_provider = CASE WHEN $fixRoute = 1 THEN $provider ELSE model_provider END, " +
                  "approval_mode = CASE WHEN $fixPerm = 1 THEN $approval ELSE approval_mode END, " +
                  "sandbox_policy = CASE WHEN $fixPerm = 1 THEN $policy ELSE sandbox_policy END " +
                  "WHERE id = $id"
                : "UPDATE threads SET " +
                  "model = CASE WHEN $fixModel = 1 THEN $model ELSE model END, " +
                  "model_provider = CASE WHEN $fixRoute = 1 THEN $provider ELSE model_provider END " +
                  "WHERE id = $id";
            update.Parameters.Add("$model", SqliteType.Text).Value = targetModel;
            update.Parameters.Add("$provider", SqliteType.Text).Value = targetProvider ?? string.Empty;
            var fixModel = update.Parameters.Add("$fixModel", SqliteType.Integer);
            var fixRoute = update.Parameters.Add("$fixRoute", SqliteType.Integer);
            var id = update.Parameters.Add("$id", SqliteType.Text);
            SqliteParameter? fixPerm = null;
            if (hasPermissions)
            {
                // Written as a pair, always: changing only sandbox_policy while approval_mode stayed
                // "never" was measured being normalised straight back by Codex, which is why an earlier
                // attempt at this looked like it had no effect.
                update.Parameters.Add("$approval", SqliteType.Text).Value = WorkingApproval;
                update.Parameters.Add("$policy", SqliteType.Text).Value = WorkingPolicy;
                fixPerm = update.Parameters.Add("$fixPerm", SqliteType.Integer);
            }

            foreach (var thread in stranded)
            {
                id.Value = thread.Id;
                fixModel.Value = thread.Model ? 1 : 0;
                fixRoute.Value = thread.Route ? 1 : 0;
                if (fixPerm is not null)
                {
                    fixPerm.Value = thread.Permissions ? 1 : 0;
                }

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
    /// The profile that measured zero tools: Codex Desktop's <c>Full access</c> chip writes exactly
    /// this pair, so a chat the user meant to give MORE freedom to ends up unable to run anything.
    /// Matched as a pair, because either half on its own is legitimate elsewhere.
    /// </summary>
    private static bool IsToolLessProfile(string approval, string policy) =>
        string.Equals(approval, ToolLessApproval, StringComparison.Ordinal) &&
        Normalise(policy) == Normalise(ToolLessPolicy);

    /// <summary>Whitespace-insensitive compare — the same JSON is stored with different spacing.</summary>
    private static string Normalise(string json) =>
        new(json.Where(character => !char.IsWhiteSpace(character)).ToArray());

    private readonly record struct StrandedThread(string Id, bool Model, bool Route, bool Permissions);

    /// <summary>
    /// Confirms the columns this repair actually depends on, and reports separately whether the
    /// permission columns are available (older Codex homes and the test fixtures may not have them).
    /// </summary>
    private static bool ReadSchema(SqliteConnection connection, out bool hasPermissions, out string note)
    {
        hasPermissions = false;
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

            hasPermissions = columns.Contains("approval_mode") && columns.Contains("sandbox_policy");
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
