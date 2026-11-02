using System.IO.Compression;
using System.Text.Json.Nodes;
using ClaudePortable.Core.Abstractions;
using ClaudePortable.Core.Archive;
using ClaudePortable.Core.Backup;
using ClaudePortable.Core.Discovery;
using ClaudePortable.Core.Manifest;
using ClaudePortable.Core.Restore;
using Microsoft.Data.Sqlite;

namespace ClaudePortable.Tests;

/// <summary>
/// External folders referenced from agent settings and links, the
/// .claude.json merge, and SQLite snapshots. Everything runs on folders
/// created under a temp root; no path of the test machine is assumed.
/// </summary>
public class ExternalDependencyTests : IDisposable
{
    // Not under %TEMP%: that lives in %LOCALAPPDATA%, which the classifier
    // treats as installed software. The test output folder is a neutral place.
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, $"cp-external-{Guid.NewGuid():N}");

    public ExternalDependencyTests()
    {
        Directory.CreateDirectory(_root);
    }

    private string Dir(params string[] parts)
    {
        var path = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(path);
        return path;
    }

    private string FileAt(string content, params string[] parts)
    {
        var path = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static string Json(string s) => s.Replace(@"\", @"\\", StringComparison.Ordinal);

    [Fact]
    public void ExtractPaths_ReadsCommandLinesListsAndPlainValues()
    {
        var script = FileAt("print()", "tools", "my hook", "hook.py");

        Assert.Equal([script], ExternalReferences.ExtractPaths(script));
        Assert.Equal(
            new[] { @"C:\Python311\python.exe", script }.Order(StringComparer.Ordinal),
            ExternalReferences.ExtractPaths($"C:/Python311/python.exe \"{script}\" --event stop").Order(StringComparer.Ordinal));
        Assert.Equal(
            [@"C:\a\one", @"D:\b\two"],
            ExternalReferences.ExtractPaths(@"C:\a\one;D:\b\two"));
        Assert.Empty(ExternalReferences.ExtractPaths("npx -y @modelcontextprotocol/server-filesystem"));
        Assert.Empty(ExternalReferences.ExtractPaths("https://example.com/mcp"));
    }

    [Fact]
    public void FromToml_ReadsMcpServersMarketplacesAndNotify_NotProjects()
    {
        var toml = FileAt(
            """
            notify = ["C:\\Tools\\notify.exe", "--x"]
            model = "gpt-5"

            [mcp_servers.chrome]
            command = "node"
            args = [
              "D:/srv/chrome/dist/index.js",
            ]

            [mcp_servers.chrome.env]
            PYTHONPATH = 'D:\srv\py\src'

            [marketplaces.local]
            source = "D:\\market\\local"

            [projects.'D:\work\repo']
            trust_level = "trusted"

            [hooks.state.'C:\Users\u\.codex\hooks.json:stop:0:0']
            enabled = true
            """,
            "codex", "config.toml");

        var found = ExternalReferences.FromToml(toml, ["mcp_servers", "marketplaces", "notify"]).Select(r => r.Path).ToList();

        Assert.Contains(@"C:\Tools\notify.exe", found);
        Assert.Contains(@"D:\srv\chrome\dist\index.js", found);
        Assert.Contains(@"D:\srv\py\src", found);
        Assert.Contains(@"D:\market\local", found);
        Assert.DoesNotContain(found, p => p.StartsWith(@"D:\work", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(found, p => p.Contains("hooks.json", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Classify_ScriptFolderRuntimeAndDataFile()
    {
        var server = Dir("srv", "mcp-server");
        var entry = FileAt("//", "srv", "mcp-server", "dist", "index.js");
        var hook = FileAt("print()", "hooks", "pet", "hook.py");
        var exe = FileAt("MZ", "bin", "tool.exe");
        var cookies = FileAt("secret", "dl", "cookies.txt");
        var venvPython = FileAt("MZ", "srv", "py", ".venv", "Scripts", "python.exe");

        Assert.Equal((server, (string?)null), ExternalReferences.Classify(entry, []));
        Assert.Equal((Path.GetDirectoryName(hook), (string?)null), ExternalReferences.Classify(hook, []));
        Assert.Contains("runtime", ExternalReferences.Classify(exe, []).SkipReason, StringComparison.Ordinal);
        Assert.Contains("data file", ExternalReferences.Classify(cookies, []).SkipReason, StringComparison.Ordinal);
        Assert.Contains("virtual environment", ExternalReferences.Classify(venvPython, []).SkipReason, StringComparison.Ordinal);
        Assert.Contains("does not exist", ExternalReferences.Classify(Path.Combine(_root, "gone", "x.js"), []).SkipReason, StringComparison.Ordinal);
        Assert.Equal("already in the backup", ExternalReferences.Classify(hook, [Path.Combine(_root, "hooks")]).SkipReason);
    }

    [Fact]
    public void Discover_FollowsSettingsAndLinks_MergesNestedFolders()
    {
        // Agent folders (stand-ins for %APPDATA%\Claude, ~\.claude, %CODEX_HOME%).
        var desktop = Dir("agents", "ClaudeDesktop");
        var dotClaude = Dir("agents", "dotclaude");
        var codex = Dir("agents", "codex-home");

        // External things they use.
        var mcp = FileAt("//", "ext", "pollinations", "dist", "index.js");
        var hook = FileAt("print()", "ext", "clawd-pet", "clawd_hook.py");
        var marketplace = Dir("ext", "marketplace");
        var marketplaceSub = Dir("ext", "marketplace", "integrations", "codex");
        var linkedSkills = Dir("ext", "skills-checkout", "my-skill");
        var runtime = FileAt("MZ", "runtime", "python.exe");

        File.WriteAllText(Path.Combine(desktop, "claude_desktop_config.json"), Json($$"""
            { "mcpServers": { "pollinations": { "command": "node", "args": ["{{mcp.Replace('\\', '/')}}"] } },
              "preferences": { "localAgentModeTrustedFolders": ["{{Dir("work", "trusted-project")}}"] } }
            """));
        File.WriteAllText(Path.Combine(dotClaude, "settings.json"), new JsonObject
        {
            ["hooks"] = new JsonObject
            {
                ["Stop"] = new JsonArray(new JsonObject
                {
                    ["hooks"] = new JsonArray(new JsonObject { ["type"] = "command", ["command"] = $"\"{runtime}\" \"{hook}\" stop" }),
                }),
            },
            ["permissions"] = new JsonObject { ["additionalDirectories"] = new JsonArray(Dir("work", "other")) },
        }.ToJsonString());
        File.WriteAllText(Path.Combine(codex, "config.toml"), $"""
            [marketplaces.local]
            source = '{marketplace}'

            [marketplaces.local-codex]
            source = '{marketplaceSub}'

            [projects.'{Dir("work", "codex-project")}']
            trust_level = "trusted"
            """);

        DiscoveredClaudePath[] sources =
        [
            new("claudeDesktopAppData", desktop, true, "test"),
            new("claudeCodeUserProfile", dotClaude, true, "test"),
            new("codexUserProfile", codex, true, "test"),
        ];
        BackupLink[] links = [new("claude-code/dotclaude/skills/my-skill", linkedSkills)];

        var refs = ExternalReferences.Discover(sources, links, []);
        var accepted = refs.Where(r => r.SkipReason is null).ToDictionary(r => r.Path, StringComparer.OrdinalIgnoreCase);

        Assert.True(accepted.ContainsKey(Path.Combine(_root, "ext", "pollinations")));
        Assert.Equal([SourceGroups.ClaudeDesktop], accepted[Path.Combine(_root, "ext", "pollinations")].Groups);
        Assert.True(accepted.ContainsKey(Path.Combine(_root, "ext", "clawd-pet")));
        Assert.True(accepted.ContainsKey(marketplace));
        Assert.False(accepted.ContainsKey(marketplaceSub)); // inside the marketplace folder
        Assert.Equal(2, accepted[marketplace].ReferencedBy.Count);
        Assert.True(accepted.ContainsKey(linkedSkills));
        Assert.Equal([SourceGroups.ClaudeCode], accepted[linkedSkills].Groups);
        Assert.Contains(refs, r => r.Path == runtime && r.SkipReason is not null);
        // Workspace lists are not dependencies.
        Assert.DoesNotContain(refs, r => r.Path.Contains(Path.Combine(_root, "work"), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Discover_RelativeLinkIntoAnotherSource_IsCovered()
    {
        var dotClaude = Dir("p", ".claude");
        var agents = Dir("p", ".agents");
        Dir("p", ".agents", "skills", "hf-cli");
        DiscoveredClaudePath[] sources =
        [
            new("claudeCodeUserProfile", dotClaude, true, "test"),
            new("agentsUserProfile", agents, true, "test"),
        ];

        var refs = ExternalReferences.Discover(sources, [new("claude-code/dotclaude/skills/hf-cli", @"..\..\.agents\skills\hf-cli")], []);

        Assert.Empty(refs);
    }

    [Fact]
    public async Task Backup_ArchivesReferencedFolderAndGlobalConfig()
    {
        var dotClaude = Dir("home", ".claude");
        var tool = Dir("ext", "mcp-tool");
        File.WriteAllText(Path.Combine(tool, "server.py"), "print('mcp')");
        Dir("ext", "mcp-tool", "node_modules", "dep");
        File.WriteAllText(Path.Combine(tool, "node_modules", "dep", "index.js"), "//");
        var globalConfig = FileAt(Json($$"""{ "mcpServers": { "tool": { "command": "python", "args": ["{{Path.Combine(tool, "server.py")}}"] } } }"""), "home", ".claude.json");

        var discovery = new FakeDiscovery(
            new("claudeCodeUserProfile", dotClaude, true, "test"),
            new("claudeCodeGlobalConfig", globalConfig, true, "test"));
        var engine = new BackupEngine(discovery, new ZipArchiveWriter(TextWriter.Null), NullCoworkProjectDiscovery.Instance);
        var outcome = await engine.CreateBackupAsync(new(Dir("backups"), RetentionTier.Daily));

        var external = Assert.Single(outcome.Manifest.ExternalPaths, e => e.ArchivePrefix is not null);
        Assert.Equal(tool, external.Path);
        Assert.Equal([SourceGroups.ClaudeCode], external.Groups);
        using var zip = ZipFile.OpenRead(outcome.ZipPath);
        Assert.NotNull(zip.GetEntry("claude-code/globalconfig/.claude.json"));
        Assert.NotNull(zip.GetEntry($"{external.ArchivePrefix}/server.py"));
        Assert.Null(zip.GetEntry($"{external.ArchivePrefix}/node_modules/dep/index.js"));
        // A dependency is not content: the backup still only "contains" Claude Code.
        Assert.Equal([SourceGroups.ClaudeCode], SourceGroups.ContentsOf(outcome.Manifest));
        Assert.Equal([SourceGroups.ClaudeCode], SourceGroups.ForArchivePrefix(external.ArchivePrefix!, outcome.Manifest));
    }

    [Fact]
    public void Enumerate_KeepsInstalledPlugins_DropsAppBundledOnes()
    {
        var codex = Dir("codex-home");
        FileAt("{}", "codex-home", "plugins", "cache", "my-market", "my-plugin", ".codex-plugin", "plugin.json");
        FileAt("MZ", "codex-home", "plugins", "cache", "openai-bundled", "chrome", "host.exe");
        FileAt("{}", "codex-home", "cache", "codex_apps_tools", "a.json");
        var dotClaude = Dir("dotclaude");
        FileAt("# skill", "dotclaude", "plugins", "cache", "mp", "plug", "1.0", "skills", "x", "SKILL.md");

        var exclusions = new ExclusionGlob(DefaultExclusions.Globs);
        var codexEntries = FileEnumerator.Enumerate(codex, "codex/dotcodex", exclusions).Select(e => e.RelativePath).ToList();
        var claudeEntries = FileEnumerator.Enumerate(dotClaude, "claude-code/dotclaude", exclusions).Select(e => e.RelativePath).ToList();

        Assert.Equal(["codex/dotcodex/plugins/cache/my-market/my-plugin/.codex-plugin/plugin.json"], codexEntries);
        Assert.Equal(["claude-code/dotclaude/plugins/cache/mp/plug/1.0/skills/x/SKILL.md"], claudeEntries);
    }

    [Fact]
    public void Enumerate_PrefixScopedGlobsDoNotLeakIntoProjects()
    {
        // A project that happens to contain a folder layout like an agent's.
        var project = Dir("project");
        FileAt("{}", "project", "codex", "dotcodex", "auth.json");

        var entries = FileEnumerator.Enumerate(project, "cowork-projects/abc", new ExclusionGlob(DefaultExclusions.Globs)).ToList();

        Assert.Single(entries);
    }

    [Fact]
    public void GlobalConfigMerge_KeepsThisMachinesIdentity()
    {
        var backup = """
            {
              "userID": "old-user", "machineID": "old-machine", "oauthAccount": { "emailAddress": "a@b" },
              "cachedGrowthBookFeatures": { "x": 1 }, "modelAccessCache": {},
              "hasCompletedOnboarding": true, "theme": "dark",
              "mcpServers": { "cam": { "command": "node" } },
              "projects": { "D:/tool/a": { "allowedTools": ["Bash"] }, "D:/tool/A": { "allowedTools": [] } }
            }
            """;
        var existing = """
            {
              "userID": "new-user", "machineID": "new-machine", "numStartups": 3,
              "mcpServers": { "local": { "command": "x" }, "cam": { "command": "old" } },
              "projects": { "C:/new": { "allowedTools": [] } }
            }
            """;

        var merged = JsonNode.Parse(ClaudeGlobalConfigMerger.Merge(backup, existing))!.AsObject();

        Assert.Equal("new-user", (string?)merged["userID"]);
        Assert.Equal("new-machine", (string?)merged["machineID"]);
        Assert.Equal(3, (int?)merged["numStartups"]);
        Assert.False(merged.ContainsKey("oauthAccount"));
        Assert.False(merged.ContainsKey("cachedGrowthBookFeatures"));
        Assert.False(merged.ContainsKey("modelAccessCache"));
        Assert.True((bool?)merged["hasCompletedOnboarding"]);
        Assert.Equal("dark", (string?)merged["theme"]);
        Assert.Equal("node", (string?)merged["mcpServers"]!["cam"]!["command"]);
        Assert.Equal("x", (string?)merged["mcpServers"]!["local"]!["command"]);
        var projects = merged["projects"]!.AsObject();
        Assert.True(projects.ContainsKey("C:/new"));
        Assert.True(projects.ContainsKey("D:/tool/a"));
        Assert.True(projects.ContainsKey("D:/tool/A"));
    }

    [Fact]
    public void GlobalConfigMerge_FreshMachine_DropsIdentity()
    {
        var merged = JsonNode.Parse(ClaudeGlobalConfigMerger.Merge("""{ "userID": "u", "anonymousId": "a", "mcpServers": {} }""", null))!.AsObject();

        Assert.False(merged.ContainsKey("userID"));
        Assert.False(merged.ContainsKey("anonymousId"));
        Assert.True(merged.ContainsKey("mcpServers"));
    }

    [Fact]
    public void SqliteSnapshot_IncludesTransactionsStillInTheWal()
    {
        var db = Path.Combine(Dir("live"), "state_5.sqlite");
        var cs = new SqliteConnectionStringBuilder { DataSource = db, Pooling = false }.ToString();
        using var writer = new SqliteConnection(cs);
        writer.Open();
        Exec(writer, "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; CREATE TABLE threads(id TEXT);");
        Exec(writer, "INSERT INTO threads VALUES ('newest-thread');");
        Assert.True(new FileInfo(db + "-wal").Length > 0);

        var snapshot = Path.Combine(Dir("snap"), "copy.sqlite");
        Assert.True(SqliteSnapshot.IsCandidate(db));
        Assert.Null(SqliteSnapshot.TrySnapshot(db, snapshot));

        using var reader = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = snapshot, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        reader.Open();
        using var cmd = reader.CreateCommand();
        cmd.CommandText = "SELECT id FROM threads";
        Assert.Equal("newest-thread", cmd.ExecuteScalar());
    }

    [Fact]
    public void SqliteSnapshot_NotADatabase_IsNotACandidate()
    {
        Assert.False(SqliteSnapshot.IsCandidate(FileAt("not sqlite", "x", "fake.db")));
        Assert.False(SqliteSnapshot.IsCandidate(FileAt("{}", "x", "settings.json")));
    }

    private static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        GC.SuppressFinalize(this);
    }

    private sealed class FakeDiscovery(params DiscoveredClaudePath[] paths) : IPathDiscovery
    {
        public IReadOnlyList<DiscoveredClaudePath> Discover() => paths;
    }
}
