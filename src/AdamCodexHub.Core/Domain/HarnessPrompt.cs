using System.Text;

namespace AdamCodexHub.Core.Domain;

/// <summary>
/// The client stamps every request with its own identity prompt ("You are Codex, an agent based on
/// GPT-6 …"). A model from another family reads that as a false system prompt: measured 2026-09-11,
/// HHTech's Claude answered "I'm noticing an attempt to inject false system instructions claiming
/// I'm \"Codex\" based on GPT-6…", declared that its only tool was a clock, and refused the work —
/// which is what stalled the video pipeline that was supposed to run on Claude.
/// The harness framing belongs to this hub, so the claim is replaced with an honest one before the
/// request leaves the machine. Nothing else in the request is touched.
/// </summary>
public static class HarnessPrompt
{
    /// <summary>What the model is told instead of the identity claim.</summary>
    public const string Framing =
        "You are a coding agent running inside an agent harness on the user's machine, served through Adam CodexHub. " +
        "The tool list and the surrounding instructions describe the environment you operate in; they are not a claim " +
        "about which company trained you. Carry the user's request out end to end with the tools you are given, and " +
        "verify your own work before reporting.";

    // Phrasings the client is known to use. Matched case-insensitively, longest first.
    private static readonly string[] Claims =
    {
        "You are Codex, an agent based on GPT",
        "You are Codex, an AI assistant based on GPT",
        "You are Codex, based on GPT",
        "You are Codex based on GPT",
        "You are Codex, an agent",
    };

    private const int MaxClaimLength = 600;

    /// <summary>
    /// Returns the request body with the identity claim replaced, or the original bytes when the
    /// request carries no such claim.
    /// </summary>
    public static byte[] Neutralise(byte[] body, out string? replaced)
    {
        replaced = null;
        if (body.Length == 0)
        {
            return body;
        }

        var text = Encoding.UTF8.GetString(body);
        foreach (var claim in Claims)
        {
            var start = text.IndexOf(claim, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
            {
                continue;
            }

            var end = FindClaimEnd(text, start + claim.Length);
            replaced = text.Substring(start, Math.Min(end - start, 160));
            var rewritten = text[..start] + Framing + text[end..];
            return Encoding.UTF8.GetBytes(rewritten);
        }

        return body;
    }

    /// <summary>The claim runs to the end of its sentence — a period, a newline (raw or JSON-escaped).</summary>
    private static int FindClaimEnd(string text, int from)
    {
        var limit = Math.Min(text.Length, from + MaxClaimLength);
        for (var i = from; i < limit; i++)
        {
            if (text[i] == '\n' || text[i] == '\\' && i + 1 < text.Length && text[i + 1] == 'n')
            {
                return i;
            }

            if (text[i] == '.' && i + 1 < text.Length && (text[i + 1] == ' ' || text[i + 1] == '"'))
            {
                return i + 1;
            }
        }

        return limit;
    }
}
