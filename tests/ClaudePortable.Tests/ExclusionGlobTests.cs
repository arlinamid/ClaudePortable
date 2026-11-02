using ClaudePortable.Core.Archive;

namespace ClaudePortable.Tests;

public class ExclusionGlobTests
{
    private static readonly ExclusionGlob Defaults = new(DefaultExclusions.Globs);

    [Theory]
    [InlineData("claude-desktop/appdata/Cache/000003.ldb", true)]
    [InlineData("claude-desktop/appdata/GPUCache/index", true)]
    [InlineData("claude-desktop/appdata/Code Cache/js/v8", true)]
    [InlineData("claude-desktop/appdata/Crashpad/reports/foo.dmp", true)]
    [InlineData("claude-desktop/appdata/Network/Cookies-journal", true)]
    [InlineData("claude-desktop/appdata/tokens.dat", true)]
    [InlineData("claude-desktop/appdata/Login Data", true)]
    [InlineData("claude-desktop/appdata/Login Data-journal", true)]
    [InlineData("claude-code/dotclaude/.remote-plugins/foo/bin.dll", true)]
    [InlineData("claude-code/dotclaude/plugins/my-plugin/skill.md", false)]
    [InlineData("claude-code/dotclaude/settings.json", false)]
    [InlineData("claude-code/dotclaude/CLAUDE.md", false)]
    [InlineData("claude-desktop/appdata/IndexedDB/data.leveldb/MANIFEST-000001", false)]
    [InlineData("claude-desktop/appdata/Preferences", false)]
    [InlineData("claude-desktop/appdata/config.json", true)]
    [InlineData("claude-desktop/appdata/claude_desktop_config.json", false)]
    [InlineData("claude-desktop/appdata/extensions-installations.json", false)]
    [InlineData("claude-desktop/appdata/local-agent-mode-sessions/abc/def/.claude/settings.json", false)]
    [InlineData("claude-desktop/appdata/local-agent-mode-sessions/abc/def/.claude/CLAUDE.md", false)]
    [InlineData("claude-desktop/appdata/local-agent-mode-sessions/abc/def/.claude/mcp-needs-auth-cache.json", true)]
    [InlineData("claude-code/dotclaude/mcp-needs-auth-cache.json", true)]
    [InlineData("claude-desktop/appdata/Claude Extensions/pdf-server/dist/index.js", false)]
    [InlineData("claude-desktop/appdata/DIPS-wal", true)]
    [InlineData("claude-desktop/appdata/IndexedDB/foo-wal", true)]
    [InlineData("claude-desktop/appdata/not-a-wal-but-walrus.json", false)]
    [InlineData("claude-desktop/appdata/IndexedDB/foo.sqlite-shm", true)]
    [InlineData("claude-code/dotclaude/.credentials.json", true)]
    [InlineData("agents/dotagents/skills/find-skills/SKILL.md", false)]
    [InlineData("agents/dotagents/.skill-lock.json", false)]
    // Codex state root: credentials, machine-bound sandbox state,
    // binaries, logs and caches are dropped; user config + history kept.
    [InlineData("codex/dotcodex/auth.json", true)]
    [InlineData("codex/dotcodex/installation_id", true)]
    [InlineData("codex/dotcodex/cap_sid", true)]
    [InlineData("codex/dotcodex/.sandbox-secrets/key", true)]
    [InlineData("codex/dotcodex/.sandbox-bin/codex.exe", true)]
    [InlineData("codex/dotcodex/packages/standalone/codex.exe", true)]
    [InlineData("codex/dotcodex/plugins/cache/foo/plugin.json", false)]
    [InlineData("codex/dotcodex/plugins/cache/local-playground/heartstrings/.codex-plugin/plugin.json", false)]
    [InlineData("codex/dotcodex/plugins/cache/openai-bundled/chrome/26.1/extension-host.exe", true)]
    [InlineData("codex/dotcodex/plugins/.remote-plugin-install-staging/x.zip", true)]
    [InlineData("codex/dotcodex/cache/codex_apps_tools/a.json", true)]
    [InlineData("codex/dotcodex/chrome-native-hosts-v2.json", true)]
    [InlineData("codex/dotcodex/project-chatgpt-provisioning/7a87.json", true)]
    [InlineData("codex/dotcodex/sqlite/codex-dev.db", false)]
    [InlineData("codex/dotcodex/automations/daily/automation.toml", false)]
    [InlineData("codex/dotcodex/.chatgpt-projects/g-p-1/AGENTS.md", false)]
    [InlineData("codex/dotcodex/hooks/continue_after_quota.py", false)]
    [InlineData("codex/dotcodex/mcp/pexels/server.py", false)]
    [InlineData("codex/dotcodex/mcp/pexels/.venv/Lib/site.py", true)]
    // Claude Code: installed plugins are kept (they used to fall under
    // the case-insensitive "**/Cache/**").
    [InlineData("claude-code/dotclaude/plugins/cache/anthropic-agent-skills/claude-api/ca45/SKILL.md", false)]
    [InlineData("claude-code/dotclaude/plugins/cache/mp/plugin/1.0/node_modules/x/index.js", false)]
    [InlineData("claude-code/dotclaude/plugins/installed_plugins.json", false)]
    [InlineData("claude-code/globalconfig/.claude.json", false)]
    // Claude Desktop 4.x: device identity, bundled binaries, VM image, logs.
    [InlineData("claude-desktop/appdata/ant-did", true)]
    [InlineData("claude-desktop/appdata/ant-device-registry.json", true)]
    [InlineData("claude-desktop/appdata/remote-control-state.json", true)]
    [InlineData("claude-desktop/appdata/ssh-remote-server-state.json", true)]
    [InlineData("claude-desktop/appdata/bridge-state.json", true)]
    [InlineData("claude-desktop/appdata/lockfile", true)]
    [InlineData("claude-desktop/appdata/claude-code/2.1.309/9078/claude.exe", true)]
    [InlineData("claude-desktop/appdata/claude-code-vm/2.1.309/claude", true)]
    [InlineData("claude-desktop/appdata/claude-code-sessions/a/b/local_1.json", false)]
    [InlineData("claude-desktop/appdata/vm_bundles/claudevm.bundle/rootfs.vhdx", true)]
    [InlineData("claude-desktop/appdata/vm_bundles/claudevm.bundle/.rootfs.vhdx.origin", true)]
    [InlineData("claude-desktop/appdata/vm_bundles/claudevm.bundle/vmlinuz", true)]
    [InlineData("claude-desktop/appdata/vm_bundles/warm/2a76/rootfs.vhdx", true)]
    [InlineData("claude-desktop/appdata/vm_bundles/claudevm.bundle/sessiondata.vhdx", false)]
    [InlineData("claude-desktop/appdata/logs/main.log", true)]
    [InlineData("claude-desktop/appdata/GrShaderCache/data_0", true)]
    [InlineData("claude-desktop/appdata/git-worktrees.json", false)]
    [InlineData("claude-desktop/appdata/ccd-session-presets.json", false)]
    [InlineData("claude-desktop/appdata/side-session-notes/local_1/notes.md", false)]
    [InlineData("claude-desktop/appdata/rpm/bb/89/plugin_01/manifest.json", false)]
    [InlineData("claude-desktop/appdata-3p/config.json", true)]
    [InlineData("claude-desktop/appdata-3p/claude_desktop_config.json", false)]
    [InlineData("codex/dotcodex/plugins/.plugin-appserver/codex.exe", true)]
    [InlineData("codex/dotcodex/logs_2.sqlite", true)]
    [InlineData("codex/dotcodex/sqlite/logs_2.sqlite-wal", true)]
    [InlineData("codex/dotcodex/log/codex-tui.log", true)]
    [InlineData("codex/dotcodex/sandbox.2026-06-30.log", true)]
    [InlineData("codex/dotcodex/mcp-oauth-locks/x", true)]
    [InlineData("codex/dotcodex/.sqlite-maintenance.lock", true)]
    [InlineData("codex/dotcodex/..codex-global-state.json.tmp-1785051217994-f9fe", true)]
    [InlineData("codex/dotcodex/models_cache.json", true)]
    [InlineData("codex/dotcodex/config.toml", false)]
    [InlineData("codex/dotcodex/AGENTS.md", false)]
    [InlineData("codex/dotcodex/hooks.json", false)]
    [InlineData("codex/dotcodex/sessions/2026/10/09/rollout-abc.jsonl", false)]
    [InlineData("codex/dotcodex/archived_sessions/rollout-abc.jsonl", false)]
    [InlineData("codex/dotcodex/skills/my-skill/SKILL.md", false)]
    [InlineData("codex/dotcodex/rules/default.rules", false)]
    [InlineData("codex/dotcodex/memories/note.md", false)]
    [InlineData("codex/dotcodex/state_5.sqlite", false)]
    [InlineData("codex/dotcodex/thread_history_1.sqlite", false)]
    [InlineData("codex/dotcodex/generated_images/img.png", false)]
    [InlineData("codex/dotcodex/.codex-global-state.json", false)]
    [InlineData("codex-desktop/appdata/web/Codex/Default/Cookies", true)]
    // Codex app 26.x profile (web/Codex): app state kept, browser dropped.
    [InlineData("codex-desktop/appdata/web/Codex/Local State", false)]
    [InlineData("codex-desktop/appdata/web/Codex/Default/Preferences", false)]
    [InlineData("codex-desktop/appdata/web/Codex/Default/Local Storage/leveldb/019693.ldb", false)]
    [InlineData("codex-desktop/appdata/web/Codex/Default/Local Storage/leveldb/LOCK", true)]
    [InlineData("codex-desktop/appdata/web/Codex/Default/IndexedDB/app_0.indexeddb.leveldb/000003.log", false)]
    [InlineData("codex-desktop/appdata/web/Codex/browser-sidebar-page-states.json", false)]
    [InlineData("codex-desktop/appdata/web/Codex/artifact-sessions/store/a.json", false)]
    [InlineData("codex-desktop/appdata/web/Codex/Default/Web Data", true)]
    [InlineData("codex-desktop/appdata/web/Codex/Default/History", true)]
    [InlineData("codex-desktop/appdata/web/Codex/Default/Cache/Cache_Data/f_1", true)]
    [InlineData("codex-desktop/appdata/web/Codex/codex-browser-app/Local Storage/leveldb/1.ldb", true)]
    [InlineData("codex-desktop/appdata/web/Codex/WidevineCdm/4.10/manifest.json", true)]
    [InlineData("codex-desktop/appdata/web/Codex/lockfile", true)]
    [InlineData("codex-desktop/appdata/Cache/Cache_Data/f_000001", true)]
    [InlineData("codex-desktop/appdata/sentry/scope_v3.json", true)]
    [InlineData("codex-desktop/appdata/Local Storage/leveldb/000003.log", false)]
    [InlineData("codex-desktop/appdata/Preferences", false)]
    // Codex-scoped globs must not leak into other sources.
    [InlineData("cowork-projects/a7b3/auth.json", false)]
    [InlineData("claude-code/dotclaude/packages/foo.txt", false)]
    public void IsExcluded_MatchesExpectedPolicy(string path, bool expected)
    {
        Assert.Equal(expected, Defaults.IsExcluded(path));
    }

    [Fact]
    public void ReInclude_LastMatchWins()
    {
        var glob = new ExclusionGlob(["a/**", "!a/keep/**", "**/LOCK"]);

        Assert.True(glob.IsExcluded("a/x.txt"));
        Assert.False(glob.IsExcluded("a/keep/x.txt"));
        Assert.True(glob.IsExcluded("a/keep/LOCK"));
        Assert.False(glob.IsExcluded("b/x.txt"));
    }

    [Theory]
    [InlineData("codex-desktop/appdata/web/", false)]
    [InlineData("codex-desktop/appdata/web/Codex/", false)]
    [InlineData("codex-desktop/appdata/web/Codex/Default/", false)]
    [InlineData("codex-desktop/appdata/web/Codex/Default/Local Storage/", false)]
    [InlineData("codex-desktop/appdata/web/Codex/Default/Cache/", true)]
    [InlineData("codex-desktop/appdata/web/Codex/WidevineCdm/", true)]
    [InlineData("codex-desktop/appdata/web/Codex/codex-browser-app/", true)]
    [InlineData("codex/dotcodex/plugins/cache/", false)]
    [InlineData("codex/dotcodex/plugins/cache/openai-bundled/", true)]
    [InlineData("claude-code/dotclaude/plugins/cache/", false)]
    [InlineData("claude-desktop/appdata/Cache/", true)]
    [InlineData("claude-desktop/appdata/claude-code/", true)]
    public void DirectoryPruning_RespectsReIncludes(string directory, bool pruned)
    {
        Assert.Equal(pruned, Defaults.IsDirectoryPruned(directory));
    }

    [Fact]
    public void IsExcluded_IsCaseInsensitive()
    {
        Assert.True(Defaults.IsExcluded("claude-desktop/appdata/CACHE/foo"));
        Assert.True(Defaults.IsExcluded("claude-desktop/appdata/cache/foo"));
    }

    [Fact]
    public void IsExcluded_HandlesBackslashSeparators()
    {
        Assert.True(Defaults.IsExcluded(@"claude-desktop\appdata\Cache\foo"));
    }
}
