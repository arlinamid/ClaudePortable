using System.Collections;
using System.Globalization;
using System.Text.RegularExpressions;
using ClaudePortable.App.Localization;

namespace ClaudePortable.Tests;

public class LocalizationParityTests
{
    private static Dictionary<string, string> ResourceSet(CultureInfo culture)
    {
        // tryParents: false so the Hungarian set contains only its own
        // entries and missing keys are not masked by English fallback.
        var set = Loc.Resources.GetResourceSet(culture, createIfNotExists: true, tryParents: false);
        Assert.NotNull(set);
        return set!.Cast<DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string)e.Value!, StringComparer.Ordinal);
    }

    private static Dictionary<string, string> English => ResourceSet(CultureInfo.InvariantCulture);
    // Neutral "hu": the satellite assembly is built for the neutral culture,
    // and tryParents: false means "hu-HU" would not fall back to it.
    private static Dictionary<string, string> Hungarian => ResourceSet(CultureInfo.GetCultureInfo("hu"));

    [Fact]
    public void Hungarian_covers_every_english_key()
    {
        var missing = English.Keys.Except(Hungarian.Keys).Order().ToList();
        Assert.True(missing.Count == 0, $"Keys missing from Strings.hu.resx: {string.Join(", ", missing)}");
    }

    [Fact]
    public void Hungarian_has_no_orphan_keys()
    {
        var orphans = Hungarian.Keys.Except(English.Keys).Order().ToList();
        Assert.True(orphans.Count == 0, $"Keys only in Strings.hu.resx: {string.Join(", ", orphans)}");
    }

    [Fact]
    public void Format_placeholders_match_between_languages()
    {
        var en = English;
        var hu = Hungarian;
        var mismatches = new List<string>();
        foreach (var (key, enValue) in en)
        {
            if (!hu.TryGetValue(key, out var huValue))
            {
                continue; // covered by the coverage test
            }
            var enSlots = Placeholders(enValue);
            var huSlots = Placeholders(huValue);
            if (!enSlots.SetEquals(huSlots))
            {
                mismatches.Add($"{key} (en: {string.Join(",", enSlots)} vs hu: {string.Join(",", huSlots)})");
            }
        }
        Assert.True(mismatches.Count == 0, $"Placeholder mismatches: {string.Join("; ", mismatches)}");
    }

    [Fact]
    public void Loc_switches_language_at_runtime()
    {
        var original = Loc.LanguageCode;
        try
        {
            Loc.SetLanguage("en");
            Assert.Equal("Backup now", Loc.T("Sidebar_BackupNow"));
            Loc.SetLanguage("hu");
            Assert.Equal("Mentés most", Loc.T("Sidebar_BackupNow"));
        }
        finally
        {
            Loc.SetLanguage(original);
        }
    }

    [Fact]
    public void Normalize_falls_back_to_supported_language()
    {
        Assert.Equal("en", Loc.Normalize("en"));
        Assert.Equal("hu", Loc.Normalize("HU"));
        var fallback = Loc.Normalize("de");
        Assert.Contains(fallback, new[] { "en", "hu" });
        Assert.Equal(Loc.Normalize(null), Loc.Normalize("auto"));
    }

    private static HashSet<string> Placeholders(string value)
        => Regex.Matches(value, @"\{(\d+)")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
}
