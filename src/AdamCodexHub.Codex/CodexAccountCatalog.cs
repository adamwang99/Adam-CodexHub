using System.Text.Json;

namespace AdamCodexHub.Codex;

/// <summary>
/// The models the signed-in ChatGPT account offers, as cached by Codex itself in
/// <c>~/.codex/models_cache.json</c> right after it asks chatgpt.com. The hub cannot query the
/// account (it is not an OpenAI-compatible provider), so this cache is the only catalogue we have.
/// Returns an empty list when the file is missing or unreadable — callers then skip migration.
/// </summary>
public static class CodexAccountCatalog
{
    public static IReadOnlyList<string> Read(string? codexHome)
    {
        var home = string.IsNullOrWhiteSpace(codexHome)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".codex")
            : codexHome;

        try
        {
            var path = Path.Combine(home, "models_cache.json");
            if (!File.Exists(path))
            {
                return Array.Empty<string>();
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("models", out var models) ||
                models.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<string>();
            }

            var slugs = new List<string>();
            foreach (var entry in models.EnumerateArray())
            {
                if (entry.TryGetProperty("slug", out var slug) &&
                    slug.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(slug.GetString()))
                {
                    slugs.Add(slug.GetString()!);
                }
            }

            return slugs;
        }
        catch (Exception exception) when (
            exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }
}
