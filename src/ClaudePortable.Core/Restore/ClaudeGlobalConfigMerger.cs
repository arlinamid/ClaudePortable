using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudePortable.Core.Restore;

/// <summary>
/// Restores Claude Code's global config (%USERPROFILE%\.claude.json) by
/// merging, never by overwriting.
/// <para>
/// The file holds the user's configuration (user-scoped MCP servers, every
/// project's allowed tools / MCP servers / trust decision, onboarding state)
/// side by side with identifiers of this machine and account (userID,
/// machineID, oauthAccount, ...) and server-side caches. Copying the whole
/// file onto another PC would clone the old machine's identity, so:
/// </para>
/// <list type="bullet">
/// <item>identity keys and caches always come from the file already here
/// (or are left out, and Claude Code creates them on first start);</item>
/// <item>"projects" and "mcpServers" are merged entry by entry, the backup
/// winning for an entry both sides have;</item>
/// <item>every other key is taken from the backup, and keys only this
/// machine has are kept.</item>
/// </list>
/// </summary>
public static class ClaudeGlobalConfigMerger
{
    /// <summary>Keys that identify this machine / install / account.</summary>
    internal static readonly HashSet<string> MachineKeys = new(StringComparer.Ordinal)
    {
        "userID",
        "anonymousId",
        "machineID",
        "remoteControlMachineId",
        "oauthAccount",
        "installMethod",
        "autoUpdates",
        "autoUpdatesProtectedForNative",
        "firstStartTime",
        "numStartups",
        "claudeCodeFirstTokenDate",
    };

    private static readonly string[] MergedObjects = ["projects", "mcpServers"];

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>Server-side caches (refetched on start, and tied to the account).</summary>
    internal static bool IsCacheKey(string key)
        => key.StartsWith("cached", StringComparison.Ordinal)
           || key.EndsWith("Cache", StringComparison.Ordinal)
           || key.EndsWith("CacheSlots", StringComparison.Ordinal);

    public static string Merge(string backupJson, string? existingJson)
    {
        var backup = JsonNode.Parse(backupJson) as JsonObject
            ?? throw new InvalidDataException("The backed-up .claude.json is not a JSON object.");
        var existing = string.IsNullOrWhiteSpace(existingJson) ? null : TryParseObject(existingJson);
        var result = existing is null ? new JsonObject() : (JsonObject)existing.DeepClone();

        foreach (var (key, value) in backup)
        {
            if (MachineKeys.Contains(key) || IsCacheKey(key))
            {
                continue;
            }
            if (MergedObjects.Contains(key, StringComparer.Ordinal)
                && value is JsonObject fromBackup
                && result[key] is JsonObject here)
            {
                foreach (var (entryKey, entryValue) in fromBackup)
                {
                    here[entryKey] = entryValue?.DeepClone();
                }
                continue;
            }
            result[key] = value?.DeepClone();
        }

        return result.ToJsonString(Indented);
    }

    private static JsonObject? TryParseObject(string json)
    {
        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            // A corrupt local file is replaced by the backup's content
            // (minus identity keys); the original stays in the safety copy.
            return null;
        }
    }
}
