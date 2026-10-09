using System.Text.RegularExpressions;
using ClaudePortable.Core.Abstractions;

namespace ClaudePortable.Core.Restore;

public sealed class PathRewriter : IPathRewriter
{
    // Match any "<drive>:<sep>Users<sep><user><sep>" prefix. We intentionally
    // do NOT tie this to .claude / AppData / .cowork tails any more: Claude
    // Desktop configs and Claude Code session state frequently reference
    // arbitrary paths under the user's home (project folders, screenshots,
    // recently-opened files, ...). Those all need to follow the user-name
    // shift when a backup is restored onto a machine with a different
    // %USERPROFILE%. Overshooting is safe: rewriting C:\Users\sascha\foo
    // to C:\Users\sasch\foo at least points at the new user's home.
    private static readonly Regex EscapedBackslashPattern = new(
        @"(?<drive>[A-Z]):\\\\Users\\\\(?<user>[^\\\\""]+)\\\\",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SingleBackslashPattern = new(
        @"(?<drive>[A-Z]):\\Users\\(?<user>[^\\""]+)\\",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ForwardSlashPattern = new(
        @"(?<drive>[A-Z]):/Users/(?<user>[^/""]+)/",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public PathRewriteResult Rewrite(string rootFolder, string oldUserProfile, string newUserProfile)
    {
        if (!Directory.Exists(rootFolder))
        {
            return new PathRewriteResult(0, 0, 0);
        }

        var filesScanned = 0;
        var filesChanged = 0;
        var replacementsMade = 0;

        var oldUserName = Path.GetFileName(oldUserProfile.TrimEnd('\\', '/'));
        var newUserName = Path.GetFileName(newUserProfile.TrimEnd('\\', '/'));

        // *.toml covers Codex's config.toml, whose [projects."C:\Users\..."]
        // trust entries and MCP server paths are user-profile absolute.
        var candidates = Directory.EnumerateFiles(rootFolder, "*.json", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(rootFolder, "*.toml", SearchOption.AllDirectories));
        foreach (var file in candidates)
        {
            filesScanned++;
            string content;
            try
            {
                content = File.ReadAllText(file);
            }
            catch (IOException)
            {
                continue;
            }

            // Whole-profile rewrite first: handles profiles outside X:\Users
            // (e.g. D:\Profiles\jane) and a different drive or profile folder
            // name on the restore machine. The user-name pass then catches
            // \Users\<old>\ references on other drives.
            var (profileReplaced, afterProfile) = ReplaceProfileIn(content, oldUserProfile, newUserProfile);
            var (nameReplaced, newContent) = ReplaceIn(afterProfile, oldUserName, newUserName);
            var replaced = profileReplaced + nameReplaced;
            if (replaced > 0)
            {
                File.WriteAllText(file, newContent);
                filesChanged++;
                replacementsMade += replaced;
            }
        }

        return new PathRewriteResult(filesScanned, filesChanged, replacementsMade);
    }

    /// <summary>
    /// Replace the old profile root with the new one in its three common
    /// encodings: JSON-escaped (C:\\Users\\a), plain (C:\Users\a) and forward
    /// slash (C:/Users/a). The match must end at a path boundary so
    /// C:\Users\sam never rewrites C:\Users\samantha.
    /// </summary>
    internal static (int Replacements, string NewContent) ReplaceProfileIn(string content, string oldUserProfile, string newUserProfile)
    {
        var oldRoot = oldUserProfile.TrimEnd('\\', '/');
        var newRoot = newUserProfile.TrimEnd('\\', '/');
        if (oldRoot.Length < 3
            || newRoot.Length == 0
            || string.Equals(oldRoot, newRoot, StringComparison.OrdinalIgnoreCase))
        {
            return (0, content);
        }

        var count = 0;
        var result = content;
        foreach (var (from, to) in new[]
        {
            (oldRoot.Replace(@"\", @"\\", StringComparison.Ordinal), newRoot.Replace(@"\", @"\\", StringComparison.Ordinal)),
            (oldRoot, newRoot),
            (oldRoot.Replace('\\', '/'), newRoot.Replace('\\', '/')),
        })
        {
            var pattern = new Regex(
                // No whitespace in the boundary: "C:\Users\John" must not
                // rewrite the prefix of "C:\Users\John Smith".
                Regex.Escape(from) + @"(?=[\\/""';,]|$)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            result = pattern.Replace(result, _ =>
            {
                count++;
                return to;
            });
        }
        return (count, result);
    }

    internal static (int Replacements, string NewContent) ReplaceIn(string content, string oldUserName, string newUserName)
    {
        if (string.IsNullOrEmpty(oldUserName) || string.Equals(oldUserName, newUserName, StringComparison.OrdinalIgnoreCase))
        {
            return (0, content);
        }

        var count = 0;

        var stage1 = EscapedBackslashPattern.Replace(content, m =>
        {
            if (!MatchesOldUser(m, oldUserName))
            {
                return m.Value;
            }
            count++;
            return $@"{m.Groups["drive"].Value}:\\Users\\{newUserName}\\";
        });

        var stage2 = SingleBackslashPattern.Replace(stage1, m =>
        {
            if (!MatchesOldUser(m, oldUserName))
            {
                return m.Value;
            }
            count++;
            return $@"{m.Groups["drive"].Value}:\Users\{newUserName}\";
        });

        var stage3 = ForwardSlashPattern.Replace(stage2, m =>
        {
            if (!MatchesOldUser(m, oldUserName))
            {
                return m.Value;
            }
            count++;
            return $"{m.Groups["drive"].Value}:/Users/{newUserName}/";
        });

        return (count, stage3);
    }

    private static bool MatchesOldUser(Match m, string oldUserName)
    {
        var user = m.Groups["user"].Value;
        return string.Equals(user, oldUserName, StringComparison.OrdinalIgnoreCase);
    }
}
