using System.Text.RegularExpressions;

namespace ClaudePortable.Core.Archive;

/// <summary>
/// Archive-path globs: "*" stays within a path segment, "**" spans segments.
/// A glob starting with "!" re-includes what an earlier glob excluded, like
/// .gitignore: the LAST matching glob decides. This lets a large folder be
/// excluded wholesale while a few things inside it are kept (the Codex app's
/// Chromium profile, of which only the app state is wanted).
/// </summary>
public sealed class ExclusionGlob
{
    private readonly List<(Regex Rx, bool Include, string LiteralPrefix)> _compiled;

    public ExclusionGlob(IEnumerable<string> globs)
    {
        _compiled = globs.Select(g => g.StartsWith('!')
                ? (Compile(g[1..]), true, LiteralPrefix(g[1..]))
                : (Compile(g), false, string.Empty))
            .ToList();
    }

    public bool IsExcluded(string relativePath)
    {
        var decisive = LastMatch(relativePath.Replace('\\', '/'));
        return decisive >= 0 && !_compiled[decisive].Include;
    }

    /// <summary>Index of the last glob matching the path, or -1.</summary>
    private int LastMatch(string normalized)
    {
        for (var i = _compiled.Count - 1; i >= 0; i--)
        {
            if (_compiled[i].Rx.IsMatch(normalized))
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// Whether a whole directory can be skipped without listing it: it is
    /// excluded, and no re-include glob can match anything below it.
    /// <paramref name="directoryPath"/> ends with "/".
    /// </summary>
    public bool IsDirectoryPruned(string directoryPath)
    {
        var normalized = directoryPath.Replace('\\', '/');
        var decisive = LastMatch(normalized);
        if (decisive < 0 || _compiled[decisive].Include)
        {
            return false;
        }
        // Only a re-include AFTER the glob that excludes this directory can
        // bring back something below it.
        for (var i = decisive + 1; i < _compiled.Count; i++)
        {
            var (_, include, prefix) = _compiled[i];
            if (!include)
            {
                continue;
            }
            // The re-include glob reaches below this directory when one of
            // the two literal prefixes contains the other.
            if (prefix.StartsWith(normalized, StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>The part of a glob before its first wildcard, e.g. "a/b/" for "a/b/**".</summary>
    private static string LiteralPrefix(string glob)
    {
        var wildcard = glob.IndexOfAny(['*', '?']);
        return wildcard < 0 ? glob : glob[..wildcard];
    }

    private static Regex Compile(string glob)
    {
        var pattern = new System.Text.StringBuilder("^");
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            if (c == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
            {
                pattern.Append(".*");
                i++;
                if (i + 1 < glob.Length && glob[i + 1] == '/')
                {
                    i++;
                }
            }
            else if (c == '*')
            {
                pattern.Append("[^/]*");
            }
            else if (c == '?')
            {
                pattern.Append("[^/]");
            }
            else if ("+()^$.{}[]|\\".Contains(c))
            {
                pattern.Append('\\').Append(c);
            }
            else
            {
                pattern.Append(c);
            }
        }
        pattern.Append('$');
        return new Regex(pattern.ToString(), RegexOptions.Compiled | RegexOptions.IgnoreCase);
    }
}
