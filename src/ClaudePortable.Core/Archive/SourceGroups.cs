using ClaudePortable.Core.Manifest;

namespace ClaudePortable.Core.Archive;

/// <summary>
/// User-facing selection unit for backup and restore. Every discovered source
/// belongs to one or more groups:
/// <list type="bullet">
/// <item><c>claude-desktop</c> - Claude Desktop app data (incl. Cowork session metadata)</item>
/// <item><c>cowork</c> - project folders opened in Cowork sessions</item>
/// <item><c>claude-code</c> - %USERPROFILE%\.claude</item>
/// <item><c>codex</c> - %CODEX_HOME% / .codex and the Codex desktop app</item>
/// </list>
/// The shared skill store (%USERPROFILE%\.agents) belongs to both
/// claude-code and codex, because both agents' skills live (or are linked)
/// there: selecting either agent keeps its skills intact.
/// </summary>
public static class SourceGroups
{
    public const string ClaudeDesktop = "claude-desktop";
    public const string Cowork = "cowork";
    public const string ClaudeCode = "claude-code";
    public const string Codex = "codex";

    /// <summary>All groups, in display order.</summary>
    public static IReadOnlyList<string> All { get; } = [ClaudeDesktop, Cowork, ClaudeCode, Codex];

    private static readonly Dictionary<string, string[]> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["claude"] = [ClaudeDesktop, Cowork, ClaudeCode],
        ["all"] = [ClaudeDesktop, Cowork, ClaudeCode, Codex],
    };

    /// <summary>Groups a discovery key (or "coworkProject:&lt;hash&gt;") belongs to.</summary>
    public static IReadOnlyList<string> ForSourceKey(string key)
    {
        if (key.StartsWith("coworkProject:", StringComparison.OrdinalIgnoreCase))
        {
            return [Cowork];
        }
        return key switch
        {
            "claudeDesktopAppData" or "claudeDesktopLocalAppData" => [ClaudeDesktop],
            "claudeCodeUserProfile" => [ClaudeCode],
            "codexUserProfile" or "codexDesktopAppData" => [Codex],
            "agentsUserProfile" => [ClaudeCode, Codex],
            _ => All,
        };
    }

    /// <summary>Groups an archive prefix belongs to (incl. legacy prefixes).</summary>
    public static IReadOnlyList<string> ForArchivePrefix(string archivePrefix)
    {
        if (archivePrefix.StartsWith("cowork-projects/", StringComparison.OrdinalIgnoreCase))
        {
            return [Cowork];
        }
        if (archivePrefix.Equals("cowork/sessions", StringComparison.OrdinalIgnoreCase))
        {
            return [ClaudeDesktop];
        }
        var key = SourceLayout.KeyFor(archivePrefix);
        return key is null ? All : ForSourceKey(key);
    }

    public static bool IsSelected(IReadOnlyList<string> groupsOfSource, IReadOnlySet<string>? selection)
        => selection is null || groupsOfSource.Any(selection.Contains);

    /// <summary>
    /// Resolve CLI-style --include / --skip lists ("claude-code,codex",
    /// aliases "claude" and "all") into a selection. Returns null when the
    /// result is "everything", so callers keep the full-backup behaviour.
    /// Throws <see cref="ArgumentException"/> on unknown names or an empty result.
    /// </summary>
    public static IReadOnlySet<string>? Resolve(IEnumerable<string>? include, IEnumerable<string>? skip)
    {
        var includeList = Expand(include);
        var skipList = Expand(skip);
        var selection = new SortedSet<string>(
            includeList.Count > 0 ? includeList : All,
            StringComparer.OrdinalIgnoreCase);
        selection.ExceptWith(skipList);

        if (selection.Count == 0)
        {
            throw new ArgumentException("The selection is empty: nothing would be backed up or restored.");
        }
        return selection.SetEquals(All) ? null : selection;
    }

    /// <summary>
    /// Groups a backup was made with. Backups from before selections existed
    /// (no "groups" field) were always full backups.
    /// </summary>
    public static IReadOnlyList<string> SelectionOf(BackupManifest manifest)
        => manifest.Groups is { Count: > 0 } groups ? groups : All;

    /// <summary>Groups that actually have data in the backup.</summary>
    public static IReadOnlyList<string> ContentsOf(BackupManifest manifest)
    {
        var sourceKeys = manifest.ArchiveTargets.Count > 0
            ? manifest.ArchiveTargets.Keys.Select(p => SourceLayout.KeyFor(p) ?? p)
            : manifest.SourcePaths.Keys;
        // The shared .agents store does not by itself mean a backup holds
        // Claude Code AND Codex; it only counts when it is all there is.
        var direct = sourceKeys.Where(k => k != "agentsUserProfile").ToList();
        var groups = (direct.Count > 0 ? direct : sourceKeys)
            .SelectMany(k => k.Contains('/', StringComparison.Ordinal) ? ForArchivePrefix(k) : ForSourceKey(k))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return All.Where(groups.Contains).ToList();
    }

    /// <summary>Filename marker for a partial backup, e.g. "claude-code+codex"; null for a full one.</summary>
    public static string? FilenameTag(IReadOnlySet<string>? selection)
        => selection is null ? null : string.Join('+', All.Where(selection.Contains));

    private static List<string> Expand(IEnumerable<string>? names)
    {
        var result = new List<string>();
        if (names is null)
        {
            return result;
        }
        foreach (var raw in names.SelectMany(n => n.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
        {
            if (Aliases.TryGetValue(raw, out var expanded))
            {
                result.AddRange(expanded);
            }
            else if (All.Contains(raw, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(All.First(g => g.Equals(raw, StringComparison.OrdinalIgnoreCase)));
            }
            else
            {
                throw new ArgumentException(
                    $"Unknown group '{raw}'. Valid: {string.Join(", ", All)}, claude, all.");
            }
        }
        return result;
    }
}
