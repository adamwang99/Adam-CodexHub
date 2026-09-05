using System.IO;
using System.Text.Json;

namespace AdamCodexHub.App.Services;

/// <summary>
/// Tiny JSON persistence for UI preferences (currently just the chosen UI language).
/// File lives next to the app data: %LOCALAPPDATA%/AdamCodexHub/data/ui-settings.json.
/// Written without dependencies (System.Text.Json only), same pattern as the
/// Infrastructure AppSettingsService.
/// </summary>
public static class UiSettingsStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static string SettingsPath(string appDataRoot) =>
        Path.Combine(appDataRoot, "data", "ui-settings.json");

    /// <summary>Reads the persisted language ("en" or "vi"); defaults to English.</summary>
    public static string LoadLanguage(string appDataRoot)
    {
        try
        {
            var path = SettingsPath(appDataRoot);
            if (!File.Exists(path))
            {
                return L10n.English;
            }

            var json = File.ReadAllText(path);
            var doc = JsonSerializer.Deserialize<UiSettingsDocument>(json, Json);
            return string.Equals(doc?.Language, L10n.Vietnamese, StringComparison.OrdinalIgnoreCase)
                ? L10n.Vietnamese
                : L10n.English;
        }
        catch
        {
            return L10n.English;
        }
    }

    public static void SaveLanguage(string appDataRoot, string language)
    {
        try
        {
            var path = SettingsPath(appDataRoot);
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var doc = new UiSettingsDocument { Language = language };
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(doc, Json));
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            // Persisting the preference is best-effort; never crash the toggle on IO errors.
        }
    }

    private sealed class UiSettingsDocument
    {
        public string Language { get; set; } = L10n.English;
    }
}
