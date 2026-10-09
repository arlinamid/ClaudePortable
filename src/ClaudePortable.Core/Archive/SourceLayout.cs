namespace ClaudePortable.Core.Archive;

/// <summary>
/// Maps discovery keys (see WindowsPathDiscovery) to the archive prefix their
/// files are stored under inside the ZIP. Backup uses it to lay out the
/// archive; restore uses the reverse direction to re-resolve where a prefix
/// belongs on the restore machine, which can differ from the backup machine
/// (Store vs. non-Store install, a different %CODEX_HOME%, ...).
/// </summary>
public static class SourceLayout
{
    private static readonly (string Key, string Prefix)[] Map =
    [
        ("claudeDesktopAppData", "claude-desktop/appdata"),
        ("claudeDesktopLocalAppData", "claude-desktop/localappdata"),
        ("claudeCodeUserProfile", "claude-code/dotclaude"),
        ("codexUserProfile", "codex/dotcodex"),
        ("codexDesktopAppData", "codex-desktop/appdata"),
        ("agentsUserProfile", "agents/dotagents"),
    ];

    public static string PrefixFor(string key)
    {
        foreach (var (k, prefix) in Map)
        {
            if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
            {
                return prefix;
            }
        }
        return key;
    }

    public static string? KeyFor(string archivePrefix)
    {
        foreach (var (key, p) in Map)
        {
            if (string.Equals(p, archivePrefix, StringComparison.OrdinalIgnoreCase))
            {
                return key;
            }
        }
        return null;
    }
}
