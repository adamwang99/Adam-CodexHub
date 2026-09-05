using System.Windows;

namespace AdamCodexHub.App.Services;

/// <summary>
/// Lightweight localization service for C# strings (ViewModels, dialogs, code-behind).
/// XAML text goes through {DynamicResource L10n_...} keys stored in the merged
/// Locale.EN.xaml / Locale.VI.xaml dictionaries; this service reads the SAME app-level
/// dictionary so both worlds always agree. Raise <see cref="LanguageChanged"/> after the
/// active merged dictionary has been swapped so ViewModels can re-notify.
/// </summary>
public static class L10n
{
    public const string English = "en";
    public const string Vietnamese = "vi";

    private static string _current = English;

    /// <summary>Current language code: "en" or "vi".</summary>
    public static string CurrentLanguage
    {
        get => _current;
        private set => _current = value;
    }

    public static bool IsVietnamese => CurrentLanguage == Vietnamese;

    /// <summary>Raised after the app-level merged locale dictionary has been swapped.</summary>
    public static event Action? LanguageChanged;

    public static void SetLanguage(string language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            language = English;
        }

        if (string.Equals(language, _current, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _current = language == Vietnamese ? Vietnamese : English;
        LanguageChanged?.Invoke();
    }

    /// <summary>Returns the localized string for a full resource key (e.g. "L10n_Home_Title"),
    /// or the key itself when the resource is missing (defensive fallback).</summary>
    public static string T(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return key;
        }

        var found = Application.Current?.TryFindResource(key);
        return found as string ?? key;
    }

    /// <summary>Localized format: L10n.F("L10n_Card_ModelCount", count).</summary>
    public static string F(string key, params object?[] args)
    {
        try
        {
            return string.Format(T(key), args);
        }
        catch (FormatException)
        {
            return T(key);
        }
    }
}
