using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AdamCodexHub.Codex;

/// <summary>
/// User preferences for the session hand-off the hub performs when a provider is activated from
/// Home, persisted as its own document so the Settings page can own it without rewriting the
/// general UI-settings file (a typed reload there would drop unknown keys).
/// </summary>
public sealed record CodexHandoffPreferences
{
    /// <summary>
    /// Fresh chat off, recap on: activating a provider must reach the chat that is already open.
    /// Codex pins a model per thread and the hub re-points those pins at the new provider's model, so
    /// the user carries on where they were (Adam, 2026-09-11: "tiếp tục làm việc trên chat cũ được
    /// ngay không cần tạo chat mới").
    /// </summary>
    public static CodexHandoffPreferences Default { get; } = new();

    /// <summary>Open a brand new Codex chat when a provider card is activated (Ctrl+N). Off by default.</summary>
    public bool OpenFreshChat { get; init; }

    /// <summary>Submit the recap instead of leaving it in the composer for the user to send.</summary>
    public bool AutoSubmit { get; init; } = true;

    /// <summary>
    /// Auto-submit is only meaningful together with a fresh chat: without one the recap would be
    /// typed into whatever chat is on screen. Derived, so it stays out of the settings file.
    /// </summary>
    [JsonIgnore]
    public bool ShouldSubmitRecap => OpenFreshChat && AutoSubmit;

    public static string DefaultRoot =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AdamCodexHub");

    public static string SettingsPath(string? appDataRoot = null) =>
        Path.Combine(appDataRoot ?? DefaultRoot, "data", "codex-handoff.json");

    /// <summary>Loads the switches; a missing or unreadable file means "both on".</summary>
    public static CodexHandoffPreferences Load(string? appDataRoot = null)
    {
        try
        {
            var path = SettingsPath(appDataRoot);
            if (!File.Exists(path))
            {
                return Default;
            }

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<CodexHandoffPreferences>(json, Json) ?? Default;
        }
        catch
        {
            return Default;
        }
    }

    /// <summary>Persists the switches (best-effort: the Settings toggle must never throw).</summary>
    public void Save(string? appDataRoot = null)
    {
        try
        {
            var path = SettingsPath(appDataRoot);
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            // Keep the in-memory switch; the file is a convenience, not a source of truth.
        }
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
}
