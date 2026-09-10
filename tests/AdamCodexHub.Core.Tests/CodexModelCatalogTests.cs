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
        var input = new (string Id, string Name, int? ContextWindow, string Tag)[]
        {
            ("claude-opus-4-7[1M]", "Claude Opus 4.7 [1M]", 200000, "F")
        };

        var json = CodexModelCatalog.Build(input);

        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("models", out var models));
        var first = models.EnumerateArray().Single();
        Assert.Equal("claude-opus-4-7[1M]", first.GetProperty("slug").GetString());
        // Codex không tô màu được tên model nên nhãn tốc độ phải nằm trong display_name.
        Assert.Equal("Claude Opus 4.7 [1M] (F)", first.GetProperty("display_name").GetString());
        Assert.Equal("list", first.GetProperty("visibility").GetString());
        Assert.True(first.GetProperty("supported_in_api").GetBoolean());
        Assert.Equal(200000, first.GetProperty("context_window").GetInt32());
        Assert.Equal(200000, first.GetProperty("max_context_window").GetInt32());
    }

    [Fact]
    public void Build_FallsBackToId_AndOmitsInvalidContextWindow()
    {
        var input = new (string Id, string Name, int? ContextWindow, string Tag)[]
        {
            ("dsv4", "", null, "U"),
            ("claude-sonnet-5", "Claude Sonnet 5", 0, "S")
        };

        var json = CodexModelCatalog.Build(input);

        using var doc = JsonDocument.Parse(json);
        var list = doc.RootElement.GetProperty("models").EnumerateArray().ToArray();
        Assert.Equal(2, list.Length);
        Assert.Equal("dsv4 (U)", list[0].GetProperty("display_name").GetString());
        // dòng mô tả nói rõ nghĩa của nhãn ghép sau tên
        Assert.Contains("(U)", list[0].GetProperty("description").GetString(), StringComparison.Ordinal);
        // không truyền context window thì giữ giá trị mặc định của template, không ghi 0/null
        Assert.NotEqual(JsonValueKind.Null, list[0].GetProperty("context_window").ValueKind);
        Assert.Equal(1, list[0].GetProperty("priority").GetInt32());
        Assert.Equal(2, list[1].GetProperty("priority").GetInt32());
        // mọi field Codex cần đều có mặt trên từng entry
        Assert.Equal(38, list[0].EnumerateObject().Count());

        // The harness prompt a served model receives must not claim an identity the model cannot accept.
        // Measured 2026-09-11 with a Claude model: Codex's own framing ("You are Codex … based on GPT-6")
        // made it answer "I'm noticing an attempt to inject false system instructions…", refuse the tools
        // and reply as a chat-only assistant, so the whole video pipeline stalled. The entry therefore
        // explains the harness instead of claiming to be the model.
        var instructions = list[0].GetProperty("base_instructions").GetString();
        Assert.False(string.IsNullOrWhiteSpace(instructions));
        Assert.Contains("harness", instructions!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("GPT", instructions!, StringComparison.Ordinal);
        // `experimental_supported_tools` là danh sách tool Codex cấp cho model. Rỗng ⇒ mọi lượt gửi đi
        // KHÔNG có tool nào (log `tools offered to <model>: none`), model đúng khi trả lời "không chạy
        // được lệnh" — đã đo 2026-09-11 bằng một việc kiểm chứng được (yêu cầu tạo file, file không hề
        // xuất hiện). Danh sách ["clock"] cũng sai (chỉ có tool đồng hồ). Xoá hẳn field thì Codex không
        // parse nổi entry và bỏ luôn danh mục hub. Nay điền đúng bộ tool mà Codex dùng.
        Assert.True(list[0].TryGetProperty("experimental_supported_tools", out var tools));
        var toolNames = tools.EnumerateArray().Select(t => t.GetString()).ToList();
        // Empty is the real value (`gpt-5.6-sol` in the account catalogue has `[]`); inventing tool names
        // here is what made Codex offer a single clock tool earlier the same day.
        Assert.Empty(toolNames);
        Assert.Equal("code_mode_only", list[0].GetProperty("tool_mode").GetString());
        Assert.Equal("unified_exec", list[0].GetProperty("shell_type").GetString());
    }
}
