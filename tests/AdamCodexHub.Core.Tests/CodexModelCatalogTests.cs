using System.Linq;
using System.Text.Json;
using AdamCodexHub.Gateway;
using Xunit;

namespace AdamCodexHub.Core.Tests;

/// <summary>
/// Codex (app-server) gọi GET /v1/models?client_version=… và BẮT BUỘC thấy field "models";
/// thiếu field này là nó bỏ luôn danh sách model của hub (lỗi thật gặp ở Codex 0.153.4).
/// </summary>
public sealed class CodexModelCatalogTests
{
    [Fact]
    public void Build_ReturnsModelsShapeCodexExpects()
    {
        var input = new (string Id, string Name, int? ContextWindow)[] { ("claude-opus-4-7[1M]", "Claude Opus 4.7 [1M]", 200000) };

        var json = CodexModelCatalog.Build(input);

        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("models", out var models));
        var first = models.EnumerateArray().Single();
        Assert.Equal("claude-opus-4-7[1M]", first.GetProperty("slug").GetString());
        Assert.Equal("Claude Opus 4.7 [1M]", first.GetProperty("display_name").GetString());
        Assert.Equal("list", first.GetProperty("visibility").GetString());
        Assert.True(first.GetProperty("supported_in_api").GetBoolean());
        Assert.Equal(200000, first.GetProperty("context_window").GetInt32());
        Assert.Equal(200000, first.GetProperty("max_context_window").GetInt32());
    }

    [Fact]
    public void Build_FallsBackToId_AndOmitsInvalidContextWindow()
    {
        var input = new (string Id, string Name, int? ContextWindow)[]
        {
            ("dsv4", "", null),
            ("claude-sonnet-5", "Claude Sonnet 5", 0)
        };

        var json = CodexModelCatalog.Build(input);

        using var doc = JsonDocument.Parse(json);
        var list = doc.RootElement.GetProperty("models").EnumerateArray().ToArray();
        Assert.Equal(2, list.Length);
        Assert.Equal("dsv4", list[0].GetProperty("display_name").GetString());
        // không truyền context window thì giữ giá trị mặc định của template, không ghi 0/null
        Assert.NotEqual(JsonValueKind.Null, list[0].GetProperty("context_window").ValueKind);
        Assert.Equal(1, list[0].GetProperty("priority").GetInt32());
        Assert.Equal(2, list[1].GetProperty("priority").GetInt32());
        // mọi field Codex cần đều có mặt trên từng entry
        Assert.Equal(38, list[0].EnumerateObject().Count());
    }
}
