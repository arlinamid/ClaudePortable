namespace ClaudePortable.Core.Archive;

/// <summary>
/// What a backup leaves out. Order matters: a "!" glob re-includes what an
/// earlier glob excluded, and the last matching glob wins (see
/// <see cref="ExclusionGlob"/>). The Codex app profile block therefore comes
/// first, so the generic Chromium / credential exclusions after it still
/// apply inside the folders it re-includes.
/// </summary>
public static class DefaultExclusions
{
    public static IReadOnlyList<string> Globs { get; } = new[]
    {
        // Codex desktop (26.x, ChatGPT.exe): web/Codex is the app's whole
        // Chromium user-data-dir. It mixes the app's own state with an
        // embedded browser (codex-browser-app: cookies, site logins,
        // autofill, history) and ~400 MB of downloaded components (models,
        // Widevine, TTS, crx caches). Keep the app state only.
        "codex-desktop/appdata/web/**",
        "!codex-desktop/appdata/web/Codex/Local State",
        "!codex-desktop/appdata/web/Codex/browser-sidebar-*.json",
        "!codex-desktop/appdata/web/Codex/artifact-sessions/**",
        "!codex-desktop/appdata/web/Codex/Default/Preferences",
        "!codex-desktop/appdata/web/Codex/Default/Local Storage/**",
        "!codex-desktop/appdata/web/Codex/Default/IndexedDB/**",
        "!codex-desktop/appdata/web/Codex/Default/Session Storage/**",
        "!codex-desktop/appdata/web/Codex/Default/WebStorage/**",

        "**/Cache/**",
        "**/GPUCache/**",
        "**/GrShaderCache/**",
        "**/ShaderCache/**",
        "**/GPUPersistentCache/**",
        "**/GraphiteDawnCache/**",
        "**/DawnGraphiteCache/**",
        "**/DawnWebGPUCache/**",
        "**/Code Cache/**",
        "**/Extensions Update Cache/**",
        "**/component_crx_cache/**",
        "**/extensions_crx_cache/**",
        "**/Crashpad/**",
        "**/VideoDecodeStats/**",
        "**/Partitions/**",
        "**/Network/**",
        "**/*.ldb.tmp",
        "**/*-journal",
        // SQLite write-ahead logs. Safe to drop because every SQLite
        // database is archived from a consistent snapshot that already
        // contains the WAL's content (see SqliteSnapshot); a database that
        // cannot be snapshotted keeps its -wal in the archive.
        "**/*-wal",
        "**/tokens.dat",
        "**/Login Data*",
        "**/Cookies*",
        "**/Web Data*",
        "**/.remote-plugins/**",
        "**/LOCK",
        "claude-desktop/**/lockfile",
        "codex-desktop/**/lockfile",
        "**/debug/latest",
        // Claude Desktop's OAuth token cache; restore carries it over on
        // the same machine, elsewhere the user signs in again.
        "claude-desktop/appdata/config.json",
        "claude-desktop/appdata-3p/config.json",
        // Device identity and pairing state (Claude Desktop 4.x): device
        // ids, remote-control identities, SSH remote tokens, Chrome bridge
        // pairing. Carried over on the same machine like config.json.
        "claude-desktop/appdata/ant-did",
        "claude-desktop/appdata/ant-device-registry.json",
        "claude-desktop/appdata/remote-control-state.json",
        "claude-desktop/appdata/ssh-remote-server-state.json",
        "claude-desktop/appdata/bridge-state.json",
        // Downloaded binaries Claude Desktop refetches on start: the Claude
        // Code builds it runs for the Code tab (one folder per version,
        // ~300 MB each), the VM-side build, and the Cowork VM image. The
        // VM's own disk with session data (sessiondata.vhdx) is kept.
        "claude-desktop/appdata/claude-code/**",
        "claude-desktop/appdata/claude-code-vm/**",
        "claude-desktop/appdata/vm_bundles/warm/**",
        "claude-desktop/appdata/vm_bundles/*/rootfs.vhdx",
        "claude-desktop/appdata/vm_bundles/*/smol-bin.vhdx",
        "claude-desktop/appdata/vm_bundles/*/initrd",
        "claude-desktop/appdata/vm_bundles/*/vmlinuz",
        "claude-desktop/appdata/vm_bundles/*/.*.origin",
        "claude-desktop/appdata/logs/**",
        "claude-desktop/appdata/sentry/**",
        "claude-desktop/appdata/fcache",
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
        // memories, hooks, automations/, .chatgpt-projects/, installed
        // plugins (plugins/cache, minus the app-bundled ones),
        // generated_images/ and the sqlite state/history databases.
        // Dropped: credentials, machine-bound sandbox and install identity,
        // account provisioning state, downloaded binaries, logs, locks and
        // caches Codex rebuilds itself. All are prefix-scoped so a project
        // file with the same name is never affected.
        "codex/dotcodex/auth.json",
        "codex/dotcodex/installation_id",
        "codex/dotcodex/cap_sid",
        "codex/dotcodex/chrome-native-hosts-v2.json",
        "codex/dotcodex/project-chatgpt-provisioning/**",
        "codex/dotcodex/project-chatgpt-pairing/**",
        "codex/dotcodex/.sandbox/**",
        "codex/dotcodex/.sandbox-bin/**",
        "codex/dotcodex/.sandbox-secrets/**",
        "codex/dotcodex/packages/**",
        "codex/dotcodex/cache/**",
        "codex/dotcodex/plugins/.plugin-appserver/**",
        "codex/dotcodex/plugins/.remote-plugin-install-staging/**",
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
        // Codex desktop, pre-26.x layout (top-level Chromium profile).
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
        // External folders (MCP servers, hook scripts, link targets) are
        // archived without their repository: a .git without its objects is
        // a broken repo. The manifest keeps the origin URL to clone from.
        "external/*/.git/**",
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

        // Installed plugins. Claude Code (installed_plugins.json ->
        // plugins/cache/<marketplace>/<plugin>/<version>) and Codex
        // (plugins/cache/<marketplace>/<plugin>) run plugins from here.
        // "**/Cache/**" matches case-insensitively and used to drop both,
        // so restored machines had plugins registered but missing. Kept
        // whole (a plugin may ship its own node_modules), except the
        // plugins bundled with the Codex app, which it reinstalls itself.
        "!claude-code/dotclaude/plugins/cache/**",
        "!codex/dotcodex/plugins/cache/**",
        "codex/dotcodex/plugins/cache/openai-bundled/**",
    };
}
