using System.ComponentModel;
using System.Globalization;
using System.Resources;

namespace ClaudePortable.App.Localization;

/// <summary>
/// Runtime-switchable string localization backed by Strings.resx (English,
/// neutral) and Strings.hu.resx (Hungarian). XAML binds against the indexer
/// via <see cref="LocExtension"/>; code calls <see cref="T"/> / <see cref="F"/>.
/// Switching the language raises PropertyChanged for the indexer so every
/// bound control re-reads its text without an application restart.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public const string English = "en";
    public const string Hungarian = "hu";

    public static Loc Instance { get; } = new();

    /// <summary>Exposed for resource-parity tests.</summary>
    public static ResourceManager Resources { get; } = new(
        "ClaudePortable.App.Localization.Strings",
        typeof(Loc).Assembly);

    private CultureInfo _culture = CultureInfo.CurrentUICulture;

    private Loc()
    {
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised after the language changed; UI chrome that is not
    /// data-bound (tray menu, cached rows) re-reads its strings here.</summary>
    public static event EventHandler? LanguageChanged;

    public string this[string key] => T(key);

    public static string LanguageCode
        => Instance._culture.TwoLetterISOLanguageName.Equals(Hungarian, StringComparison.OrdinalIgnoreCase)
            ? Hungarian
            : English;

    public static string T(string key)
        => Resources.GetString(key, Instance._culture) ?? key;

    public static string F(string key, params object?[] args)
        => string.Format(CultureInfo.CurrentCulture, T(key), args);

    /// <summary>
    /// Normalize a requested language code: "en"/"hu" pass through, anything
    /// else (null, "auto", unknown) falls back to the Windows display language.
    /// </summary>
    public static string Normalize(string? code)
    {
        var normalized = code?.Trim().ToLowerInvariant();
        if (normalized is English or Hungarian)
        {
            return normalized;
        }
        return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName
            .Equals(Hungarian, StringComparison.OrdinalIgnoreCase)
            ? Hungarian
            : English;
    }

    public static void SetLanguage(string? code)
    {
        var normalized = Normalize(code);
        var culture = normalized == Hungarian
            ? CultureInfo.GetCultureInfo("hu-HU")
            : CultureInfo.GetCultureInfo("en-US");
        Instance._culture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;
        Instance.PropertyChanged?.Invoke(Instance, new PropertyChangedEventArgs("Item[]"));
        LanguageChanged?.Invoke(null, EventArgs.Empty);
    }
}
