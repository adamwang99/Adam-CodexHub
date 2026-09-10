using AdamCodexHub.Codex;
using AdamCodexHub.Core.Domain;
using AdamCodexHub.Core.Interfaces;
using AdamCodexHub.Core.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AdamCodexHub.Core.Tests;

/// <summary>
/// Codex → hub sync: Codex Desktop's "Select model" picker stores the choice per thread inside
/// ~/.codex/state_5.sqlite and never rewrites ~/.codex/config.toml, so the hub has to read that
/// state back and mirror it into the Home card and the tray menu.
/// </summary>
public sealed class CodexSessionModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 10, 19, 20, 0, TimeSpan.Zero);

    [Fact]
    public void Reader_ReadsModelOfTheNewestThread()
    {
        var dir = CreateStateDatabase(
            ("aaa", "claude-5.5", "adam_codexhub", 1_700_000_000_000L, 0),
            ("bbb", "claude-opus-4-8[1M]", "adam_codexhub", 1_700_000_500_000L, 0));
        try
        {
            var session = new CodexSessionModelReader(dir).Read();

            Assert.NotNull(session);
            Assert.Equal("claude-opus-4-8[1M]", session!.ModelId);
            Assert.Equal("bbb", session.ThreadId);
            Assert.Equal("adam_codexhub", session.ProviderId);
            Assert.Equal(CodexSessionModel.CodexSessionSource, session.Source);
            Assert.True(session.FromCodex);
            Assert.Equal(
                DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_500_000L).ToLocalTime(),
                session.ObservedAt);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Reader_SkipsArchivedThreadsAndBlankModels()
    {
        var dir = CreateStateDatabase(
            ("live", "claude-sonnet-5", "adam_codexhub", 1_700_000_000_000L, 0),
            ("archived", "claude-5.5", "adam_codexhub", 1_700_000_900_000L, 1),
            ("blank", "   ", "adam_codexhub", 1_700_000_950_000L, 0));
        try
        {
            var session = new CodexSessionModelReader(dir).Read();

            Assert.Equal("claude-sonnet-5", session?.ModelId);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Reader_ReturnsNullWhenCodexHasNoState()
    {
        var missing = Path.Combine(Path.GetTempPath(), "codex-missing-" + Guid.NewGuid().ToString("N"));

        Assert.Null(new CodexSessionModelReader(missing).Read());
    }

    [Fact]
    public void Reader_ReturnsNullWhenThreadsTableIsMissing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "codex-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using (var connection = new SqliteConnection(
                $"Data Source={Path.Combine(dir, "state_5.sqlite")};Pooling=False"))
            {
                connection.Open();
            }

            Assert.Null(new CodexSessionModelReader(dir).Read());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Watch_PublishesCodexModelOncePerChange()
    {
        var reader = new FakeReader { Next = Session("claude-5.5") };
        using var watch = new CodexSessionModelWatchService(reader);
        var raised = new List<string?>();
        watch.Changed += (_, session) => raised.Add(session?.ModelId);

        watch.Poll();
        watch.Poll();

        Assert.Equal("claude-5.5", watch.Current?.ModelId);
        Assert.Equal(new[] { "claude-5.5" }, raised);

        reader.Next = Session("claude-opus-4-7");
        watch.Poll();

        Assert.Equal("claude-opus-4-7", watch.Current?.ModelId);
        Assert.Equal(new[] { "claude-5.5", "claude-opus-4-7" }, raised);
    }

    [Fact]
    public void Watch_PrefersPendingHubActivationUntilCodexCatchesUp()
    {
        var reader = new FakeReader { Next = Session("claude-opus-4-8[1M]") };
        using var watch = new CodexSessionModelWatchService(reader);

        watch.NoteHubActivation("claude-5.5", Now);

        Assert.Equal("claude-5.5", watch.Current?.ModelId);
        Assert.Equal(CodexSessionModel.HubActivationSource, watch.Current?.Source);

        // Codex reports the same model on its next turn: the pending value is dropped.
        reader.Next = Session("claude-5.5");
        watch.Poll();

        Assert.Equal("claude-5.5", watch.Current?.ModelId);
        Assert.Equal(CodexSessionModel.CodexSessionSource, watch.Current?.Source);
    }

    [Fact]
    public void Watch_DropsPendingWhenCodexMovesOnByItself()
    {
        var reader = new FakeReader { Next = Session("claude-opus-4-8[1M]", Now.AddSeconds(-30)) };
        using var watch = new CodexSessionModelWatchService(reader);

        watch.NoteHubActivation("claude-5.5", Now);
        Assert.Equal(CodexSessionModel.HubActivationSource, watch.Current?.Source);

        // A newer Codex session with a different model wins: the user switched inside Codex again.
        reader.Next = Session("claude-sonnet-5", Now.AddMinutes(1));
        watch.Poll();

        Assert.Equal("claude-sonnet-5", watch.Current?.ModelId);
        Assert.Equal(CodexSessionModel.CodexSessionSource, watch.Current?.Source);
    }

    [Fact]
    public void Watch_StaysSilentWhileThereIsNoCodexSession()
    {
        var reader = new FakeReader();
        using var watch = new CodexSessionModelWatchService(reader);
        var fired = 0;
        watch.Changed += (_, _) => fired++;

        watch.Poll();
        watch.Poll();

        Assert.Null(watch.Current);
        Assert.Equal(0, fired);
        Assert.Equal(2, reader.Reads);
    }

    private static CodexSessionModel Session(string model, DateTimeOffset? at = null) =>
        new(model, "thread-1", "adam_codexhub", at ?? Now, CodexSessionModel.CodexSessionSource);

    /// <summary>
    /// End-to-end: the real reader + the real poll loop must follow a change written into Codex's
    /// state database on their own (this is what makes an in-Codex model switch show up in the hub).
    /// </summary>
    [Fact]
    public async Task Watch_FollowsAChangeWrittenIntoCodexState()
    {
        var dir = CreateStateDatabase(("t1", "claude-opus-4-7[1M]", "adam_codexhub", 1_700_000_000_000L, 0));
        try
        {
            var reader = new CodexSessionModelReader(dir);
            using var watch = new CodexSessionModelWatchService(reader, TimeSpan.FromMilliseconds(150));

            watch.Start();
            Assert.Equal("claude-opus-4-7[1M]", await WaitForModelAsync(watch, "claude-opus-4-7[1M]"));

            var switched = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            watch.Changed += (_, session) => switched.TrySetResult(session?.ModelId);

            WriteNewestThreadModel(dir, "claude-5.5", 1_700_000_900_000L);

            var published = await switched.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("claude-5.5", published);
            Assert.True(watch.Current?.FromCodex);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException) { /* sqlite handle still closing — temp dir only */ }
        }
    }

    private static async Task<string?> WaitForModelAsync(CodexSessionModelWatchService watch, string expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (string.Equals(watch.Current?.ModelId, expected, StringComparison.Ordinal))
            {
                return expected;
            }

            await Task.Delay(50);
        }

        return watch.Current?.ModelId;
    }

    private static void WriteNewestThreadModel(string codexHome, string model, long updatedMs)
    {
        using var connection = new SqliteConnection(
            $"Data Source={Path.Combine(codexHome, "state_5.sqlite")};Pooling=False");
        connection.Open();
        using var update = connection.CreateCommand();
        update.CommandText =
            "UPDATE threads SET model = $model, updated_at_ms = $ms, updated_at = $seconds WHERE id = 't1'";
        update.Parameters.AddWithValue("$model", model);
        update.Parameters.AddWithValue("$ms", updatedMs);
        update.Parameters.AddWithValue("$seconds", updatedMs / 1000);
        update.ExecuteNonQuery();
    }

    private static string CreateStateDatabase(
        params (string Id, string Model, string Provider, long UpdatedMs, int Archived)[] rows)
    {
        var dir = Path.Combine(Path.GetTempPath(), "codex-session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        using var connection = new SqliteConnection(
            $"Data Source={Path.Combine(dir, "state_5.sqlite")};Pooling=False");
        connection.Open();
        using (var schema = connection.CreateCommand())
        {
            schema.CommandText =
                "CREATE TABLE threads (id TEXT PRIMARY KEY, model TEXT, model_provider TEXT, " +
                "updated_at INTEGER, updated_at_ms INTEGER, created_at INTEGER, " +
                "archived INTEGER NOT NULL DEFAULT 0)";
            schema.ExecuteNonQuery();
        }

        foreach (var row in rows)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText =
                "INSERT INTO threads (id, model, model_provider, updated_at, updated_at_ms, created_at, archived) " +
                "VALUES ($id, $model, $provider, $seconds, $ms, $seconds, $archived)";
            insert.Parameters.AddWithValue("$id", row.Id);
            insert.Parameters.AddWithValue("$model", row.Model);
            insert.Parameters.AddWithValue("$provider", row.Provider);
            insert.Parameters.AddWithValue("$seconds", row.UpdatedMs / 1000);
            insert.Parameters.AddWithValue("$ms", row.UpdatedMs);
            insert.Parameters.AddWithValue("$archived", row.Archived);
            insert.ExecuteNonQuery();
        }

        return dir;
    }

    private sealed class FakeReader : ICodexSessionModelReader
    {
        public CodexSessionModel? Next { get; set; }

        public int Reads { get; private set; }

        public CodexSessionModel? Read()
        {
            Reads++;
            return Next;
        }
    }
}
