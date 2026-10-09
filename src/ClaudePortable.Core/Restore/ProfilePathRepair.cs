using System.Globalization;
using System.Text.RegularExpressions;
using ClaudePortable.Core.Abstractions;

namespace ClaudePortable.Core.Restore;

/// <summary>
/// Repairs agent state that still points at another user's profile, in
/// place, on the current machine. This is for data that was restored onto
/// a differently named profile before restore rewrote everything (e.g.
/// laptop "Janos" -> desktop "János" with 0.4.0, where Codex then fails with
/// "failed to resolve rollout path C:\Users\Janos\.codex\sessions\...").
/// <list type="number">
/// <item>Detect: the current profile comes from %USERPROFILE%. The agent
/// folders are scanned for X:\Users\&lt;name&gt;\.codex|.claude|.agents
/// paths whose &lt;name&gt; is not the current user.</item>
/// <item>Repair: the same rewrite restore uses (text files, .jsonl, SQLite,
/// Claude Code project folder names), from each detected profile to the
/// current one. Every file is copied to a repair-backup folder before it is
/// changed.</item>
/// </list>
/// </summary>
public static partial class ProfilePathRepair
{
    // A path INTO one of the agents' own folders under some user's profile:
    // C:\Users\Janos\.codex, C:\\Users\\Janos\\.claude, C:/Users/Janos/.agents
    [GeneratedRegex(@"(?<drive>[A-Za-z]):(?:\\\\|\\|/)Users(?:\\\\|\\|/)(?<user>[^\\/""':\r\n]+?)(?:\\\\|\\|/)\.(?:codex|claude|agents)(?![A-Za-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex AgentFolderPath();

    [GeneratedRegex(@"\\u(?<hex>[0-9a-fA-F]{4})")]
    private static partial Regex JsonUnicodeEscape();

    public sealed record ForeignProfile(string ProfileRoot, int Occurrences);

    public sealed record RepairResult(
        IReadOnlyList<ForeignProfile> RepairedProfiles,
        int FilesChanged,
        int ValuesChanged,
        string? OriginalsBackupFolder,
        IReadOnlyList<string> Warnings);

    /// <summary>
    /// The agent state folders the repair works on: Codex, Claude Code and the
    /// shared .agents store, wherever discovery finds them on this machine.
    /// Claude Desktop's app data is left alone; its JSON configs were already
    /// rewritten by every restore version.
    /// </summary>
    public static IReadOnlyList<(string Key, string Folder)> AgentFolders(IPathDiscovery discovery)
        => discovery.Discover()
            .Where(p => p.Exists && p.Key is "codexUserProfile" or "claudeCodeUserProfile" or "agentsUserProfile")
            .Select(p => (p.Key, p.Path))
            .ToList();

    /// <summary>Profiles other than <paramref name="currentProfile"/> that the agent state points into.</summary>
    public static IReadOnlyList<ForeignProfile> Detect(IEnumerable<string> agentFolders, string currentProfile)
    {
        var currentUser = Path.GetFileName(currentProfile.TrimEnd('\\', '/'));
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        void Visit(string text)
        {
            foreach (Match m in AgentFolderPath().Matches(text))
            {
                var user = DecodeJsonUnicode(m.Groups["user"].Value);
                if (string.Equals(user, currentUser, StringComparison.OrdinalIgnoreCase) || !IsPlausibleProfileName(user))
                {
                    continue;
                }
                var root = $@"{char.ToUpperInvariant(m.Groups["drive"].Value[0])}:\Users\{user}";
                counts[root] = counts.GetValueOrDefault(root) + 1;
            }
        }

        foreach (var folder in agentFolders.Where(Directory.Exists))
        {
            // Small text state at the top of each agent folder (config.toml,
            // settings.json, ...) plus every SQLite database: that is where
            // the absolute paths live. Session .jsonl files are not needed
            // for detection and can run to gigabytes.
            foreach (var file in Directory.EnumerateFiles(folder, "*.*", SearchOption.TopDirectoryOnly)
                         .Where(f => f.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".toml", StringComparison.OrdinalIgnoreCase)))
            {
                if (new FileInfo(file).Length < 4 * 1024 * 1024 && TryReadText(file) is { } text)
                {
                    Visit(text);
                }
            }
            // Logs and memory notes are free text: they quote example paths
            // ("C:\Users\<myname>\.codex", "C:\Users\...\.codex") that are
            // not profiles. Detection reads structural state only; the repair
            // itself still rewrites real old-profile paths everywhere.
            foreach (var db in Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
                         .Where(IsDatabaseName)
                         .Where(f => !Path.GetFileName(f).StartsWith("logs", StringComparison.OrdinalIgnoreCase)
                                     && !Path.GetFileName(f).StartsWith("memories", StringComparison.OrdinalIgnoreCase))
                         .Where(SqlitePathRewriter.IsSqliteDatabase))
            {
                SqlitePathRewriter.Scan(db, v => v.Contains(@"Users", StringComparison.OrdinalIgnoreCase), Visit, maxValuesPerColumn: 2000);
            }
        }

        return counts
            .Select(kv => new ForeignProfile(kv.Key, kv.Value))
            .OrderByDescending(p => p.Occurrences)
            .ToList();
    }

    /// <summary>
    /// Rewrite every <paramref name="profiles"/> root to <paramref name="currentProfile"/>
    /// inside the agent folders. Originals are copied under
    /// <paramref name="originalsBackupRoot"/> before any change.
    /// </summary>
    public static RepairResult Repair(
        IReadOnlyList<(string Key, string Folder)> agentFolders,
        IReadOnlyList<ForeignProfile> profiles,
        string currentProfile,
        string originalsBackupRoot,
        IProgress<OperationProgress>? progress = null)
    {
        var warnings = new List<string>();
        var filesChanged = 0;
        var valuesChanged = 0;
        var backupUsed = false;

        foreach (var (key, folder) in agentFolders)
        {
            if (!Directory.Exists(folder))
            {
                continue;
            }
            foreach (var profile in profiles)
            {
                progress?.Report(new OperationProgress($"Repairing {key}: {profile.ProfileRoot} -> {currentProfile}"));
                var result = new PathRewriter().Rewrite(
                    folder,
                    profile.ProfileRoot,
                    currentProfile,
                    beforeModify: file =>
                    {
                        var copy = Path.Combine(originalsBackupRoot, key, Path.GetRelativePath(folder, file));
                        if (!File.Exists(copy))
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                            File.Copy(file, copy);
                            // A live SQLite database may hold committed data
                            // in its write-ahead log; keep it with the copy.
                            if (File.Exists(file + "-wal"))
                            {
                                File.Copy(file + "-wal", copy + "-wal", overwrite: true);
                            }
                            backupUsed = true;
                        }
                    });
                filesChanged += result.FilesChanged;
                valuesChanged += result.ReplacementsMade;

                if (key == "claudeCodeUserProfile")
                {
                    warnings.AddRange(ClaudeCodeProjectFolders.RenameForProfile(
                        Path.Combine(folder, "projects"), profile.ProfileRoot, currentProfile));
                }
            }
        }

        return new RepairResult(profiles, filesChanged, valuesChanged, backupUsed ? originalsBackupRoot : null, warnings);
    }

    private static readonly string[] SystemProfiles = ["Public", "Default", "Default User", "All Users", "defaultuser0"];

    /// <summary>
    /// A real profile folder name: no characters Windows forbids in file
    /// names, not just dots (an elided "C:\Users\...") and not a system profile.
    /// </summary>
    internal static bool IsPlausibleProfileName(string name)
        => name.Trim().Length > 0
           && name.IndexOfAny(['<', '>', ':', '"', '|', '?', '*', '/', '\\']) < 0
           && name.Trim('.', ' ').Length > 0
           && !SystemProfiles.Contains(name, StringComparer.OrdinalIgnoreCase);

    private static bool IsDatabaseName(string path)
        => path.EndsWith(".sqlite", StringComparison.OrdinalIgnoreCase)
           || path.EndsWith(".sqlite3", StringComparison.OrdinalIgnoreCase)
           || path.EndsWith(".db", StringComparison.OrdinalIgnoreCase);

    private static string? TryReadText(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string DecodeJsonUnicode(string value)
        => value.Contains(@"\u", StringComparison.Ordinal)
            ? JsonUnicodeEscape().Replace(value, m => ((char)int.Parse(m.Groups["hex"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToString())
            : value;
}
