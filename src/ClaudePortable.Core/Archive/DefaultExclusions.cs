namespace ClaudePortable.Core.Archive;

public static class DefaultExclusions
{
    public static IReadOnlyList<string> Globs { get; } = new[]
    {
        "**/Cache/**",
        "**/GPUCache/**",
        "**/DawnGraphiteCache/**",
        "**/DawnWebGPUCache/**",
        "**/Code Cache/**",
        "**/Extensions Update Cache/**",
        "**/Crashpad/**",
        "**/VideoDecodeStats/**",
        "**/Partitions/**",
        "**/Network/**",
        "**/*.ldb.tmp",
        "**/*-journal",
        "**/*-wal",
        "**/tokens.dat",
        "**/Login Data*",
        "**/Cookies*",
        "**/.remote-plugins/**",
        "**/LOCK",
        "**/debug/latest",
        "claude-desktop/appdata/config.json",
        // Claude Code's OAuth tokens (plaintext on Windows). Restore carries
        // the live copy over on the same machine; elsewhere: `claude login`.
        "claude-code/dotclaude/.credentials.json",
        // "**/local-agent-mode-sessions/**" used to be here, excluded as
        // "ephemeral agent state". Removed 2026-04-23 after a user-
        // reported restore showed these are actually the Cowork
        // projects' per-session metadata (CLAUDE.md, settings.json,
        // sessions, agents, plugins, skills). We KEEP the OAuth /
        // debug-reparse-point / leveldb-lock guards below.
        "**/mcp-needs-auth-cache.json",
        // SQLite shared-memory index; always rebuilt from the db on open.
        "**/*-shm",
        // OpenAI Codex state root (%USERPROFILE%\.codex). Kept: config.toml,
        // AGENTS.md, sessions/, archived_sessions/, skills/, rules/, agents/,
        // memories, hooks, generated_images/ and the sqlite state/history
        // databases. Dropped: credentials, machine-bound sandbox identity,
        // downloaded binaries, logs, locks and caches Codex rebuilds itself.
        // All are prefix-scoped so a project file with the same name is
        // never affected.
        "codex/dotcodex/auth.json",
        "codex/dotcodex/installation_id",
        "codex/dotcodex/cap_sid",
        "codex/dotcodex/.sandbox/**",
        "codex/dotcodex/.sandbox-bin/**",
        "codex/dotcodex/.sandbox-secrets/**",
        "codex/dotcodex/packages/**",
        "codex/dotcodex/plugins/.plugin-appserver/**",
        "codex/dotcodex/app-server-daemon/**",
        "codex/dotcodex/shell_snapshots/**",
        "codex/dotcodex/.tmp/**",
        "codex/dotcodex/tmp/**",
        "codex/dotcodex/log/**",
        "codex/dotcodex/*.log",
        "codex/dotcodex/**/logs_*.sqlite*",
        "codex/dotcodex/**/*.lock",
        "codex/dotcodex/*.guard",
        "codex/dotcodex/*-locks/**",
        "codex/dotcodex/..codex-global-state.json.tmp-*",
        "codex/dotcodex/.codex-global-state.json.bak",
        "codex/dotcodex/models_cache.json",
        // Codex desktop (Electron). "web/" is the embedded browser's own
        // Chromium profile (cookies, site logins, component downloads) -
        // credentials plus several hundred MB of regenerable data.
        "codex-desktop/appdata/web/**",
        "codex-desktop/appdata/sentry/**",
        "codex-desktop/appdata/windows-msix-updater/**",
        "codex-desktop/appdata/blob_storage/**",
        "codex-desktop/appdata/shared_proto_db/**",
        "codex-desktop/appdata/Shared Dictionary/**",
        "codex-desktop/appdata/DIPS*",
        "codex-desktop/appdata/SharedStorage*",
        // Project-folder noise for auto-backed-up Cowork projects.
        // Intentionally NOT included are the short generic names "bin",
        // "obj", "dist", "build", "out", "target" because they
        // frequently appear inside Claude Desktop Extensions
        // (e.g. "Claude Extensions/pdf-server/dist/index.js" is the
        // real MCP-server entry point). The remaining names are narrow
        // enough to only match real project noise.
        "**/node_modules/**",
        "**/.git/objects/**",
        "**/.git/lfs/**",
        "**/.venv/**",
        "**/venv/**",
        "**/__pycache__/**",
        "**/.next/**",
        "**/.nuxt/**",
        "**/.gradle/**",
        "**/.idea/**",
        "**/.vs/**",
        "**/.DS_Store",
        "**/Thumbs.db",
        "**/*.pyc",
        "**/*.swp",
    };
}
