using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ClaudePortable.Core.Abstractions;

namespace ClaudePortable.Core.Restore;

/// <summary>
/// Rewrites absolute paths that point into the backup machine's user profile
/// so they point into the restore machine's profile instead, e.g.
/// C:\Users\Janos\... -> C:\Users\János\... It covers:
/// <list type="bullet">
/// <item>text state: *.json, *.toml (Codex config.toml), *.jsonl (Codex and
/// Claude Code session files);</item>
/// <item>SQLite databases (*.sqlite, *.sqlite3, *.db): Codex keeps every
/// conversation's absolute file path in state_5.sqlite (threads.rollout_path)
/// and refuses to resume a conversation whose file it cannot find.</item>
/// </list>
/// User-written documents (*.md and so on) are deliberately left alone.
/// </summary>
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

    private static readonly Regex JsonUnicodeEscape = new(
        @"\\u(?<hex>[0-9a-fA-F]{4})",
        RegexOptions.Compiled);

    private static readonly string[] TextPatterns = ["*.json", "*.toml", "*.jsonl"];
    private static readonly string[] SqlitePatterns = ["*.sqlite", "*.sqlite3", "*.db"];

    public PathRewriteResult Rewrite(string rootFolder, string oldUserProfile, string newUserProfile)
    {
        if (!Directory.Exists(rootFolder)
            || string.Equals(oldUserProfile.TrimEnd('\\', '/'), newUserProfile.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
        {
            return new PathRewriteResult(0, 0, 0);
        }

        var filesScanned = 0;
        var filesChanged = 0;
        var replacementsMade = 0;
        var markers = Markers(oldUserProfile);

        foreach (var file in TextPatterns.SelectMany(p => Directory.EnumerateFiles(rootFolder, p, SearchOption.AllDirectories)))
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

            // Cheap pre-check: session .jsonl files run to gigabytes and most
            // never mention the profile.
            if (!markers.Any(m => content.Contains(m, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var (replaced, newContent) = RewriteText(content, oldUserProfile, newUserProfile);
            if (replaced > 0)
            {
                File.WriteAllText(file, newContent, new UTF8Encoding(false));
                filesChanged++;
                replacementsMade += replaced;
            }
        }

        foreach (var db in SqlitePatterns.SelectMany(p => Directory.EnumerateFiles(rootFolder, p, SearchOption.AllDirectories)))
        {
            if (!SqlitePathRewriter.IsSqliteDatabase(db))
            {
                continue;
            }
            filesScanned++;
            var replaced = SqlitePathRewriter.Rewrite(
                db,
                value => markers.Any(m => value.Contains(m, StringComparison.OrdinalIgnoreCase)),
                value => RewriteText(value, oldUserProfile, newUserProfile).NewContent);
            if (replaced > 0)
            {
                filesChanged++;
                replacementsMade += replaced;
            }
        }

        return new PathRewriteResult(filesScanned, filesChanged, replacementsMade);
    }

    /// <summary>
    /// Whole-profile rewrite first: handles profiles outside X:\Users (e.g.
    /// D:\Profiles\jane) and a different drive or profile folder name on the
    /// restore machine. The user-name pass then catches \Users\&lt;old&gt;\
    /// references on other drives.
    /// </summary>
    internal static (int Replacements, string NewContent) RewriteText(string content, string oldUserProfile, string newUserProfile)
        => ProfileRewrite.For(oldUserProfile, newUserProfile).Apply(content);

    /// <summary>
    /// Both rules (whole profile root, then \Users\&lt;name&gt;\ on any
    /// drive) in ONE regex, applied in one left-to-right pass. Running them
    /// as two passes rewrote the output of the first: when the new profile
    /// path itself contains the old one (restoring with --target-user into
    /// C:\Users\a\restore\b), C:\Users\a\x became C:\Users\a\restore\b\x
    /// and then C:\Users\b\restore\b\x. Cached per profile pair because the
    /// SQLite rewrite calls this once per matching value.
    /// </summary>
    private sealed class ProfileRewrite
    {
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string, string), ProfileRewrite> Cache = new();

        private readonly Regex _pattern;
        private readonly string[] _profileReplacements;
        private readonly string _oldUserName;
        private readonly string _newUserName;

        private ProfileRewrite(string oldUserProfile, string newUserProfile)
        {
            var oldRoot = oldUserProfile.TrimEnd('\\', '/');
            var newRoot = newUserProfile.TrimEnd('\\', '/');
            _oldUserName = Path.GetFileName(oldRoot);
            _newUserName = Path.GetFileName(newRoot);

            var escapedOld = oldRoot.Replace(@"\", @"\\", StringComparison.Ordinal);
            var escapedNew = newRoot.Replace(@"\", @"\\", StringComparison.Ordinal);
            var variants = new List<(string From, string To)>();
            if (oldRoot.Length >= 3 && newRoot.Length > 0)
            {
                variants.Add((escapedOld, escapedNew));
                variants.Add((oldRoot, newRoot));
                variants.Add((oldRoot.Replace('\\', '/'), newRoot.Replace('\\', '/')));
                if (oldRoot.Any(c => c > 127))
                {
                    variants.Add((ToJsonUnicodeEscapes(escapedOld), escapedNew));
                }
            }
            _profileReplacements = [.. variants.Select(v => v.To)];

            // Profile alternatives come first, so at the same position the
            // exact profile root wins over the generic \Users\<name>\ rule.
            // No whitespace in the boundary: "C:\Users\John" must not
            // rewrite the prefix of "C:\Users\John Smith".
            var alternatives = variants
                .Select((v, i) => $@"(?<p{i}>{Regex.Escape(v.From)})(?=[\\/""';,]|$)")
                .Concat(
                [
                    @"(?<e>(?<ed>[A-Z]):\\\\Users\\\\(?<eu>[^\\"":\r\n/]+)\\\\)",
                    @"(?<s>(?<sd>[A-Z]):\\Users\\(?<su>[^\\"":\r\n/]+)\\)",
                    @"(?<f>(?<fd>[A-Z]):/Users/(?<fu>[^/""\\:\r\n]+)/)",
                ]);
            _pattern = new Regex(
                string.Join("|", alternatives),
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
        }

        public static ProfileRewrite For(string oldUserProfile, string newUserProfile)
            => Cache.GetOrAdd((oldUserProfile, newUserProfile), k => new ProfileRewrite(k.Item1, k.Item2));

        public (int Replacements, string NewContent) Apply(string content)
        {
            var count = 0;
            var result = _pattern.Replace(content, m =>
            {
                for (var i = 0; i < _profileReplacements.Length; i++)
                {
                    if (m.Groups[$"p{i}"].Success)
                    {
                        count++;
                        return _profileReplacements[i];
                    }
                }

                var renameUser = !string.IsNullOrEmpty(_oldUserName)
                    && !string.Equals(_oldUserName, _newUserName, StringComparison.OrdinalIgnoreCase);
                if (renameUser && m.Groups["e"].Success && IsOldUser(m.Groups["eu"].Value))
                {
                    count++;
                    return $@"{m.Groups["ed"].Value}:\\Users\\{_newUserName}\\";
                }
                if (renameUser && m.Groups["s"].Success && IsOldUser(m.Groups["su"].Value))
                {
                    count++;
                    return $@"{m.Groups["sd"].Value}:\Users\{_newUserName}\";
                }
                if (renameUser && m.Groups["f"].Success && IsOldUser(m.Groups["fu"].Value))
                {
                    count++;
                    return $"{m.Groups["fd"].Value}:/Users/{_newUserName}/";
                }
                return m.Value;
            });
            return (count, result);
        }

        private bool IsOldUser(string user)
            => string.Equals(user, _oldUserName, StringComparison.OrdinalIgnoreCase)
               || string.Equals(DecodeJsonUnicodeEscapes(user), _oldUserName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Replace the old profile root with the new one in its common encodings:
    /// JSON-escaped (C:\\Users\\a), plain (C:\Users\a), forward slash
    /// (C:/Users/a), and JSON with \uXXXX escapes for non-ASCII user names
    /// (C:\\Users\\J\u00e1nos). The match must end at a path boundary so
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

        var escapedOld = oldRoot.Replace(@"\", @"\\", StringComparison.Ordinal);
        var escapedNew = newRoot.Replace(@"\", @"\\", StringComparison.Ordinal);
        var variants = new List<(string From, string To)>
        {
            (escapedOld, escapedNew),
            (oldRoot, newRoot),
            (oldRoot.Replace('\\', '/'), newRoot.Replace('\\', '/')),
        };
        if (oldRoot.Any(c => c > 127))
        {
            variants.Add((ToJsonUnicodeEscapes(escapedOld), escapedNew));
        }

        var count = 0;
        var result = content;
        foreach (var (from, to) in variants)
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
        return string.Equals(user, oldUserName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(DecodeJsonUnicodeEscapes(user), oldUserName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Strings whose presence means a value may need rewriting.</summary>
    private static string[] Markers(string oldUserProfile)
    {
        var oldUserName = Path.GetFileName(oldUserProfile.TrimEnd('\\', '/'));
        var markers = new List<string> { oldUserProfile.TrimEnd('\\', '/').Replace('\\', '/'), oldUserProfile.TrimEnd('\\', '/') };
        if (!string.IsNullOrEmpty(oldUserName))
        {
            markers.Add(oldUserName);
            if (oldUserName.Any(c => c > 127))
            {
                markers.Add(ToJsonUnicodeEscapes(oldUserName));
            }
        }
        return [.. markers.Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static string ToJsonUnicodeEscapes(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c > 127)
            {
                sb.Append(CultureInfo.InvariantCulture, $"\\u{(int)c:x4}");
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    private static string DecodeJsonUnicodeEscapes(string value)
        => value.Contains(@"\u", StringComparison.Ordinal)
            ? JsonUnicodeEscape.Replace(value, m => ((char)int.Parse(m.Groups["hex"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToString())
            : value;
}
