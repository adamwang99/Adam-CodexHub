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
/// the model column (transcripts are untouched), and every failure path returns 0 instead of
/// throwing. A return value of 0 means "nothing was changed".
/// </summary>
public sealed class CodexThreadModelMigration
{
    /// <summary>Codex's internal reviewer model — every provider can run it, so never rewrite it.</summary>
    public const string InternalModelId = "codex-auto-review";

    /// <summary>
    /// Codex home to repair — the hub passes its configured `CODEX_HOME`. Left empty this helper
    /// touches nothing on purpose: a migration that guesses a home is a migration that can rewrite
    /// the wrong database.
    /// </summary>
    public string? CodexHome { get; init; }

    /// <summary>Test seam; the real process probe is used when this is null.</summary>
    public Func<bool>? CodexIsRunning { get; init; }

    public string StateDatabasePath => Path.Combine(CodexHome ?? string.Empty, "state_5.sqlite");

    /// <summary>
    /// Rewrites every thread whose model is not in <paramref name="offeredModels"/> to
    /// <paramref name="targetModel"/>. Returns the number of threads moved (0 = nothing to do, or
    /// the write was skipped).
    /// </summary>
    public int Migrate(IReadOnlyCollection<string>? offeredModels, string? targetModel)
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
        if (!offered.Contains(targetModel) || (CodexIsRunning ?? IsCodexRunning)())
        {
            return 0;
        }

        if (!File.Exists(StateDatabasePath))
        {
            return 0;
        }

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

            var stranded = new List<string>();
            using (var select = connection.CreateCommand())
            {
                select.CommandText =
                    "SELECT id, model FROM threads WHERE model IS NOT NULL AND TRIM(model) <> ''";
                using var reader = select.ExecuteReader();
                while (reader.Read())
                {
                    if (!offered.Contains(reader.GetString(1).Trim()))
                    {
                        stranded.Add(reader.GetString(0));
                    }
                }
            }

            if (stranded.Count == 0)
            {
                return 0;
            }

            using var transaction = connection.BeginTransaction();
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE threads SET model = $model WHERE id = $id";
            update.Parameters.Add("$model", SqliteType.Text).Value = targetModel;
            var id = update.Parameters.Add("$id", SqliteType.Text);
            foreach (var threadId in stranded)
            {
                id.Value = threadId;
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
