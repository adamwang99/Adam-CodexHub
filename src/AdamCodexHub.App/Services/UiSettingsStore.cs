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
        Save(appDataRoot, doc =>
        {
            doc.Language = language;
        });
    }

    /// <summary>Reads the persisted color theme ("dark" or "light"); defaults to dark.</summary>
    public static string LoadTheme(string appDataRoot)
    {
        try
        {
            var path = SettingsPath(appDataRoot);
            if (!File.Exists(path))
            {
                return App.ThemeDark;
            }

            var json = File.ReadAllText(path);
            var doc = JsonSerializer.Deserialize<UiSettingsDocument>(json, Json);
            return string.Equals(doc?.Theme, App.ThemeLight, StringComparison.OrdinalIgnoreCase)
                ? App.ThemeLight
                : App.ThemeDark;
        }
        catch
        {
            return App.ThemeDark;
        }
    }

    public static void SaveTheme(string appDataRoot, string theme)
    {
        Save(appDataRoot, doc =>
        {
            doc.Theme = theme;
        });
    }

    private static void Save(string appDataRoot, Action<UiSettingsDocument> mutate)
    {
        try
        {
            var path = SettingsPath(appDataRoot);
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var doc = new UiSettingsDocument();
            try
            {
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    doc = JsonSerializer.Deserialize<UiSettingsDocument>(json, Json) ?? doc;
                }
            }
            catch
            {
                // Start from defaults if the existing settings file is unreadable.
            }

            mutate(doc);
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
        public string Theme { get; set; } = App.ThemeDark;
    }
}
