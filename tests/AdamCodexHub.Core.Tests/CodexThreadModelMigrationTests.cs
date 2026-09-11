using AdamCodexHub.Codex;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AdamCodexHub.Core.Tests;

/// <summary>
/// Codex pins the model per thread in state_5.sqlite. When a provider is activated, every thread
/// still pinned to a model that provider does not offer has to be moved, otherwise Codex refuses
/// the next turn ("The 'claude-5.5' model is not supported when using Codex with a ChatGPT account").
/// </summary>
public sealed class CodexThreadModelMigrationTests : IDisposable
{
    private readonly string _home = Path.Combine(
        Path.GetTempPath(),
        $"adam-codexhub-thread-migration-{Guid.NewGuid():N}");

    public CodexThreadModelMigrationTests() => Directory.CreateDirectory(_home);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_home, recursive: true);
        }
        catch (IOException)
        {
            // Temp leftovers are harmless.
        }
    }

    [Fact]
    public void MovesOnlyTheThreadsTheProviderCannotOffer()
    {
        var database = SeedThreads(
            ("stranded", "claude-5.5"),
            ("keep-terra", "gpt-5.6-terra"),
            ("reviewer", CodexThreadModelMigration.InternalModelId),
            ("empty", "   "));

        var migrated = Migration().Migrate(new[] { "gpt-5.6-sol", "gpt-5.6-terra" }, "gpt-5.6-sol");

        Assert.Equal(1, migrated);
        Assert.Equal("gpt-5.6-sol", ModelOf(database, "stranded"));
        Assert.Equal("gpt-5.6-terra", ModelOf(database, "keep-terra"));
        Assert.Equal(CodexThreadModelMigration.InternalModelId, ModelOf(database, "reviewer"));
        Assert.Equal("   ", ModelOf(database, "empty"));
    }

    [Fact]
    public void StillRepairsTheThreadWhileCodexIsRunning()
    {
        // Adam, 2026-09-11: "có tác dụng ngay khi chuyển và tiếp tục làm việc trên chat cũ được ngay,
        // không cần tạo chat mới." Codex Desktop is running whenever the hub is in use, so refusing to
        // write while it runs meant a switch never reached the chats that were already open — the thread
        // kept answering "not supported when using Codex with a ChatGPT account".
        var database = SeedThreads(("stranded", "dsv4"));

        var migrated = new CodexThreadModelMigration
        {
            CodexHome = _home,
            CodexIsRunning = () => true
        }.Migrate(new[] { "gpt-5.6-sol" }, "gpt-5.6-sol");

        Assert.Equal(1, migrated);
        Assert.Equal("gpt-5.6-sol", ModelOf(database, "stranded"));
    }

    [Fact]
    public void AnUnfamiliarSchemaIsLeftAloneWithAnExplanation()
    {
        // state_5.sqlite belongs to a closed app that updates itself, and the columns rewritten here
        // were read off one build (CW 0.153.x, 2026-09-11) — they are an observation, not a contract.
        // A future CW that renames the column must cost the user nothing: no write, no exception, and
        // a log line that says why the repair stood down.
        var database = Path.Combine(_home, "state_5.sqlite");
        using (var connection = new SqliteConnection($"Data Source={database}"))
        {
            connection.Open();
            using var create = connection.CreateCommand();
            // 'model_provider' is gone — the shape this repair depends on no longer holds.
            create.CommandText =
                "CREATE TABLE threads (id TEXT PRIMARY KEY, model TEXT, route TEXT)";
            create.ExecuteNonQuery();

            using var insert = connection.CreateCommand();
            insert.CommandText =
                "INSERT INTO threads (id, model, route) VALUES ('stranded', 'claude-5.5', 'somewhere')";
            insert.ExecuteNonQuery();
        }

        var log = new List<string>();
        var migrated = new CodexThreadModelMigration
        {
            CodexHome = _home,
            CodexIsRunning = () => true,
            Log = log.Add
        }.Migrate(new[] { "gpt-5.6-sol" }, "gpt-5.6-sol", CodexThreadModelMigration.AccountProviderId);

        Assert.Equal(0, migrated);
        Assert.Contains(log, line => line.Contains("model_provider") && line.Contains("untouched"));

        // The user's row is exactly as it was.
        using var verify = new SqliteConnection($"Data Source={database}");
        verify.Open();
        using var read = verify.CreateCommand();
        read.CommandText = "SELECT model FROM threads WHERE id = 'stranded'";
        Assert.Equal("claude-5.5", read.ExecuteScalar() as string);
    }

    [Fact]
    public void AMissingThreadsTableIsReportedRatherThanGuessedAt()
    {
        var database = Path.Combine(_home, "state_5.sqlite");
        using (var connection = new SqliteConnection($"Data Source={database}"))
        {
            connection.Open();
            using var create = connection.CreateCommand();
            create.CommandText = "CREATE TABLE conversations (id TEXT PRIMARY KEY, model TEXT)";
            create.ExecuteNonQuery();
        }

        var log = new List<string>();
        var migrated = new CodexThreadModelMigration
        {
            CodexHome = _home,
            CodexIsRunning = () => false,
            Log = log.Add
        }.Migrate(new[] { "gpt-5.6-sol" }, "gpt-5.6-sol");

        Assert.Equal(0, migrated);
        Assert.Contains(log, line => line.Contains("no 'threads' table"));
    }

    [Fact]
    public void IgnoresATargetTheProviderDoesNotOffer()
    {
        var database = SeedThreads(("stranded", "dsv4"));

        var migrated = Migration().Migrate(new[] { "gpt-5.6-sol" }, "claude-5.5");

        Assert.Equal(0, migrated);
        Assert.Equal("dsv4", ModelOf(database, "stranded"));
    }

    [Fact]
    public void DoesNothingWithoutAnOfferedCatalogue()
    {
        var database = SeedThreads(("stranded", "dsv4"));

        Assert.Equal(0, Migration().Migrate(Array.Empty<string>(), "gpt-5.6-sol"));
        Assert.Equal(0, Migration().Migrate(null, "gpt-5.6-sol"));
        Assert.Equal("dsv4", ModelOf(database, "stranded"));
    }

    [Fact]
    public void MissingDatabaseIsNotAnError()
    {
        var migration = new CodexThreadModelMigration
        {
            CodexHome = Path.Combine(_home, "not-installed"),
            CodexIsRunning = () => false
        };

        Assert.Equal(0, migration.Migrate(new[] { "gpt-5.6-sol" }, "gpt-5.6-sol"));
    }

    [Fact]
    public void NoConfiguredCodexHomeMeansNothingIsEverRewritten()
    {
        // Regression guard: a migration built without an explicit home stays inert. A test run that
        // forgot to pass one once rewrote the real ~/.codex/state_5.sqlite (every thread pinned to
        // `deepseek-chat`), so the default is now "do nothing" instead of "assume ~/.codex".
        var migration = new CodexThreadModelMigration { CodexIsRunning = () => false };

        Assert.Equal(0, migration.Migrate(new[] { "gpt-5.6-sol" }, "gpt-5.6-sol"));
    }

    [Fact]
    public void AccountCatalogueComesFromTheCodexModelCache()
    {
        File.WriteAllText(
            Path.Combine(_home, "models_cache.json"),
            """
            { "models": [ { "slug": "gpt-5.6-sol" }, { "slug": "gpt-6-astra" }, { "slug": "" } ] }
            """);

        var slugs = CodexAccountCatalog.Read(_home);

        Assert.Equal(new[] { "gpt-5.6-sol", "gpt-6-astra" }, slugs);
        Assert.Empty(CodexAccountCatalog.Read(Path.Combine(_home, "not-installed")));
    }

    [Fact]
    public void MovesThreadsStillPinnedToTheChatGptAccountOntoTheGateway()
    {
        // config.toml said adam_codexhub, but the thread Adam was typing in still carried
        // model_provider "openai" — Codex answered every gateway model with "not supported when
        // using Codex with a ChatGPT account".
        var database = SeedThreadsWithProvider(
            CodexThreadModelMigration.AccountProviderId,
            ("account-thread", "gpt-6-astra"));

        var migrated = Migration().Migrate(
            new[] { "claude-5.2", "claude-k3", "deepseek-v4-pro" },
            "claude-5.2",
            CodexThreadModelMigration.GatewayProviderId);

        Assert.Equal(1, migrated);
        Assert.Equal("claude-5.2", ModelOf(database, "account-thread"));
        Assert.Equal(CodexThreadModelMigration.GatewayProviderId, ProviderOf(database, "account-thread"));
    }

    [Fact]
    public void AThreadWhoseModelIsStillOfferedOnlyFollowsTheRoute()
    {
        // The model survives the switch, so the hub must not overwrite the choice — but the thread
        // still has to stop pointing at the source we just left.
        var database = SeedThreadsWithProvider(
            CodexThreadModelMigration.AccountProviderId,
            ("account-thread", "gpt-5.6-sol"));

        var migrated = Migration().Migrate(
            new[] { "gpt-5.6-sol", "claude-5.2" },
            "claude-5.2",
            CodexThreadModelMigration.GatewayProviderId);

        Assert.Equal(1, migrated);
        Assert.Equal("gpt-5.6-sol", ModelOf(database, "account-thread"));
        Assert.Equal(CodexThreadModelMigration.GatewayProviderId, ProviderOf(database, "account-thread"));
    }

    [Fact]
    public void SwitchingBackToTheAccountPutsStrandedThreadsBackOnOpenAi()
    {
        var database = SeedThreads(("gateway-thread", "claude-5.2"));

        var migrated = Migration().Migrate(
            new[] { "gpt-5.6-sol", CodexThreadModelMigration.InternalModelId },
            "gpt-5.6-sol",
            CodexThreadModelMigration.AccountProviderId);

        Assert.Equal(1, migrated);
        Assert.Equal("gpt-5.6-sol", ModelOf(database, "gateway-thread"));
        Assert.Equal(CodexThreadModelMigration.AccountProviderId, ProviderOf(database, "gateway-thread"));
    }

    [Fact]
    public void TheProviderIsLeftAloneWhenTheCallerDoesNotNameOne()
    {
        var database = SeedThreadsWithProvider(
            CodexThreadModelMigration.AccountProviderId,
            ("account-thread", "gpt-6-astra"));

        var migrated = Migration().Migrate(new[] { "gpt-6-astra", "claude-5.2" }, "claude-5.2");

        Assert.Equal(0, migrated);
        Assert.Equal("gpt-6-astra", ModelOf(database, "account-thread"));
        Assert.Equal(CodexThreadModelMigration.AccountProviderId, ProviderOf(database, "account-thread"));
    }

    private CodexThreadModelMigration Migration() => new()
    {
        CodexHome = _home,
        CodexIsRunning = () => false
    };

    private string SeedThreads(params (string Id, string Model)[] threads) =>
        SeedThreads(CodexThreadModelMigration.GatewayProviderId, threads);

    private string SeedThreadsWithProvider(string provider, params (string Id, string Model)[] threads) =>
        SeedThreads(provider, threads);

    private string SeedThreads(string provider, params (string Id, string Model)[] threads)
    {
        var database = Path.Combine(_home, "state_5.sqlite");
        using var connection = new SqliteConnection($"Data Source={database}");
        connection.Open();

        using (var create = connection.CreateCommand())
        {
            create.CommandText =
                "CREATE TABLE IF NOT EXISTS threads (id TEXT PRIMARY KEY, model TEXT, model_provider TEXT)";
            create.ExecuteNonQuery();
        }

        foreach (var (id, model) in threads)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText =
                "INSERT INTO threads (id, model, model_provider) VALUES ($id, $model, $provider)";
            insert.Parameters.AddWithValue("$id", id);
            insert.Parameters.AddWithValue("$model", model);
            insert.Parameters.AddWithValue("$provider", provider);
            insert.ExecuteNonQuery();
        }

        return database;
    }

    private static string? ProviderOf(string database, string id)
    {
        using var connection = new SqliteConnection($"Data Source={database}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT model_provider FROM threads WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() as string;
    }

    private static string? ModelOf(string database, string id)
    {
        using var connection = new SqliteConnection($"Data Source={database}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT model FROM threads WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() as string;
    }

    private static (string? Approval, string? Policy) PermissionsOf(string database, string id)
    {
        using var connection = new SqliteConnection($"Data Source={database}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT approval_mode, sandbox_policy FROM threads WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? (reader.IsDBNull(0) ? null : reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1))
            : (null, null);
    }

    /// <summary>
    /// Seeds one thread that also carries the permission pair, i.e. a database shaped like the real
    /// one. Kept separate from <see cref="SeedThreads"/> so the older tests keep exercising a home
    /// whose table has only the three original columns.
    /// </summary>
    private string SeedThreadWithProfile(string id, string model, string approval, string policy)
    {
        var database = Path.Combine(_home, "state_5.sqlite");
        using var connection = new SqliteConnection($"Data Source={database}");
        connection.Open();

        using (var create = connection.CreateCommand())
        {
            create.CommandText =
                "CREATE TABLE IF NOT EXISTS threads (id TEXT PRIMARY KEY, model TEXT, " +
                "model_provider TEXT, approval_mode TEXT, sandbox_policy TEXT)";
            create.ExecuteNonQuery();
        }

        using var insert = connection.CreateCommand();
        insert.CommandText =
            "INSERT INTO threads (id, model, model_provider, approval_mode, sandbox_policy) " +
            "VALUES ($id, $model, $provider, $approval, $policy)";
        insert.Parameters.AddWithValue("$id", id);
        insert.Parameters.AddWithValue("$model", model);
        insert.Parameters.AddWithValue("$provider", CodexThreadModelMigration.GatewayProviderId);
        insert.Parameters.AddWithValue("$approval", approval);
        insert.Parameters.AddWithValue("$policy", policy);
        insert.ExecuteNonQuery();
        return database;
    }

    [Fact]
    public void AToolLessPermissionProfileIsMovedOntoTheWorkingPair()
    {
        // Measured 2026-09-11: the two chats that offered the model ZERO tools — it could only see a
        // clock and answered "I have no exec here" — carried approval_mode "never" with
        // sandbox_policy {"type":"disabled"}, while every working chat carried the other pair. Codex
        // Desktop's own "Full access" chip writes the tool-less one, so the user asking for more
        // freedom is exactly how a chat becomes unable to do anything.
        var database = SeedThreadWithProfile(
            "tool-less",
            "gpt-5.6-sol",
            CodexThreadModelMigration.ToolLessApproval,
            CodexThreadModelMigration.ToolLessPolicy);

        var migrated = Migration().Migrate(new[] { "gpt-5.6-sol" }, "gpt-5.6-sol");

        Assert.Equal(1, migrated);
        var (approval, policy) = PermissionsOf(database, "tool-less");
        Assert.Equal(CodexThreadModelMigration.WorkingApproval, approval);
        Assert.Equal(CodexThreadModelMigration.WorkingPolicy, policy);
    }

    [Fact]
    public void ThePermissionPairIsAlwaysWrittenTogether()
    {
        // Changing only sandbox_policy while approval_mode stayed "never" was measured being
        // normalised straight back by Codex, which is what made an earlier attempt at this look like
        // it had no effect at all.
        var database = SeedThreadWithProfile(
            "tool-less",
            "gpt-5.6-sol",
            CodexThreadModelMigration.ToolLessApproval,
            CodexThreadModelMigration.ToolLessPolicy);

        Migration().Migrate(new[] { "gpt-5.6-sol" }, "gpt-5.6-sol");

        var (approval, policy) = PermissionsOf(database, "tool-less");
        Assert.NotEqual(CodexThreadModelMigration.ToolLessApproval, approval);
        Assert.NotEqual(CodexThreadModelMigration.ToolLessPolicy, policy);
    }

    [Fact]
    public void AWorkingPermissionProfileIsLeftAlone()
    {
        // Only the combination that was actually measured is rewritten. A chat on managed + on-request
        // works, so the repair has no business touching it.
        var database = SeedThreadWithProfile(
            "working",
            "gpt-5.6-sol",
            "on-request",
            "{\"type\":\"managed\",\"file_system\":{\"type\":\"restricted\",\"entries\":[]}}");

        var migrated = Migration().Migrate(new[] { "gpt-5.6-sol" }, "gpt-5.6-sol");

        Assert.Equal(0, migrated);
        var (approval, policy) = PermissionsOf(database, "working");
        Assert.Equal("on-request", approval);
        Assert.Contains("managed", policy);
    }

    [Fact]
    public void HalfOfThePairIsNotEnoughToTriggerARewrite()
    {
        // "never" on its own is legitimate (many working threads carry it with a managed policy), so
        // matching either half alone would rewrite chats nobody measured as broken.
        var database = SeedThreadWithProfile(
            "never-but-managed",
            "gpt-5.6-sol",
            CodexThreadModelMigration.ToolLessApproval,
            "{\"type\":\"managed\",\"file_system\":{\"type\":\"restricted\",\"entries\":[]}}");

        var migrated = Migration().Migrate(new[] { "gpt-5.6-sol" }, "gpt-5.6-sol");

        Assert.Equal(0, migrated);
        Assert.Equal(CodexThreadModelMigration.ToolLessApproval, PermissionsOf(database, "never-but-managed").Approval);
    }

    [Fact]
    public void AThreadCanNeedBothRepairsInOnePass()
    {
        // The realistic case after a provider switch: the model is stranded AND the profile is the
        // tool-less one. Both have to land, or the chat is still unusable for one reason or another.
        var database = SeedThreadWithProfile(
            "both",
            "claude-5.5",
            CodexThreadModelMigration.ToolLessApproval,
            CodexThreadModelMigration.ToolLessPolicy);

        var migrated = Migration().Migrate(new[] { "gpt-5.6-sol" }, "gpt-5.6-sol");

        Assert.Equal(1, migrated);
        Assert.Equal("gpt-5.6-sol", ModelOf(database, "both"));
        var (approval, policy) = PermissionsOf(database, "both");
        Assert.Equal(CodexThreadModelMigration.WorkingApproval, approval);
        Assert.Equal(CodexThreadModelMigration.WorkingPolicy, policy);
    }

    [Fact]
    public void AHomeWithoutThePermissionColumnsStillGetsTheModelRepair()
    {
        // Older Codex homes (and the three-column fixtures above) have no approval_mode at all. That
        // must downgrade the repair, not disable it.
        var database = SeedThreads(("stranded", "claude-5.5"));

        var migrated = Migration().Migrate(new[] { "gpt-5.6-sol" }, "gpt-5.6-sol");

        Assert.Equal(1, migrated);
        Assert.Equal("gpt-5.6-sol", ModelOf(database, "stranded"));
    }
}
