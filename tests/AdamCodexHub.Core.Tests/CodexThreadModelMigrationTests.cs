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
}
