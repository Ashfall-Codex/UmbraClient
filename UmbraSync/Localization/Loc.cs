using System.Collections.ObjectModel;
using System.Globalization;
using System.Resources;

namespace UmbraSync.Localization;

public static class Loc
{
    private static readonly string PreferredLanguage = "fr";
    private static readonly string FallbackLanguage = "en";
    private static readonly string[] LanguageOrder = ["fr", "en"];

    private static readonly IReadOnlyDictionary<string, string> LanguageDisplayNames =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["fr"] = "Français",
            ["en"] = "English",
        });

    private static readonly IReadOnlyDictionary<string, CultureInfo> LanguageCultures =
        new ReadOnlyDictionary<string, CultureInfo>(LanguageOrder.ToDictionary(
            lang => lang,
            lang => CultureInfo.GetCultureInfo(lang),
            StringComparer.OrdinalIgnoreCase));

    private static readonly IReadOnlyList<KeyValuePair<string, string>> LanguageOptions =
        new ReadOnlyCollection<KeyValuePair<string, string>>(LanguageOrder
            .Select(code => new KeyValuePair<string, string>(code, LanguageDisplayNames.TryGetValue(code, out var name) ? name : code))
            .ToList());

    private static readonly ResourceManager ResourceManager = new("UmbraSync.Localization.Strings", typeof(Loc).Assembly);

    private static CultureInfo _currentCulture = LanguageCultures[PreferredLanguage];

    public static IReadOnlyList<KeyValuePair<string, string>> AvailableLanguages => LanguageOptions;
    public static string CurrentLanguage => _currentCulture.Name;

    public static void Initialize(string? preferredLanguage)
    {
        SetLanguage(preferredLanguage);
    }

    public static void SetLanguage(string? languageCode)
    {
        var normalized = NormalizeLanguage(languageCode);
        _currentCulture = normalized;
        CultureInfo.CurrentUICulture = normalized;
    }

    public static bool IsLanguageAvailable(string? languageCode)
    {
        return TryResolveCulture(languageCode, out _);
    }

    public static string GetLanguageDisplayName(string? languageCode)
    {
        if (string.IsNullOrWhiteSpace(languageCode)) return string.Empty;
        if (LanguageDisplayNames.TryGetValue(languageCode, out var exact))
        {
            return exact;
        }

        if (TryResolveCulture(languageCode, out var culture) && LanguageDisplayNames.TryGetValue(culture.TwoLetterISOLanguageName, out var name))
        {
            return name;
        }

        return languageCode;
    }

    public static string Get(string key)
    {
        var value = ResourceManager.GetString(key, _currentCulture);
        if (!string.IsNullOrEmpty(value))
        {
            return value.ReplacingLineBreaks();
        }

        var fallbackCulture = LanguageCultures[FallbackLanguage];
        value = ResourceManager.GetString(key, fallbackCulture);
        return string.IsNullOrEmpty(value) ? key : value.ReplacingLineBreaks();
    }

    /// <summary>
    /// Variante paramétrée : évite d'écrire un <c>string.Format</c> autour de chaque libellé
    /// à trou. La culture courante est utilisée pour que les nombres suivent la langue affichée.
    /// </summary>
    public static string Get(string key, params object?[] args)
    {
        string format = Get(key);
        if (args == null || args.Length == 0) return format;

        try
        {
            return string.Format(CultureInfo.CurrentCulture, format, args);
        }
        catch (FormatException)
        {
            return format;
        }
    }

    private static CultureInfo NormalizeLanguage(string? languageCode)
    {
        if (TryResolveCulture(languageCode, out var culture))
        {
            return culture;
        }

        if (LanguageCultures.TryGetValue(PreferredLanguage, out var preferred))
        {
            return preferred;
        }

        return LanguageCultures.TryGetValue(FallbackLanguage, out var fallback)
            ? fallback
            : CultureInfo.InvariantCulture;
    }

    private static bool TryResolveCulture(string? languageCode, out CultureInfo culture)
    {
        culture = null!;
        if (string.IsNullOrWhiteSpace(languageCode))
        {
            return false;
        }

        var lookupCode = languageCode!;
        if (LanguageCultures.TryGetValue(lookupCode, out var direct))
        {
            culture = direct;
            return true;
        }

        try
        {
            var requested = CultureInfo.GetCultureInfo(lookupCode);
            var match = LanguageCultures.Values.FirstOrDefault(c =>
                string.Equals(c.Name, requested.Name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(c.TwoLetterISOLanguageName, requested.TwoLetterISOLanguageName, StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                culture = match;
                return true;
            }
        }
        catch (CultureNotFoundException)
        {
            // ignore
        }

        return false;
    }

    private static string ReplacingLineBreaks(this string value)
    {
        return value.Replace("&#x0A;", "\n", StringComparison.Ordinal);
    }
}