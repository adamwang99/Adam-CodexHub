using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AdamCodexHub.Core.Domain;
using AdamCodexHub.Core.Interfaces;

namespace AdamCodexHub.Codex;

public sealed class FileProjectStateService : IProjectStateService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public async Task<ProjectState> RefreshAsync(
        string projectPath,
        SyncLevel level,
        string? providerId = null,
        string? modelId = null,
        CancellationToken cancellationToken = default)
    {
        projectPath = Path.GetFullPath(projectPath);
        var stateDirectory = Path.Combine(projectPath, ".adam-codexhub");
        Directory.CreateDirectory(stateDirectory);

        var previous = await ReadAsync(projectPath, cancellationToken);
        var changedFiles = await GetChangedFilesAsync(projectPath, cancellationToken);
        var head = await GitAsync(projectPath, "rev-parse HEAD", cancellationToken);

        var state = new ProjectState
        {
            ProjectPath = projectPath,
            Revision = (previous?.Revision ?? 0) + 1,
            UpdatedAt = DateTimeOffset.UtcNow,
            GitHead = string.IsNullOrWhiteSpace(head) ? previous?.GitHead : head.Trim(),
            ChangedFiles = changedFiles,
            CurrentObjective = previous?.CurrentObjective ?? string.Empty,
            CompletedWork = previous?.CompletedWork ?? Array.Empty<string>(),
            PendingTasks = previous?.PendingTasks ?? Array.Empty<string>(),
            ImportantDecisions = previous?.ImportantDecisions ?? Array.Empty<string>(),
            KnownIssues = previous?.KnownIssues ?? Array.Empty<string>(),
            LastProviderId = providerId ?? previous?.LastProviderId,
            LastModelId = modelId ?? previous?.LastModelId
        };

        var jsonPath = Path.Combine(stateDirectory, "project-state.json");
        await File.WriteAllTextAsync(
            jsonPath,
            JsonSerializer.Serialize(state, Json),
            cancellationToken);

        await File.WriteAllTextAsync(
            Path.Combine(stateDirectory, "CURRENT_STATE.md"),
            BuildMarkdown(state, level),
            cancellationToken);

        return state;
    }

    public async Task<ProjectState?> ReadAsync(
        string projectPath,
        CancellationToken cancellationToken = default)
    {
        var jsonPath = Path.Combine(
            Path.GetFullPath(projectPath),
            ".adam-codexhub",
            "project-state.json");

        if (!File.Exists(jsonPath))
        {
            return null;
        }

        await using var stream = File.OpenRead(jsonPath);
        return await JsonSerializer.DeserializeAsync<ProjectState>(
            stream,
            Json,
            cancellationToken);
    }

    private static async Task<IReadOnlyList<string>> GetChangedFilesAsync(
        string projectPath,
        CancellationToken cancellationToken)
    {
        var status = await GitAsync(projectPath, "status --porcelain=v1", cancellationToken);
        if (string.IsNullOrWhiteSpace(status))
        {
            return Array.Empty<string>();
        }

        return status.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Length > 3 ? line[3..].Trim() : line.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static async Task<string> GitAsync(
        string projectPath,
        string arguments,
        CancellationToken cancellationToken)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = arguments,
                    WorkingDirectory = projectPath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
            var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode == 0 ? stdout : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string BuildMarkdown(ProjectState state, SyncLevel level)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Adam CodexHub — Current Project State");
        sb.AppendLine();
        sb.AppendLine($"> Revision: {state.Revision}");
        sb.AppendLine($"> Updated: {state.UpdatedAt:O}");
        sb.AppendLine($"> Sync level: {level}");
        sb.AppendLine();
        sb.AppendLine("## Source of truth");
        sb.AppendLine();
        sb.AppendLine("Current filesystem and Git state take priority over stale chat assumptions.");
        sb.AppendLine();
        sb.AppendLine("## Current objective");
        sb.AppendLine();
        sb.AppendLine(string.IsNullOrWhiteSpace(state.CurrentObjective)
            ? "_Not recorded yet._"
            : state.CurrentObjective);
        sb.AppendLine();
        sb.AppendLine("## Changed files");
        sb.AppendLine();

        if (state.ChangedFiles.Count == 0)
        {
            sb.AppendLine("- No Git working-tree changes detected.");
        }
        else
        {
            foreach (var file in state.ChangedFiles)
            {
                sb.AppendLine($"- `{file}`");
            }
        }

        sb.AppendLine();
        sb.AppendLine("## Pending tasks");
        sb.AppendLine();

        if (state.PendingTasks.Count == 0)
        {
            sb.AppendLine("- _Not recorded yet._");
        }
        else
        {
            foreach (var task in state.PendingTasks)
            {
                sb.AppendLine($"- {task}");
            }
        }

        return sb.ToString();
    }
}
