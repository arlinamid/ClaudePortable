using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ClaudePortable.Core.Abstractions;
using ClaudePortable.Core.Archive;
using ClaudePortable.Core.Manifest;

namespace ClaudePortable.Core.Discovery;

/// <summary>
/// A folder outside the agents' own folders that their configuration needs:
/// an MCP server's code, a hook or status-line script, a local plugin
/// marketplace, or the target of a junction / symlink inside a backed-up
/// folder (skills linked to a checkout elsewhere).
/// </summary>
/// <param name="Path">Folder to back up, or the referenced path when <paramref name="SkipReason"/> is set.</param>
/// <param name="Groups">Source groups whose configuration refers to it (selection unit for restore).</param>
/// <param name="ReferencedBy">Where it was found, e.g. "claude_desktop_config.json: mcpServers.pollinations".</param>
/// <param name="SkipReason">Why it is not backed up (runtime, credential-like file, system folder, missing...); null = backed up.</param>
public sealed record ExternalReference(string Path, IReadOnlyList<string> Groups, IReadOnlyList<string> ReferencedBy, string? SkipReason);

/// <summary>
/// Finds the external folders an agent setup depends on by reading the
/// configuration the agents themselves use - no path is assumed:
/// <list type="bullet">
/// <item>Claude Desktop: <c>claude_desktop_config.json</c> → mcpServers;</item>
/// <item>Claude Code: <c>settings.json</c> / <c>settings.local.json</c> → hooks,
/// statusLine, mcpServers, extraKnownMarketplaces; <c>.claude.json</c> →
/// mcpServers (user and per project); <c>plugins/known_marketplaces.json</c>;</item>
/// <item>Codex: <c>config.toml</c> → [mcp_servers.*], [marketplaces.*],
/// notify; <c>hooks.json</c> → hooks;</item>
/// <item>links recorded while enumerating the sources.</item>
/// </list>
/// Workspace lists (trusted folders, Codex [projects], Claude Code projects)
/// are deliberately not followed: those are the user's projects, not part of
/// the agent setup. Runtimes (python.exe, node.exe, venvs, npm globals) are
/// reported but not backed up - they are reinstalled, not copied.
/// </summary>
public static class ExternalReferences
{
    /// <summary>Script types whose folder is the tool to back up (a referenced .exe is a runtime).</summary>
    private static readonly string[] ScriptExtensions =
        [".js", ".mjs", ".cjs", ".ts", ".py", ".ps1", ".psm1", ".cmd", ".bat", ".sh", ".rb", ".lua"];

    /// <summary>Build-output folder names: a script inside one belongs to the project above it.</summary>
    private static readonly string[] OutputFolderNames = ["dist", "build", "out", "lib", "bin", "src"];

    /// <summary>Path segments that mark regenerable or installed content (caches, venvs, package stores).</summary>
    private static readonly string[] RegenerableSegments =
        [".cache", ".tmp", "node_modules", ".venv", "venv", "site-packages", "__pycache__"];

    // An absolute Windows path: drive-letter or UNC, with either separator.
    // Quoted occurrences may contain spaces; bare ones end at whitespace.
    private static readonly Regex QuotedPath = new(@"[""'](?<p>(?:[A-Za-z]:[\\/]|\\\\)[^""'\r\n]+)[""']", RegexOptions.Compiled);
    private static readonly Regex BarePath = new(@"(?<![\w""'])(?<p>(?:[A-Za-z]:[\\/]|\\\\)[^\s""';,]+)", RegexOptions.Compiled);

    private sealed class Candidate
    {
        public HashSet<string> Groups { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> ReferencedBy { get; } = [];
    }

    /// <summary>
    /// <paramref name="sources"/>: the discovered (and selected) agent sources;
    /// their configuration is read and anything inside them is already
    /// covered. <paramref name="coveredRoots"/>: further folders already in
    /// the backup (Cowork projects).
    /// </summary>
    public static IReadOnlyList<ExternalReference> Discover(
        IReadOnlyList<DiscoveredClaudePath> sources,
        IReadOnlyList<BackupLink> links,
        IReadOnlyList<string> coveredRoots)
    {
        var raw = new Dictionary<string, Candidate>(StringComparer.OrdinalIgnoreCase);
        void Add(string path, string group, string referencedBy)
        {
            if (!raw.TryGetValue(path, out var c))
            {
                raw[path] = c = new Candidate();
            }
            c.Groups.Add(group);
            if (!c.ReferencedBy.Contains(referencedBy, StringComparer.Ordinal))
            {
                c.ReferencedBy.Add(referencedBy);
            }
        }

        foreach (var source in sources.Where(s => s.Exists))
        {
            foreach (var (path, from) in ReferencesIn(source))
            {
                foreach (var group in SourceGroups.ForSourceKey(source.Key))
                {
                    Add(path, group, from);
                }
            }
        }

        // Junctions and directory symlinks are recorded, not followed, while
        // the sources are enumerated. A link whose target lies outside every
        // backed-up folder would restore as a dangling link, so its target is
        // backed up too (one level: links inside that target are recorded but
        // not chased further).
        foreach (var link in links)
        {
            var source = sources.FirstOrDefault(s => s.Exists
                && link.Path.StartsWith(SourceLayout.PrefixFor(s.Key).TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase));
            string target;
            if (System.IO.Path.IsPathFullyQualified(link.Target))
            {
                target = link.Target;
            }
            else if (source is not null && ResolveLinkLocation(link.Path, source) is { } linkLocation)
            {
                target = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(linkLocation)!, link.Target));
            }
            else
            {
                continue;
            }
            IReadOnlyList<string> groups = source is not null
                ? SourceGroups.ForSourceKey(source.Key)
                : SourceGroups.ForArchivePrefix(link.Path.Split('/') is { Length: > 1 } parts ? $"{parts[0]}/{parts[1]}" : link.Path);
            foreach (var group in groups)
            {
                Add(target, group, $"link {link.Path}");
            }
        }

        var covered = sources.Where(s => s.Exists).Select(s => s.Path).Concat(coveredRoots).ToList();
        return Resolve(raw, covered);
    }

    /// <summary>The absolute location of a recorded link, from its archive path and the source it lives in.</summary>
    private static string? ResolveLinkLocation(string archivePath, DiscoveredClaudePath source)
    {
        var prefix = SourceLayout.PrefixFor(source.Key).TrimEnd('/') + "/";
        return archivePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? System.IO.Path.Combine(source.Path, archivePath[prefix.Length..].Replace('/', System.IO.Path.DirectorySeparatorChar))
            : null;
    }

    /// <summary>(path, where) pairs found in the configuration files of one source.</summary>
    internal static IEnumerable<(string Path, string From)> ReferencesIn(DiscoveredClaudePath source)
    {
        switch (source.Key)
        {
            case "claudeDesktopAppData":
            case "claudeDesktop3pAppData":
                return FromJson(System.IO.Path.Combine(source.Path, "claude_desktop_config.json"), ["mcpServers"]);
            case "claudeCodeUserProfile":
                return
                [
                    .. FromJson(System.IO.Path.Combine(source.Path, "settings.json"), ["hooks", "statusLine", "mcpServers", "extraKnownMarketplaces"]),
                    .. FromJson(System.IO.Path.Combine(source.Path, "settings.local.json"), ["hooks", "statusLine", "mcpServers", "extraKnownMarketplaces"]),
                    .. FromJson(System.IO.Path.Combine(source.Path, "plugins", "known_marketplaces.json"), ["*"]),
                ];
            case "claudeCodeGlobalConfig":
                return FromJson(source.Path, ["mcpServers", "projects.*.mcpServers"]);
            case "codexUserProfile":
                return
                [
                    .. FromToml(System.IO.Path.Combine(source.Path, "config.toml"), ["mcp_servers", "marketplaces", "notify"]),
                    .. FromJson(System.IO.Path.Combine(source.Path, "hooks.json"), ["hooks"]),
                ];
            default:
                return [];
        }
    }

    /// <summary>
    /// Paths in the given sections of a JSON file. A section is a top-level
    /// key, "*" for the whole file, or "a.*.b" for a key under every entry of a.
    /// </summary>
    internal static List<(string Path, string From)> FromJson(string file, string[] sections)
    {
        var result = new List<(string, string)>();
        JsonNode? root;
        try
        {
            if (!File.Exists(file))
            {
                return result;
            }
            root = JsonNode.Parse(File.ReadAllText(file), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return result;
        }
        if (root is not JsonObject obj)
        {
            return result;
        }

        var name = System.IO.Path.GetFileName(file);
        foreach (var section in sections)
        {
            foreach (var (node, label) in Select(obj, section))
            {
                Collect(node, label, s =>
                {
                    foreach (var p in ExtractPaths(s))
                    {
                        result.Add((p, $"{name}: {label}"));
                    }
                });
            }
        }
        return result;
    }

    private static IEnumerable<(JsonNode Node, string Label)> Select(JsonObject root, string section)
    {
        if (section == "*")
        {
            yield return (root, "*");
            yield break;
        }
        var parts = section.Split('.');
        IEnumerable<(JsonNode Node, string Label)> current = [(root, string.Empty)];
        foreach (var part in parts)
        {
            current = current.SelectMany(c => c.Node is JsonObject o
                ? part == "*"
                    ? o.Where(kv => kv.Value is not null).Select(kv => (kv.Value!, c.Label + "*."))
                    : o[part] is { } child ? [(child, c.Label + part + ".")] : []
                : []).ToList();
        }
        foreach (var (node, label) in current)
        {
            yield return (node, label.TrimEnd('.'));
        }
    }

    private static void Collect(JsonNode? node, string label, Action<string> visit)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var (_, v) in o)
                {
                    Collect(v, label, visit);
                }
                break;
            case JsonArray a:
                foreach (var v in a)
                {
                    Collect(v, label, visit);
                }
                break;
            case JsonValue v when v.TryGetValue<string>(out var s):
                visit(s);
                break;
        }
    }

    /// <summary>
    /// Paths in the given tables of a TOML file ("mcp_servers" matches
    /// [mcp_servers.x] and [mcp_servers.x.env]; a top-level key such as
    /// "notify" matches that key before the first table). A line-level
    /// reader is enough: only string values are of interest.
    /// </summary>
    internal static List<(string Path, string From)> FromToml(string file, string[] sections)
    {
        var result = new List<(string, string)>();
        string[] lines;
        try
        {
            if (!File.Exists(file))
            {
                return result;
            }
            lines = File.ReadAllLines(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return result;
        }

        var name = System.IO.Path.GetFileName(file);
        var table = string.Empty;
        var tableHeader = new Regex(@"^\s*\[\[?\s*(?<t>[^\]]+?)\s*\]\]?\s*(#.*)?$");
        foreach (var line in lines)
        {
            var header = tableHeader.Match(line);
            if (header.Success)
            {
                table = header.Groups["t"].Value;
                continue;
            }
            var eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                // Continuation line of a multi-line array: same table / key.
                if (table.Length > 0 && Matches(table, sections))
                {
                    AddToml(result, name, table, line);
                }
                continue;
            }
            var key = line[..eq].Trim();
            var scope = table.Length == 0 ? key : table;
            if (Matches(scope, sections))
            {
                AddToml(result, name, table.Length == 0 ? key : $"{table}.{key}", line[(eq + 1)..]);
            }
        }
        return result;
    }

    private static bool Matches(string tableOrKey, string[] sections)
        => sections.Any(s => tableOrKey.Equals(s, StringComparison.OrdinalIgnoreCase)
            || tableOrKey.StartsWith(s + ".", StringComparison.OrdinalIgnoreCase));

    private static void AddToml(List<(string, string)> result, string file, string label, string value)
    {
        // TOML basic strings escape backslashes ("C:\\Users\\..."); literal
        // strings ('C:\Users\...') do not.
        foreach (var p in ExtractPaths(value.Replace(@"\\", @"\", StringComparison.Ordinal)))
        {
            result.Add((p, $"{file}: {label}"));
        }
    }

    /// <summary>Absolute paths in a config value: a plain path, a command line, or a ';'-separated list.</summary>
    internal static IEnumerable<string> ExtractPaths(string value)
    {
        var found = new List<string>();
        foreach (Match m in QuotedPath.Matches(value))
        {
            found.Add(m.Groups["p"].Value);
        }
        var unquoted = QuotedPath.Replace(value, " ");
        var trimmed = unquoted.Trim();
        if (found.Count == 0 && LooksLikeSinglePath(trimmed))
        {
            found.AddRange(trimmed.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }
        else
        {
            foreach (Match m in BarePath.Matches(unquoted))
            {
                found.Add(m.Groups["p"].Value);
            }
        }
        return found
            .SelectMany(p => p.Split(';', StringSplitOptions.RemoveEmptyEntries))
            .Select(p => p.Trim().TrimEnd('\\', '/').Replace('/', '\\'))
            .Where(p => p.Length > 3)
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A whole value that is one existing path (spaces allowed) or a ';' list
    /// of them. Anything else is read as a command line and split at spaces.
    /// </summary>
    private static bool LooksLikeSinglePath(string value)
    {
        var parts = value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length > 0 && parts.All(part =>
        {
            try
            {
                return part.Length > 3
                    && System.IO.Path.IsPathFullyQualified(part)
                    && !part.StartsWith(@"\\", StringComparison.Ordinal)
                    && (File.Exists(part) || Directory.Exists(part));
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                return false;
            }
        });
    }

    /// <summary>Turn raw references into folders to back up (or skip reasons), merging nested ones.</summary>
    private static List<ExternalReference> Resolve(Dictionary<string, Candidate> raw, IReadOnlyList<string> covered)
    {
        var accepted = new Dictionary<string, Candidate>(StringComparer.OrdinalIgnoreCase);
        var result = new List<ExternalReference>();
        foreach (var (path, candidate) in raw)
        {
            var (folder, reason) = Classify(path, covered);
            if (reason is not null)
            {
                // Inside a backed-up folder: nothing to report.
                if (reason != CoveredReason)
                {
                    result.Add(new ExternalReference(path, Sorted(candidate.Groups), candidate.ReferencedBy, reason));
                }
                continue;
            }
            if (!accepted.TryGetValue(folder!, out var merged))
            {
                accepted[folder!] = merged = new Candidate();
            }
            merged.Groups.UnionWith(candidate.Groups);
            merged.ReferencedBy.AddRange(candidate.ReferencedBy.Where(r => !merged.ReferencedBy.Contains(r, StringComparer.Ordinal)));
        }

        // A folder inside another accepted folder is covered by it.
        var folders = accepted.Keys.OrderBy(k => k.Length).ToList();
        foreach (var folder in folders)
        {
            var parent = folders.FirstOrDefault(f => f.Length < folder.Length && IsUnder(folder, f));
            if (parent is null)
            {
                continue;
            }
            accepted[parent].Groups.UnionWith(accepted[folder].Groups);
            accepted[parent].ReferencedBy.AddRange(accepted[folder].ReferencedBy.Where(r => !accepted[parent].ReferencedBy.Contains(r, StringComparer.Ordinal)));
            accepted.Remove(folder);
        }

        result.AddRange(accepted.Select(kv => new ExternalReference(kv.Key, Sorted(kv.Value.Groups), kv.Value.ReferencedBy, null)));
        return result.OrderBy(r => r.SkipReason is not null).ThenBy(r => r.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private const string CoveredReason = "already in the backup";

    /// <summary>The folder to back up for one referenced path, or why not.</summary>
    internal static (string? Folder, string? SkipReason) Classify(string path, IReadOnlyList<string> covered)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return (null, "network or device path");
        }
        string full;
        try
        {
            full = System.IO.Path.GetFullPath(path).TrimEnd('\\');
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return (null, "not a valid path");
        }
        // A command written without its extension ("...\bin\sentrux") is
        // resolved the way Windows does it.
        if (!File.Exists(full) && !Directory.Exists(full) && string.IsNullOrEmpty(System.IO.Path.GetExtension(full)))
        {
            var withExtension = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT")
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(ext => full + ext.ToLowerInvariant())
                .FirstOrDefault(File.Exists);
            full = withExtension ?? full;
        }
        if (covered.Any(c => IsUnder(full, c) || string.Equals(full, c.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
        {
            return (null, CoveredReason);
        }
        if (SystemRoots().Any(r => IsUnder(full, r) || string.Equals(full, r, StringComparison.OrdinalIgnoreCase)))
        {
            return (null, "installed software / app data (reinstall it on the new PC)");
        }
        var segments = full.Split('\\');
        if (segments.Any(s => RegenerableSegments.Contains(s, StringComparer.OrdinalIgnoreCase)))
        {
            return (null, "cache, virtual environment or package store (recreated by reinstalling)");
        }

        string folder;
        if (Directory.Exists(full))
        {
            folder = full;
        }
        else if (File.Exists(full))
        {
            var ext = System.IO.Path.GetExtension(full);
            if (!ScriptExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
            {
                return (null, string.Equals(ext, ".exe", StringComparison.OrdinalIgnoreCase)
                    ? "program / runtime (reinstall it on the new PC)"
                    : "single data file (may hold credentials; copy it yourself if needed)");
            }
            folder = System.IO.Path.GetDirectoryName(full)!;
            if (OutputFolderNames.Contains(System.IO.Path.GetFileName(folder), StringComparer.OrdinalIgnoreCase)
                && System.IO.Path.GetDirectoryName(folder) is { } project
                && System.IO.Path.GetPathRoot(project) != project)
            {
                folder = project;
            }
        }
        else
        {
            return (null, "does not exist on this PC");
        }

        if (string.Equals(System.IO.Path.GetPathRoot(folder)?.TrimEnd('\\'), folder, StringComparison.OrdinalIgnoreCase)
            || ProfileLevelFolders().Any(r => string.Equals(folder, r, StringComparison.OrdinalIgnoreCase)))
        {
            return (null, "a drive or profile root (too broad to back up)");
        }
        if (covered.Any(c => IsUnder(folder, c)))
        {
            return (null, CoveredReason);
        }
        return (folder, null);
    }

    /// <summary>Installed software and per-app data: Windows, Program Files, ProgramData, AppData.</summary>
    private static IEnumerable<string> SystemRoots() => new[]
    {
        Environment.SpecialFolder.Windows,
        Environment.SpecialFolder.ProgramFiles,
        Environment.SpecialFolder.ProgramFilesX86,
        Environment.SpecialFolder.CommonApplicationData,
        Environment.SpecialFolder.ApplicationData,
        Environment.SpecialFolder.LocalApplicationData,
    }.Select(Environment.GetFolderPath).Where(p => !string.IsNullOrEmpty(p)).Select(p => p.TrimEnd('\\'));

    /// <summary>Folders too broad to take whole: the profile and its well-known top-level folders.</summary>
    private static IEnumerable<string> ProfileLevelFolders() => new[]
    {
        Environment.SpecialFolder.UserProfile,
        Environment.SpecialFolder.MyDocuments,
        Environment.SpecialFolder.Desktop,
        Environment.SpecialFolder.MyPictures,
        Environment.SpecialFolder.MyMusic,
        Environment.SpecialFolder.MyVideos,
    }.Select(Environment.GetFolderPath).Where(p => !string.IsNullOrEmpty(p)).Select(p => p.TrimEnd('\\'));

    private static bool IsUnder(string path, string root)
        => path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    private static List<string> Sorted(IEnumerable<string> groups)
        => SourceGroups.All.Where(g => groups.Contains(g, StringComparer.OrdinalIgnoreCase)).ToList();
}
