using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudePortable.App.Ui.Services;

public sealed record AppSettings
{
    [JsonPropertyName("language")]
    public string? Language { get; init; }
}

/// <summary>
/// Persists app-level preferences (currently only the UI language) to
/// %LOCALAPPDATA%\ClaudePortable\settings.json, next to targets.json.
/// </summary>
[SupportedOSPlatform("windows")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822", Justification = "Instance methods for DI and mocking.")]
public sealed class SettingsStore
{
    private static readonly string ConfigPath = Path.Combine(
        Environment.ExpandEnvironmentVariables("%LOCALAPPDATA%"),
        "ClaudePortable",
        "settings.json");

    public AppSettings Load()
    {
        if (!File.Exists(ConfigPath))
        {
            return new AppSettings();
        }
        try
        {
            var json = File.ReadAllText(ConfigPath);
            return JsonSerializer.Deserialize(json, SettingsStoreJsonContext.Default.AppSettings) ?? new AppSettings();
        }
        catch (JsonException)
        {
            return new AppSettings();
        }
        catch (IOException)
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        var json = JsonSerializer.Serialize(settings, SettingsStoreJsonContext.Default.AppSettings);
        File.WriteAllText(ConfigPath, json);
    }

    public void SaveLanguage(string language)
        => Save(Load() with { Language = language });
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsStoreJsonContext : JsonSerializerContext
{
}
