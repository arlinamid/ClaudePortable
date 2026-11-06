using ClaudePortable.Core.Abstractions;

namespace ClaudePortable.Core.Discovery;

public sealed class WindowsPathDiscovery : IPathDiscovery
{
    // Each known Claude artefact has a list of candidate paths. We use the
    // FIRST one that looks accessible at discovery time. This matters for
    // the Store-installed Claude Desktop where %APPDATA%\Claude is a
    // reparse point into %LOCALAPPDATA%\Packages\Claude_<publisherId>\...
    // (matched by wildcard, so a different publisher id still resolves).
    // Some process contexts (e.g. a portable exe flagged with
    // Mark-of-the-Web after being downloaded / synced through OneDrive)
    // fail Directory.Exists on the reparse point even though the target
    // is fully accessible. Enumerating the Packages path directly bypasses
    // the reparse-point resolution.
    //
    // Claude Desktop ships as two MSIX packages: "Claude" (claude.ai
    // download, Claude_pzs8sxrjxfjjc) and "AnthropicPBC.Claude" (Microsoft
    // Store). Since 4.x both write %APPDATA%\Claude directly (the package
    // declares unvirtualizedResources) and it is no longer a reparse point;
    // older builds still need the Packages fallbacks. The legacy Squirrel
    // installer always used %APPDATA%\Claude.
    //
    // Since 4.16xxx (2026-10) the packaged app moves its data folder out of
    // the package, to %LOCALAPPDATA%\Claude-Data\<package family, lower
    // case> (e.g. claude_pzs8sxrjxfjjc). It renames the old folder there and
    // leaves only a 0-byte "lockfile" behind in %APPDATA%\Claude or the
    // package's LocalCache\Roaming\Claude, so a folder merely existing does
    // not mean it holds the data: the Claude Desktop rows only accept a
    // folder that contains one of DesktopDataMarkers, and fall back to the
    // first existing one when none does.
    private static readonly string[] DesktopDataMarkers =
    [
        "Local State",
        "config.json",
        "claude_desktop_config.json",
        "claude-code-sessions",
        "local-agent-mode-sessions",
        "Local Storage",
        "IndexedDB",
    ];

    private static readonly (string Key, string[] Candidates, string Source, bool IsFile, string[]? Markers)[] KnownPaths =
    [
        (
            "claudeDesktopAppData",
            new[]
            {
                @"%LOCALAPPDATA%\Claude-Data\claude_*",
                @"%LOCALAPPDATA%\Claude-Data\anthropicpbc.claude_*",
                @"%APPDATA%\Claude",
                @"%LOCALAPPDATA%\Packages\Claude_*\LocalCache\Roaming\Claude",
                @"%LOCALAPPDATA%\Packages\AnthropicPBC.Claude_*\LocalCache\Roaming\Claude",
            },
            "Claude Desktop (claude.ai, Store or legacy install; Claude-Data since 4.16xxx), verified 2026-11-05",
            false,
            DesktopDataMarkers
        ),
        // Claude Desktop in third-party inference mode (Bedrock / Vertex /
        // gateway) keeps its own profile next to the regular one. Current
        // builds keep it in %LOCALAPPDATA%\Claude-3p (moved there from
        // %APPDATA%\Claude-3p on first launch).
        (
            "claudeDesktop3pAppData",
            new[]
            {
                @"%LOCALAPPDATA%\Claude-3p",
                @"%APPDATA%\Claude-3p",
                @"%LOCALAPPDATA%\Packages\Claude_*\LocalCache\Local\Claude-3p",
                @"%LOCALAPPDATA%\Packages\AnthropicPBC.Claude_*\LocalCache\Local\Claude-3p",
                @"%LOCALAPPDATA%\Packages\Claude_*\LocalCache\Roaming\Claude-3p",
                @"%LOCALAPPDATA%\Packages\AnthropicPBC.Claude_*\LocalCache\Roaming\Claude-3p",
            },
            "Claude Desktop third-party inference profile, verified 2026-11-05",
            false,
            DesktopDataMarkers
        ),
        // CLAUDE_CONFIG_DIR moves both the .claude folder and .claude.json.
        (
            "claudeCodeUserProfile",
            new[] { @"%CLAUDE_CONFIG_DIR%", @"%USERPROFILE%\.claude" },
            "Spec 1.1 + verified 2026-04-22",
            false,
            null
        ),
        // Claude Code's global settings: user-scoped MCP servers and the
        // per-project settings (allowed tools, project MCP servers, trust).
        // It also holds account / machine ids, which restore never copies
        // over: see ClaudeGlobalConfigMerger.
        (
            "claudeCodeGlobalConfig",
            new[] { @"%CLAUDE_CONFIG_DIR%\.claude.json", @"%USERPROFILE%\.claude.json" },
            "Claude Code global config, verified 2026-10-29",
            true,
            null
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
                @"%LOCALAPPDATA%\Packages\Claude_*\LocalCache\Local\Claude",
                @"%LOCALAPPDATA%\Packages\AnthropicPBC.Claude_*\LocalCache\Local\Claude",
            },
            "Spec 1.1 + Store-app reparse fallback",
            false,
            null
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
            "Codex CLI/desktop state root, verified 2026-10-29",
            false,
            null
        ),
        // Codex desktop (Electron, Store-packaged as OpenAI.Codex_<publisherId>;
        // since 26.x the app is branded ChatGPT and runs as ChatGPT.exe).
        // %APPDATA%\Codex is virtualized into the package's LocalCache, so
        // outside the package only the Packages path exists. The app's live
        // profile is web\Codex below it (its --user-data-dir); the top-level
        // Chromium folders date from before 26.x. A non-packaged install
        // would write %APPDATA%\Codex directly.
        (
            "codexDesktopAppData",
            new[]
            {
                @"%APPDATA%\Codex",
                @"%LOCALAPPDATA%\Packages\OpenAI.Codex_*\LocalCache\Roaming\Codex",
            },
            "Codex desktop Store-app, verified 2026-10-29",
            false,
            null
        ),
        // Cross-agent skill store. `npx skills add` installs skills here and
        // links them into each agent's own folder (~\.claude\skills\<name>
        // -> ~\.agents\skills\<name>), and Codex reads ~\.agents\skills as
        // its user-level skills folder. Links are not followed during backup,
        // so without this source those skills would be lost on a new machine.
        (
            "agentsUserProfile",
            new[] { @"%USERPROFILE%\.agents" },
            "Shared agent skills store (npx skills, Codex user skills)",
            false,
            null
        ),
    ];

    public IReadOnlyList<DiscoveredClaudePath> Discover()
    {
        return KnownPaths
            .Select(kp => Resolve(kp.Key, kp.Candidates, kp.Source, kp.IsFile, kp.Markers))
            .ToList();
    }

    /// <summary>
    /// Pick the first candidate that exists. With <paramref name="markers"/>,
    /// prefer the first existing folder that contains one of them (the app's
    /// live data folder over a husk it left behind when it moved its data),
    /// then the first existing folder. When nothing exists, report the first
    /// candidate that resolved to a concrete path (no unmatched wildcard), so
    /// a restore onto a fresh machine writes where the app will look.
    /// </summary>
    internal static DiscoveredClaudePath Resolve(string key, IReadOnlyList<string> candidates, string source, bool isFile, IReadOnlyList<string>? markers)
    {
        string? first = null;
        string? firstExisting = null;
        foreach (var rel in candidates)
        {
            var expanded = ExpandCandidate(rel);
            if (expanded is null)
            {
                continue;
            }
            if (!expanded.Contains('*', StringComparison.Ordinal))
            {
                first ??= expanded;
            }
            if (!(isFile ? SafeFileExists(expanded) : SafeDirectoryExists(expanded)))
            {
                continue;
            }
            if (markers is null || markers.Count == 0 || HasAnyMarker(expanded, markers))
            {
                return new DiscoveredClaudePath(key, expanded, true, source);
            }
            firstExisting ??= expanded;
        }
        return firstExisting is not null
            ? new DiscoveredClaudePath(key, firstExisting, true, source)
            : new DiscoveredClaudePath(key, first ?? string.Empty, false, source);
    }

    private static bool HasAnyMarker(string folder, IReadOnlyList<string> markers)
    {
        foreach (var marker in markers)
        {
            var path = Path.Combine(folder, marker);
            try
            {
                if (File.Exists(path) || Directory.Exists(path))
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // An unreadable entry does not count as a marker.
            }
        }
        return false;
    }

    /// <summary>
    /// Expand environment variables and resolve a single "Name_*" wildcard
    /// segment (used for Store package folders). Returns null when the
    /// candidate references an unset environment variable, so optional
    /// overrides like %CODEX_HOME% are skipped instead of reported literally.
    /// </summary>
    internal static string? ExpandCandidate(string candidate)
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

    private static bool SafeFileExists(string path)
    {
        try
        {
            return File.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
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
