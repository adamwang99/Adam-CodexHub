using System.Text;
using System.Text.Json;
using AdamCodexHub.Codex;
using Xunit;

namespace AdamCodexHub.Core.Tests;

/// <summary>
/// Covers the provider-switch hand-off: finding the project the user works in and turning the
/// previous rollout into the recap that the fresh Codex chat receives.
/// </summary>
public sealed class CodexContinuationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "AdamCodexHub.Tests",
        Guid.NewGuid().ToString("N"));

    private readonly string _codexHome;

    public CodexContinuationTests()
    {
        _codexHome = Path.Combine(_root, ".codex");
        Directory.CreateDirectory(_codexHome);
    }

    [Fact]
    public void ResolveWorkspacePrefersProjectSelectedInTheApp()
    {
        var selected = CreateProjectDir("Test Video Factory");
        var newer = CreateProjectDir("Other Project");
        WriteState(
            ("11111111-1111-1111-1111-111111111111", "Test Video Factory", selected, 1_700_000_000_000),
            ("22222222-2222-2222-2222-222222222222", "Other Project", newer, 1_800_000_000_000));
        WriteStateFile(selectedProjectId: "11111111-1111-1111-1111-111111111111");

        var workspace = CodexDesktopState.ResolveWorkspace(_codexHome);

        Assert.NotNull(workspace);
        Assert.Equal(selected, workspace!.RootPath);
        Assert.Equal("Test Video Factory", workspace.DisplayName);
    }

    [Fact]
    public void ResolveWorkspaceFallsBackToNewestProjectWhenSelectionIsGone()
    {
        var stale = Path.Combine(_root, "deleted-project");
        var alive = CreateProjectDir("Alive");
        WriteState(
            ("11111111-1111-1111-1111-111111111111", "Deleted", stale, 1_800_000_000_000),
            ("22222222-2222-2222-2222-222222222222", "Alive", alive, 1_700_000_000_000));
        WriteStateFile(selectedProjectId: "11111111-1111-1111-1111-111111111111");

        var workspace = CodexDesktopState.ResolveWorkspace(_codexHome);

        Assert.NotNull(workspace);
        Assert.Equal(alive, workspace!.RootPath);
    }

    [Fact]
    public void ReadLastTurnsKeepsRecentChatAndDropsInjectedContext()
    {
        var project = CreateProjectDir("Test Video Factory");
        var rollout = WriteRollout(project, new (string Role, string Text)[]
        {
            ("user", "<environment_context>cwd=/x</environment_context>"),
            ("user", "câu hỏi 1"),
            ("assistant", "trả lời 1"),
            ("user", "câu hỏi 2"),
            ("assistant", "trả lời 2")
        });

        var turns = CodexHandoffBuilder.ReadLastTurns(rollout, maxTurns: 3);

        Assert.Equal(3, turns.Count);
        Assert.Equal("trả lời 1", turns[0].Text);
        Assert.Equal("câu hỏi 2", turns[1].Text);
        Assert.Equal("assistant", turns[2].Role);
    }

    [Fact]
    public void BuildHandsOverProjectPathAndRecapInBothLanguages()
    {
        var project = CreateProjectDir("Test Video Factory");
        var workspace = new CodexWorkspace(project, "Test Video Factory");
        var turns = new[] { new CodexSessionTurn("user", "làm nốt phần export") };

        var vi = CodexHandoffBuilder.Build(workspace, turns, "HHTech API", vietnamese: true);
        var en = CodexHandoffBuilder.Build(workspace, turns, "HHTech API", vietnamese: false);

        Assert.Contains(project, vi, StringComparison.Ordinal);
        Assert.Contains("HHTech API", vi, StringComparison.Ordinal);
        Assert.Contains("làm nốt phần export", vi, StringComparison.Ordinal);
        Assert.Contains("chat MỚI", vi, StringComparison.Ordinal);

        Assert.Contains(project, en, StringComparison.Ordinal);
        Assert.Contains("NEW chat", en, StringComparison.Ordinal);
        Assert.DoesNotContain("chat MỚI", en, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildWorksWithoutAnyPreviousTurns()
    {
        var project = CreateProjectDir("Empty");
        var workspace = new CodexWorkspace(project, "Empty");

        var prompt = CodexHandoffBuilder.Build(workspace, Array.Empty<CodexSessionTurn>(), null, true);

        Assert.Contains(project, prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Tóm tắt", prompt, StringComparison.Ordinal);
    }

    private string CreateProjectDir(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private void WriteState(params (string Id, string Name, string Root, long UpdatedAt)[] projects)
    {
        var map = new Dictionary<string, object>();
        foreach (var project in projects)
        {
            map[project.Id] = new
            {
                id = project.Id,
                name = project.Name,
                rootPaths = new[] { project.Root },
                createdAt = project.UpdatedAt,
                updatedAt = project.UpdatedAt
            };
        }

        _projects = map;
    }

    private Dictionary<string, object> _projects = new();

    private void WriteStateFile(string selectedProjectId)
    {
        var payload = new Dictionary<string, object>
        {
            ["selected-project"] = new { type = "local", projectId = selectedProjectId },
            ["local-projects"] = _projects
        };

        File.WriteAllText(
            Path.Combine(_codexHome, ".codex-global-state.json"),
            JsonSerializer.Serialize(payload));
    }

    private string WriteRollout(string cwd, IReadOnlyList<(string Role, string Text)> messages)
    {
        var dir = Path.Combine(_codexHome, "sessions", "2026", "09", "10");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"rollout-2026-09-10T10-41-58-{Guid.NewGuid():N}.jsonl");

        var builder = new StringBuilder();
        builder.AppendLine(JsonSerializer.Serialize(new
        {
            timestamp = "2026-09-10T10:41:58.000Z",
            type = "session_meta",
            payload = new { id = Guid.NewGuid().ToString(), cwd, model_provider = "adam_codexhub" }
        }));

        foreach (var (role, text) in messages)
        {
            builder.AppendLine(JsonSerializer.Serialize(new
            {
                timestamp = "2026-09-10T10:42:00.000Z",
                type = "response_item",
                payload = new
                {
                    type = "message",
                    role,
                    content = new[] { new { type = role == "user" ? "input_text" : "output_text", text } }
                }
            }));
        }

        File.WriteAllText(path, builder.ToString());
        return path;
    }

    [Fact]
    public void MarkerFlattensTheRecapToASingleLineWithoutLosingCharacters()
    {
        // The marker is matched against the decoded message, so quotes and backslashes have to
        // survive; only line breaks would break a substring check.
        var marker = CodexDesktopBridge.BuildMarker("Resume\nthe \"session\"\r\\now");

        Assert.Equal("Resumethe \"session\"\\now", marker);
    }

    [Fact]
    public void MarkerIsTrimmedAndCappedSoItStaysMatchable()
    {
        Assert.Equal("hello", CodexDesktopBridge.BuildMarker("  hello  "));
        Assert.Equal("abcdefghij", CodexDesktopBridge.BuildMarker("abcdefghijklmnop", length: 10));
        Assert.Equal(string.Empty, CodexDesktopBridge.BuildMarker(string.Empty));
    }

    [Fact]
    public void NewChatIsRecognisedOnlyWhenItsRolloutAppearedAfterTheHandoffStarted()
    {
        var cwd = CreateProjectDir("Test Video Factory");
        WriteRollout(cwd, new[] { ("user", "previous chat content") });
        var knownRollouts = CodexDesktopState.SnapshotRollouts(_codexHome);

        // Nothing new yet: the recap is still in the composer.
        Assert.Null(CodexDesktopState.FindNewRolloutSince(knownRollouts, cwd, _codexHome));

        var fresh = WriteRollout(cwd, new[] { ("user", "recap: continue the previous session") });

        Assert.Equal(fresh, CodexDesktopState.FindNewRolloutSince(knownRollouts, cwd, _codexHome));
        Assert.True(CodexHandoffBuilder.RolloutContainsUserMessage(
            fresh,
            "recap: continue the previous session"));
        Assert.False(CodexHandoffBuilder.RolloutContainsUserMessage(fresh, "a message that was never sent"));

        // Once both chats are known, the hand-off has no new session to report.
        Assert.Null(CodexDesktopState.FindNewRolloutSince(
            CodexDesktopState.SnapshotRollouts(_codexHome),
            cwd,
            _codexHome));
    }

    [Fact]
    public void HandoffCheckReadsTheRecapWhileCodexStillHoldsTheRolloutOpen()
    {
        var cwd = CreateProjectDir("Live Project");
        var rollout = WriteRollout(cwd, new[] { ("user", "recap with \"quotes\" and a \\ backslash") });

        // A live session is exactly the case that matters, and Codex keeps its rollout handle
        // open while appending. A plain read fails there, so the check has to share the handle,
        // and the text is compared after decoding the JSON line.
        using var liveSession = new FileStream(
            rollout,
            FileMode.Open,
            FileAccess.Write,
            FileShare.Read);

        Assert.Throws<IOException>(() => File.ReadAllText(rollout));

        Assert.True(CodexHandoffBuilder.RolloutContainsUserMessage(rollout, "recap with \"quotes\""));
        Assert.True(CodexHandoffBuilder.RolloutContainsUserMessage(rollout, "\\ backslash"));
        Assert.False(CodexHandoffBuilder.RolloutContainsUserMessage(rollout, "not in this chat"));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Temp cleanup is best effort.
        }
    }
}
