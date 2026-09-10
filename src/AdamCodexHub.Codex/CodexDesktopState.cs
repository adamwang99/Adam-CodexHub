using System.Text.Json;

namespace AdamCodexHub.Codex;

/// <summary>A project folder that Codex Desktop keeps in its sidebar.</summary>
public sealed record CodexWorkspace(string RootPath, string? DisplayName);

/// <summary>
/// Read-only access to the state Codex Desktop persists under ~/.codex
/// (<c>.codex-global-state.json</c> plus the <c>sessions/</c> rollout log). The hub only ever reads
/// it: which project the user is working in and which chat ran last, so that switching provider can
/// continue the work in a FRESH chat instead of an old thread that is pinned to the previous
/// provider (that is what makes an old chat answer with the ChatGPT account's usage limit even
/// though the new provider still has quota).
/// </summary>
public static class CodexDesktopState
{
    /// <summary>Root of a rollout file that carries the session metadata.</summary>
    private const string RolloutPrefix = "rollout-";

    public static string DefaultHome { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".codex");

    public static string ResolveHome(string? codexHome) =>
        string.IsNullOrWhiteSpace(codexHome) ? DefaultHome : codexHome!;

    /// <summary>
    /// Best guess of the project the user is working in: the project selected in the Desktop app,
    /// else the cwd of the newest Codex session, else the most recently used project folder.
    /// </summary>
    public static CodexWorkspace? ResolveWorkspace(string? codexHome = null)
    {
        var home = ResolveHome(codexHome);

        var selected = GetActiveWorkspace(home);
        if (selected is not null && Directory.Exists(selected.RootPath))
        {
            return selected;
        }

        var fromSessions = GetLastSessionWorkspace(home);
        if (fromSessions is not null)
        {
            return fromSessions;
        }

        // The selected project can point at a folder that no longer exists (renamed, deleted,
        // worktree removed) — never hand Codex a dead path.
        return GetMostRecentWorkspace(home);
    }

    /// <summary>Project currently selected in the Desktop app, or null when it cannot be read.</summary>
    public static CodexWorkspace? GetActiveWorkspace(string? codexHome = null)
    {
        var state = TryReadState(codexHome);
        if (state is null)
        {
            return null;
        }

        try
        {
            var root = state.Value;
            if (!TryGet(root, "selected-project", out var selected) ||
                !TryGet(selected, "projectId", out var idElement) ||
                idElement.GetString() is not { Length: > 0 } id ||
                !TryGet(root, "local-projects", out var projects) ||
                !TryGet(projects, id, out var project))
            {
                return null;
            }

            return ReadProject(project);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Most recently touched project folder in the Desktop sidebar.</summary>
    public static CodexWorkspace? GetMostRecentWorkspace(string? codexHome = null)
    {
        var state = TryReadState(codexHome);
        if (state is null)
        {
            return null;
        }

        try
        {
            if (!TryGet(state.Value, "local-projects", out var projects) ||
                projects.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            CodexWorkspace? best = null;
            var bestStamp = long.MinValue;

            foreach (var property in projects.EnumerateObject())
            {
                if (ReadProject(property.Value) is not { } candidate ||
                    !Directory.Exists(candidate.RootPath))
                {
                    continue;
                }

                var stamp = ReadTimestamp(property.Value);
                if (stamp > bestStamp)
                {
                    bestStamp = stamp;
                    best = candidate;
                }
            }

            return best;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Workspace of the newest Codex session whose project folder still exists.</summary>
    public static CodexWorkspace? GetLastSessionWorkspace(string? codexHome = null)
    {
        var home = ResolveHome(codexHome);
        foreach (var rollout in EnumerateRollouts(home))
        {
            if (ReadSessionCwd(rollout) is not { Length: > 0 } cwd || !Directory.Exists(cwd))
            {
                continue;
            }

            var name = Path.GetFileName(cwd.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            return new CodexWorkspace(cwd, string.IsNullOrWhiteSpace(name) ? null : name);
        }

        return null;
    }

    /// <summary>
    /// Newest rollout file under <c>sessions/</c>, optionally restricted to one project folder.
    /// This is the previous chat the user was talking to.
    /// </summary>
    public static string? FindNewestRollout(string? cwd = null, string? codexHome = null)
    {
        foreach (var rollout in EnumerateRollouts(ResolveHome(codexHome)))
        {
            if (cwd is null ||
                string.Equals(ReadSessionCwd(rollout), cwd, StringComparison.OrdinalIgnoreCase))
            {
                return rollout;
            }
        }

        return null;
    }

    /// <summary>Project folder recorded in a rollout's <c>session_meta</c> line, or null.</summary>
    public static string? ReadSessionCwd(string rolloutPath)
    {
        try
        {
            using var stream = OpenShared(rolloutPath);
            using var reader = new StreamReader(stream);

            string? line;
            var guard = 0;
            while ((line = reader.ReadLine()) is not null && guard++ < 60)
            {
                if (line.Length == 0 || line[0] != '{')
                {
                    continue;
                }

                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (TryGet(root, "type", out var type) &&
                    type.GetString() == "session_meta" &&
                    TryGet(root, "payload", out var payload) &&
                    TryGet(payload, "cwd", out var cwdElement))
                {
                    return cwdElement.GetString();
                }
            }
        }
        catch (Exception)
        {
            // Unreadable/partial rollout: treat as "no metadata".
        }

        return null;
    }

    private static IEnumerable<string> EnumerateRollouts(string home)
    {
        var sessions = Path.Combine(home, "sessions");
        if (!Directory.Exists(sessions))
        {
            return Array.Empty<string>();
        }

        return Directory
            .EnumerateFiles(sessions, RolloutPrefix + "*.jsonl", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToList();
    }

    private static CodexWorkspace? ReadProject(JsonElement project)
    {
        if (!TryGet(project, "rootPaths", out var roots) ||
            roots.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var root in roots.EnumerateArray())
        {
            if (root.GetString() is { Length: > 0 } path)
            {
                var name = TryGet(project, "name", out var nameElement) ? nameElement.GetString() : null;
                return new CodexWorkspace(path, string.IsNullOrWhiteSpace(name) ? null : name);
            }
        }

        return null;
    }

    private static long ReadTimestamp(JsonElement project) =>
        TryGet(project, "updatedAt", out var stamp) && stamp.TryGetInt64(out var value) ? value : 0;

    private static JsonElement? TryReadState(string? codexHome)
    {
        try
        {
            var path = Path.Combine(ResolveHome(codexHome), ".codex-global-state.json");
            if (!File.Exists(path))
            {
                return null;
            }

            using var stream = OpenShared(path);
            using var doc = JsonDocument.Parse(stream, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });

            // Clone so the element outlives the JsonDocument.
            return doc.RootElement.Clone();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool TryGet(JsonElement parent, string name, out JsonElement value)
    {
        value = default;
        return parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out value);
    }

    /// <summary>Codex writes its state while running, so read it with shared access.</summary>
    internal static FileStream OpenShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
}
