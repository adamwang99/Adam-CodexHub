using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace AdamCodexHub.Codex;

/// <summary>
/// Queues a message (with an explicit model) into the newest Codex session via
/// `codex queue --thread <id> --message <text> --model <model>`. Used by the tray
/// item "switch the model of the open session".
/// </summary>
public sealed class CodexSessionQueue
{
    private static readonly Regex RolloutName = new(
        @"^rollout-\d{4}-\d{2}-\d{2}T\d{2}-\d{2}-\d{2}-(?<thread>[0-9a-fA-F-]{36})\.jsonl$",
        RegexOptions.Compiled);

    public string? SessionsRoot { get; init; }
    public string? ExecutablePath { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(60);

    public static string? ParseThreadId(string? rolloutFileName)
    {
        if (string.IsNullOrEmpty(rolloutFileName))
        {
            return null;
        }

        var match = RolloutName.Match(rolloutFileName);
        return match.Success ? match.Groups["thread"].Value : null;
    }

    public static string? ResolveExecutable()
    {
        var binRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenAI", "Codex", "bin");
        if (!Directory.Exists(binRoot))
        {
            return null;
        }

        try
        {
            return Directory.EnumerateDirectories(binRoot)
                .OrderByDescending(Directory.GetLastWriteTimeUtc)
                .Select(directory => Path.Combine(directory, "codex.exe"))
                .FirstOrDefault(File.Exists);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public (string ThreadId, string RolloutPath)? FindNewestSession()
    {
        var root = SessionsRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "sessions");
        if (!Directory.Exists(root))
        {
            return null;
        }

        try
        {
            var newest = Directory.EnumerateFiles(root, "rollout-*.jsonl", SearchOption.AllDirectories)
                .Select(path => new FileInfo(path))
                .OrderByDescending(info => info.LastWriteTimeUtc)
                .FirstOrDefault();
            if (newest is null)
            {
                return null;
            }

            var threadId = ParseThreadId(newest.Name);
            return threadId is null ? null : (threadId, newest.FullName);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public async Task<CodexQueueOutcome> QueueMessageAsync(string model, string message, CancellationToken cancellationToken = default)
    {
        var executable = ExecutablePath ?? ResolveExecutable();
        if (executable is null)
        {
            return new CodexQueueOutcome(false, null, "codex executable not found");
        }

        var session = FindNewestSession();
        if (session is null)
        {
            return new CodexQueueOutcome(false, null, "no Codex session found");
        }

        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(executable) ?? "."
        };
        info.ArgumentList.Add("queue");
        info.ArgumentList.Add("--thread");
        info.ArgumentList.Add(session.Value.ThreadId);
        info.ArgumentList.Add("--message");
        info.ArgumentList.Add(message);
        info.ArgumentList.Add("--model");
        info.ArgumentList.Add(model);

        try
        {
            using var process = Process.Start(info);
            if (process is null)
            {
                return new CodexQueueOutcome(false, session.Value.ThreadId, "codex did not start");
            }

            var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(Timeout);
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(true); } catch (Exception) { }
                return new CodexQueueOutcome(false, session.Value.ThreadId, "timed out");
            }

            var output = ((await stdout) + (await stderr)).Trim();
            return new CodexQueueOutcome(process.ExitCode == 0, session.Value.ThreadId, output);
        }
        catch (Exception ex)
        {
            return new CodexQueueOutcome(false, session.Value.ThreadId, ex.Message);
        }
    }
}

public readonly record struct CodexQueueOutcome(bool Queued, string? ThreadId, string Detail);
