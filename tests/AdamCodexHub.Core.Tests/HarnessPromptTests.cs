using System.Text;
using System.Text.Json;
using AdamCodexHub.Core.Domain;
using Xunit;

namespace AdamCodexHub.Core.Tests;

public sealed class HarnessPromptTests
{
    [Fact]
    public void ReplacesTheClientsIdentityClaim()
    {
        var body = Encoding.UTF8.GetBytes(
            "{\"model\":\"claude-opus-4-7[1M]\",\"instructions\":\"You are Codex, an agent based on GPT-6. " +
            "You and the user share the same workspace.\",\"input\":\"dựng video\"}");

        var result = HarnessPrompt.Neutralise(body, out var replaced);
        var text = Encoding.UTF8.GetString(result);

        Assert.NotNull(replaced);
        Assert.Contains("You are Codex", replaced);
        Assert.DoesNotContain("You are Codex", text);
        Assert.DoesNotContain("GPT-6", text);
        Assert.Contains("harness", text);

        // The request must still be the request: valid JSON, same model, same input.
        using var doc = JsonDocument.Parse(text);
        Assert.Equal("claude-opus-4-7[1M]", doc.RootElement.GetProperty("model").GetString());
        Assert.Equal("dựng video", doc.RootElement.GetProperty("input").GetString());
    }

    [Fact]
    public void KeepsTheToolListIntact()
    {
        var body = Encoding.UTF8.GetBytes(
            "{\"model\":\"m\",\"instructions\":\"You are Codex, an agent based on GPT-5. Be nice.\"," +
            "\"tools\":[{\"type\":\"function\",\"name\":\"shell\"}],\"tool_choice\":\"auto\"}");

        var text = Encoding.UTF8.GetString(HarnessPrompt.Neutralise(body, out _));

        using var doc = JsonDocument.Parse(text);
        var tools = doc.RootElement.GetProperty("tools");
        Assert.Equal(1, tools.GetArrayLength());
        Assert.Equal("shell", tools[0].GetProperty("name").GetString());
        Assert.Equal("auto", doc.RootElement.GetProperty("tool_choice").GetString());
    }

    [Fact]
    public void LeavesARequestWithoutTheClaimUntouched()
    {
        var body = Encoding.UTF8.GetBytes("{\"model\":\"m\",\"instructions\":\"You are a helpful assistant.\"}");

        var result = HarnessPrompt.Neutralise(body, out var replaced);

        Assert.Null(replaced);
        // The bytes come back as they went in, and without a copy: the segment still points at the
        // caller's array, which is what keeps a 64 MB turn from being duplicated for nothing.
        Assert.Same(body, result.Array);
        Assert.Equal(0, result.Offset);
        Assert.Equal(body.Length, result.Count);
    }
}
