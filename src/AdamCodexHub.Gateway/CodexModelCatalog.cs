// Sinh tự động: catalog model dạng Codex app-server mong đợi ({ "models": [...] }).
// Nguồn field: catalog thật của Codex 0.153.4. Sửa tay file này khi Codex đổi schema.
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AdamCodexHub.Gateway;

public static class CodexModelCatalog
{
    private const string TemplateJson = """
{
  "slug": "__SLUG__",
  "display_name": "__NAME__",
  "description": "__DESC__",
  "default_reasoning_level": "low",
  "supported_reasoning_levels": [
    {
      "effort": "low",
      "description": "Fast responses with lighter reasoning"
    },
    {
      "effort": "medium",
      "description": "Balances speed and reasoning depth for everyday tasks"
    },
    {
      "effort": "high",
      "description": "Greater reasoning depth for complex problems"
    },
    {
      "effort": "xhigh",
      "description": "Extra high reasoning depth for complex problems"
    },
    {
      "effort": "max",
      "description": "Maximum reasoning depth for the hardest problems"
    },
    {
      "effort": "ultra",
      "description": "Maximum reasoning with automatic task delegation"
    }
  ],
  "shell_type": "unified_exec",
  "visibility": "list",
  "supported_in_api": true,
  "priority": 0,
  "additional_speed_tiers": [
    "fast"
  ],
  "service_tiers": [
    {
      "id": "priority",
      "name": "Fast",
      "description": "2x speed, increased usage"
    }
  ],
  "availability_nux": null,
  "upgrade": null,
  "model_messages": {
    "persistent_instructions": "Served through Adam CodexHub.",
    "instructions_template": "Served through Adam CodexHub.",
    "instructions_variables": null,
    "approvals": {
      "on_request": null,
      "on_request_auto_review": "Served through Adam CodexHub.",
      "never": null,
      "unless_trusted": null
    },
    "collaboration_modes": {
      "default": "Served through Adam CodexHub.",
      "plan": null
    },
    "auto_review": {
      "policy": null,
      "policy_template": null,
      "rejection_instructions": "Do not bypass this rejection through a workaround or indirect execution. Continue with a safer alternative, or carry out checks to prove that the action is authorized or low risk before trying again. Complete unaffected work without asking for confirmation. Report anything that remains blocked, clarify why it was blocked by auto-review, inform the user of the risk and ask for approval.",
      "timeout_instructions": null
    },
    "permissions": null,
    "multi_agent": {
      "role": {
        "root": "Served through Adam CodexHub.",
        "subagent": "Served through Adam CodexHub."
      },
      "mode": null
    },
    "token_budget": {
      "enabled": false,
      "use_history_notes_extension": false,
      "reminder_threshold_tokens": 6144,
      "reminder_message_template": "Served through Adam CodexHub.",
      "guidance_message": "Served through Adam CodexHub.",
      "auto_compact_fallback_prompt": "Served through Adam CodexHub.",
      "auto_compact_fallback_buffer_tokens": 16384
    },
    "guardian_v2": {
      "classifier_instructions": "Served through Adam CodexHub."
    },
    "confirmation_policies": {
      "browser_use": "Served through Adam CodexHub.",
      "computer_use": "Served through Adam CodexHub."
    }
  },
  "include_skills_usage_instructions": false,
  "include_plugin_usage_instructions": false,
  "include_apps_usage_instructions": false,
  "default_reasoning_summary": "none",
  "support_verbosity": true,
  "default_verbosity": "low",
  "apply_patch_tool_type": "freeform",
  "web_search_tool_type": "text_and_image",
  "truncation_policy": {
    "mode": "tokens",
    "limit": 10000
  },
  "supports_image_detail_original": true,
  "context_window": 272000,
  "max_context_window": 872000,
  "comp_hash": "3000",
  "effective_context_window_percent": 95,
  "experimental_supported_tools": [
    "send_user_message_async",
    "clock"
  ],
  "input_modalities": [
    "text",
    "image"
  ],
  "supports_search_tool": true,
  "use_responses_lite": true,
  "node_repl_auto_review_required": true,
  "node_repl_disabled": false,
  "tool_mode": "code_mode_only",
  "multi_agent_version": "v2",
  "multi_agent_reasoning_effort": "xhigh",
  "base_instructions": "You are a coding agent served through Adam CodexHub."
}
""";

    /// <summary>
    /// Danh sách model của provider đang bật, ở đúng shape Codex đọc được. Codex không tô màu
    /// được tên model, nên <paramref name="tag"/> (F/N/S/U) được ghép vào display_name để người
    /// dùng vẫn phân biệt được model nhanh / chậm; thứ tự truyền vào là thứ tự Codex hiển thị.
    /// </summary>
    public static string Build(IEnumerable<(string Id, string Name, int? ContextWindow, string Tag)> models)
    {
        var template = JsonNode.Parse(TemplateJson)!;
        var list = new JsonArray();
        var priority = 1;
        foreach (var (id, name, contextWindow, tag) in models)
        {
            var entry = JsonNode.Parse(template.ToJsonString())!;
            entry["slug"] = id;
            entry["display_name"] = string.IsNullOrWhiteSpace(name) ? $"{id} ({tag})" : $"{name} ({tag})";
            entry["description"] = Description(tag);
            entry["priority"] = priority++;
            entry["visibility"] = "list";
            entry["supported_in_api"] = true;
            if (contextWindow is > 0)
            {
                entry["context_window"] = contextWindow.Value;
                entry["max_context_window"] = contextWindow.Value;
            }

            list.Add(entry);
        }

        var root = new JsonObject { ["models"] = list };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }

    /// <summary>Dòng mô tả của từng entry: nguồn model + nghĩa nhãn tốc độ ghép sau tên.</summary>
    private static string Description(string tag) =>
        "Model của Adam CodexHub (provider đang bật). "
        + $"Nhãn ({tag}): F = nhanh, N = thường, S = chậm, U = chưa kiểm tra.";
}
