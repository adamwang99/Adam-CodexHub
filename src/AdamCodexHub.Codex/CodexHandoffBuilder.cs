using System.Text;
using System.Text.Json;

namespace AdamCodexHub.Codex;

/// <summary>One chat message pulled out of a Codex rollout file.</summary>
public sealed record CodexSessionTurn(string Role, string Text);

/// <summary>
/// Builds the "continue where we left off" prompt that the hub types into the fresh Codex Desktop
/// chat after a provider switch.
///
/// Background: a Codex thread keeps the provider it was created with, so an old thread keeps
/// billing the ChatGPT account (usage limit) even when the hub has just pointed Codex at another
/// provider. A brand new chat picks up the hub's overlay — but it starts empty, so the hub hands it
/// a short recap of the previous conversation and the project folder to keep the work going.
/// </summary>
public static class CodexHandoffBuilder
{
    /// <summary>Cap for the whole prompt so the composer stays manageable.</summary>
    private const int MaxPromptChars = 4000;

    /// <summary>Last user/assistant messages of a rollout, oldest first.</summary>
    public static IReadOnlyList<CodexSessionTurn> ReadLastTurns(
        string rolloutPath,
        int maxTurns = 6,
        int maxCharsPerTurn = 400)
    {
        var turns = new List<CodexSessionTurn>();

        try
        {
            using var stream = CodexDesktopState.OpenShared(rolloutPath);
            using var reader = new StreamReader(stream);

            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (line.Length == 0 || line[0] != '{' || !line.Contains("\"message\"", StringComparison.Ordinal))
                {
                    continue;
                }

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (!IsMessageItem(root, out var role, out var text))
                    {
                        continue;
                    }

                    var cleaned = Clean(text, maxCharsPerTurn);
                    if (cleaned.Length == 0)
                    {
                        continue;
                    }

                    turns.Add(new CodexSessionTurn(role, cleaned));
                }
                catch (JsonException)
                {
                    // Half-written line while Codex appends: ignore it.
                }
            }
        }
        catch (Exception)
        {
            return Array.Empty<CodexSessionTurn>();
        }

        return turns.Count <= maxTurns ? turns : turns.Skip(turns.Count - maxTurns).ToList();
    }

    /// <summary>The prompt handed to the new chat.</summary>
    public static string Build(
        CodexWorkspace workspace,
        IReadOnlyList<CodexSessionTurn> turns,
        string? providerName,
        bool vietnamese)
    {
        var builder = new StringBuilder();
        var provider = string.IsNullOrWhiteSpace(providerName) ? null : providerName!.Trim();
        var projectName = string.IsNullOrWhiteSpace(workspace.DisplayName)
            ? Path.GetFileName(workspace.RootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            : workspace.DisplayName!;

        if (vietnamese)
        {
            builder.AppendLine(provider is null
                ? "[Adam CodexHub] Đây là chat MỚI cho dự án bên dưới, tiếp nối phiên làm việc trước."
                : $"[Adam CodexHub] Provider đã được đổi sang \"{provider}\". Đây là chat MỚI tiếp nối phiên làm việc trước (chat cũ ghim provider cũ nên không dùng lại được).");
            builder.AppendLine();
            builder.AppendLine($"Dự án: {projectName}");
            builder.AppendLine($"Đường dẫn: {workspace.RootPath}");

            if (turns.Count > 0)
            {
                builder.AppendLine();
                builder.AppendLine($"Tóm tắt {turns.Count} lượt cuối của chat trước:");
                foreach (var turn in turns)
                {
                    builder.AppendLine($"- {RoleLabel(turn.Role, vietnamese)}: {turn.Text}");
                }
            }

            builder.AppendLine();
            builder.AppendLine("Trước khi làm tiếp: đọc .adam-codexhub/CURRENT_STATE.md nếu có và kiểm tra git status — trạng thái filesystem hiện tại là nguồn sự thật, đừng hoàn tác việc đã làm chỉ vì lịch sử chat cũ khác đi.");
            builder.AppendLine("Hãy xác nhận ngắn gọn là bạn đã nắm bối cảnh rồi chờ chỉ dẫn tiếp theo.");
        }
        else
        {
            builder.AppendLine(provider is null
                ? "[Adam CodexHub] This is a NEW chat for the project below, continuing the previous working session."
                : $"[Adam CodexHub] Provider switched to \"{provider}\". This is a NEW chat continuing the previous session (the old thread is pinned to the previous provider and can no longer be used).");
            builder.AppendLine();
            builder.AppendLine($"Project: {projectName}");
            builder.AppendLine($"Path: {workspace.RootPath}");

            if (turns.Count > 0)
            {
                builder.AppendLine();
                builder.AppendLine($"Recap of the last {turns.Count} messages:");
                foreach (var turn in turns)
                {
                    builder.AppendLine($"- {RoleLabel(turn.Role, vietnamese)}: {turn.Text}");
                }
            }

            builder.AppendLine();
            builder.AppendLine("Before continuing: read .adam-codexhub/CURRENT_STATE.md if present and check git status — the current filesystem state is the source of truth, do not undo completed work just because the old chat history differs.");
            builder.AppendLine("Reply briefly confirming you have the context, then wait for the next instruction.");
        }

        var prompt = builder.ToString().TrimEnd();
        return prompt.Length <= MaxPromptChars ? prompt : prompt[..MaxPromptChars];
    }

    private static string RoleLabel(string role, bool vietnamese) =>
        role == "assistant" ? "Codex" : vietnamese ? "Anh" : "You";

    private static bool IsMessageItem(JsonElement root, out string role, out string text)
    {
        role = string.Empty;
        text = string.Empty;

        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("type", out var type) ||
            type.GetString() != "response_item" ||
            !root.TryGetProperty("payload", out var payload) ||
            payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty("type", out var payloadType) ||
            payloadType.GetString() != "message" ||
            !payload.TryGetProperty("role", out var roleElement))
        {
            return false;
        }

        var value = roleElement.GetString();
        if (value is not ("user" or "assistant"))
        {
            return false;
        }

        role = value;
        text = ExtractText(payload);
        return true;
    }

    private static string ExtractText(JsonElement payload)
    {
        if (!payload.TryGetProperty("content", out var content))
        {
            return string.Empty;
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString() ?? string.Empty;
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        foreach (var part in content.EnumerateArray())
        {
            if (part.ValueKind == JsonValueKind.Object &&
                part.TryGetProperty("text", out var textElement) &&
                textElement.GetString() is { Length: > 0 } value)
            {
                parts.Add(value);
            }
            else if (part.ValueKind == JsonValueKind.String &&
                     part.GetString() is { Length: > 0 } raw)
            {
                parts.Add(raw);
            }
        }

        return string.Join(" ", parts);
    }

    /// <summary>Collapse whitespace and drop injected context blocks the model would not need.</summary>
    private static string Clean(string text, int maxChars)
    {
        var trimmed = text.Trim();
        if (trimmed.StartsWith('<') || trimmed.Contains("<environment_context>", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(trimmed.Length);
        var lastWasSpace = false;
        foreach (var ch in trimmed)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!lastWasSpace)
                {
                    builder.Append(' ');
                    lastWasSpace = true;
                }

                continue;
            }

            builder.Append(ch);
            lastWasSpace = false;
        }

        var collapsed = builder.ToString().Trim();
        return collapsed.Length <= maxChars ? collapsed : collapsed[..maxChars] + "…";
    }
}
