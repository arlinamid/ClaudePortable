using ClaudePortable.Core.Abstractions;

namespace ClaudePortable.Core.Discovery;

public sealed class WindowsPathDiscovery : IPathDiscovery
{
    // Each known Claude artefact has a list of candidate paths. We use the
    // FIRST one that looks accessible at discovery time. This matters for
    // the Store-installed Claude Desktop where %APPDATA%\Claude is a
    // reparse point into %LOCALAPPDATA%\Packages\Claude_pzs8sxrjxfjjc\...
    // Some process contexts (e.g. a portable exe flagged with
    // Mark-of-the-Web after being downloaded / synced through OneDrive)
    // fail Directory.Exists on the reparse point even though the target
    // is fully accessible. Enumerating the Packages path directly bypasses
    // the reparse-point resolution.
    private static readonly (string Key, string[] Candidates, string Source)[] KnownPaths =
    [
        (
            "claudeDesktopAppData",
            new[]
            {
                @"%APPDATA%\Claude",
                @"%LOCALAPPDATA%\Packages\Claude_pzs8sxrjxfjjc\LocalCache\Roaming\Claude",
            },
            "Spec 1.1 + Store-app reparse fallback"
        ),
        (
            "claudeCodeUserProfile",
            new[] { @"%USERPROFILE%\.claude" },
            "Spec 1.1 + verified 2026-04-22"
        ),
        // NOTE: there is intentionally no "coworkSessions" entry here.
        // The original Spec 1.1 guess of %USERPROFILE%\.cowork was never
        // verified and does not exist on the Store-app Claude Desktop.
        // Phase-0 verification (docs/discovered-paths.md, 2026-04-23)
        // established that Cowork session state actually lives under
        // %APPDATA%\Claude\local-agent-mode-sessions\<guid>\..., which is a
        // subfolder of "claudeDesktopAppData" above and is therefore already
        // backed up. The user-selected project roots referenced by those
        // sessions are captured separately by CoworkProjectDiscovery under
        // the "cowork-projects/<hash>" archive prefix. A standalone .cowork
        // row would only ever show EXISTS = false and would duplicate the
        // local-agent-mode-sessions subtree in the ZIP if repointed.
        (
            "claudeDesktopLocalAppData",
            new[]
            {
                @"%LOCALAPPDATA%\Claude",
                @"%LOCALAPPDATA%\Packages\Claude_pzs8sxrjxfjjc\LocalCache\Local\Claude",
            },
            "Spec 1.1 + Store-app reparse fallback"
        ),
        // OpenAI Codex. The CLI, the IDE extension and the Codex desktop
        // app all share one state root: %CODEX_HOME%, defaulting to
        // %USERPROFILE%\.codex (config.toml, AGENTS.md, sessions/,
        // skills/, rules/, memories, sqlite state). Credentials and
        // regenerable caches/binaries inside it are filtered by
        // DefaultExclusions under the "codex/dotcodex" prefix.
        (
            "codexUserProfile",
            new[]
            {
                @"%CODEX_HOME%",
                @"%USERPROFILE%\.codex",
            },
            "Codex CLI/desktop state root, verified 2026-10-09"
        ),
        // Codex desktop (Electron, Store-packaged as OpenAI.Codex_<publisherId>).
        // Same reparse-point story as Claude Desktop: %APPDATA%\Codex is
        // redirected into the package's LocalCache. The package folder is
        // matched by wildcard so a different publisher-id suffix still works.
        (
            "codexDesktopAppData",
            new[]
            {
                @"%APPDATA%\Codex",
                @"%LOCALAPPDATA%\Packages\OpenAI.Codex_*\LocalCache\Roaming\Codex",
            },
            "Codex desktop Store-app, verified 2026-10-09"
        ),
    ];

    public IReadOnlyList<DiscoveredClaudePath> Discover()
    {
        return KnownPaths
            .Select(kp =>
            {
                string? first = null;
                foreach (var rel in kp.Candidates)
                {
                    var expanded = ExpandCandidate(rel);
                    if (expanded is null)
                    {
                        continue;
                    }
                    first ??= expanded;
                    if (SafeDirectoryExists(expanded))
                    {
                        return new DiscoveredClaudePath(kp.Key, expanded, true, kp.Source);
                    }
                }
                return new DiscoveredClaudePath(kp.Key, first ?? string.Empty, false, kp.Source);
            })
            .ToList();
    }

    /// <summary>
    /// Expand environment variables and resolve a single "Name_*" wildcard
    /// segment (used for Store package folders). Returns null when the
    /// candidate references an unset environment variable, so optional
    /// overrides like %CODEX_HOME% are skipped instead of reported literally.
    /// </summary>
    private static string? ExpandCandidate(string candidate)
    {
        var expanded = Environment.ExpandEnvironmentVariables(candidate);
        if (expanded.Contains('%', StringComparison.Ordinal))
        {
            return null;
        }

        var segments = expanded.Split(Path.DirectorySeparatorChar);
        var wildcardIndex = Array.FindIndex(segments, s => s.Contains('*', StringComparison.Ordinal));
        if (wildcardIndex <= 0)
        {
            return expanded;
        }

        // string.Join, not Path.Combine: Path.Combine("C:", "Users") yields
        // the drive-relative "C:Users".
        var parent = string.Join(Path.DirectorySeparatorChar, segments[..wildcardIndex]);
        var tail = segments[(wildcardIndex + 1)..];
        try
        {
            var match = Directory.Exists(parent)
                ? Directory.EnumerateDirectories(parent, segments[wildcardIndex], SearchOption.TopDirectoryOnly)
                    .Select(d => string.Join(Path.DirectorySeparatorChar, [d, .. tail]))
                    .FirstOrDefault(SafeDirectoryExists)
                : null;
            return match ?? expanded;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return expanded;
        }
    }

    private static bool SafeDirectoryExists(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }
        try
        {
            if (Directory.Exists(path))
            {
                return true;
            }
            // Some reparse points + ACL combos make Directory.Exists lie.
            // Double-check by asking the DirectoryInfo cache and, as a last
            // resort, by having the parent directory list its children.
            var info = new DirectoryInfo(path);
            if (info.Exists)
            {
                return true;
            }
            var parent = info.Parent;
            if (parent is null || !parent.Exists)
            {
                return false;
            }
            return parent.EnumerateDirectories(info.Name, SearchOption.TopDirectoryOnly).Any();
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
