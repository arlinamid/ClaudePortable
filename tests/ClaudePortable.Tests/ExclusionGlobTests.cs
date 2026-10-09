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
    [InlineData("codex/dotcodex/plugins/cache/foo/plugin.json", true)]
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
