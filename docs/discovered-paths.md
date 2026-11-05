# Discovered Claude Paths (Phase 0)

Tracks which Claude artifact paths have been verified on a real install vs which are still assumptions.

**Last updated:** 2026-10-29 (Claude Desktop 4.29 / Store 3.6556, Codex app 26.1021: see "2026-10-29 update" below)

**Verification method:** directory listing + content inspection on the dev machine. ProcMon-based verification of write-path dynamics is tracked in `docs/phase0-procmon.md` and issue #4.

## Path inventory

| Artifact | Path | Status | Source |
|---|---|---|---|
| Claude Desktop install | `C:\Program Files\WindowsApps\Claude_<version>_x64__pzs8sxrjxfjjc\` | VERIFIED (Store) | `Get-AppxPackage -Name Claude` 2026-04-23 |
| Claude Desktop package data | `%LOCALAPPDATA%\Packages\Claude_pzs8sxrjxfjjc\` | VERIFIED | `ls` 2026-04-23 |
| Claude Desktop AppData (roaming) | `%APPDATA%\Claude\` | VERIFIED | `ls` 2026-04-22 (Preferences, IndexedDB, Local Storage, Extensions) |
| Claude Desktop IndexedDB | `%APPDATA%\Claude\IndexedDB\` | VERIFIED | `ls` 2026-04-22 |
| Claude Desktop Local Storage | `%APPDATA%\Claude\Local Storage\` | VERIFIED | `ls` 2026-04-22 |
| Claude Desktop Preferences | `%APPDATA%\Claude\Preferences` | VERIFIED | `ls` 2026-04-22 |
| **MCP servers + user preferences** | `%APPDATA%\Claude\claude_desktop_config.json` | VERIFIED | content inspection 2026-04-23 (issue #5) |
| Installed Claude Desktop extensions | `%APPDATA%\Claude\extensions-installations.json` | VERIFIED | content inspection 2026-04-23 |
| Claude Desktop Extensions (MCP server code) | `%APPDATA%\Claude\Claude Extensions\<extensionId>\` | VERIFIED | `ls` 2026-04-23 |
| Extension blocklist | `%APPDATA%\Claude\extensions-blocklist.json` | VERIFIED | `ls` 2026-04-23 |
| Runtime MCP server summary | `%APPDATA%\Claude\logs\mcp-info.json` | VERIFIED (regenerable) | content inspection 2026-04-23 |
| Claude Code user profile | `%USERPROFILE%\.claude\` | VERIFIED | `ls` 2026-04-22 (CLAUDE.md, agents, plugins, projects, skills, settings.json, sessions) |
| Claude Code plugins | `%USERPROFILE%\.claude\plugins\` | VERIFIED | `ls` 2026-04-22 |
| Claude Code skills | `%USERPROFILE%\.claude\skills\` | VERIFIED | `ls` 2026-04-22 |
| Claude Desktop Local AppData | `%LOCALAPPDATA%\Claude\` | VERIFIED (empty or small) | `ls` 2026-04-23 |
| Cowork sessions (Claude CLI agent mode) | `%APPDATA%\Claude\local-agent-mode-sessions\<guid>\...\.claude\` | VERIFIED (many, ephemeral) | `ls` 2026-04-23 - EXCLUDED by glob |

## Security-critical exclusions

These files are deliberately **not** backed up because they hold credentials or maschine-bound secrets:

| Path | Reason |
|---|---|
| `%APPDATA%\Claude\config.json` | Contains `oauth:tokenCache` (encrypted OAuth refresh-token blob). File is regenerated on first sign-in after restore. |
| `**/tokens.dat` | Generic token cache used by some Electron apps. |
| `**/Login Data*`, `**/Cookies*` | Chromium credential and cookie stores. |
| `**/mcp-needs-auth-cache.json` | Per-agent-mode OAuth-flow state, machine-bound. |
| `**/local-agent-mode-sessions/**` | Ephemeral sessions for Claude Code inside Claude Desktop; not user-meaningful to carry between machines. |
| `**/.remote-plugins/**` | Binary plugin cache; re-fetched on first `claude plugin sync`. |

## Operational exclusions (non-credential, just noise)

| Glob | Reason |
|---|---|
| `**/Cache/**`, `**/GPUCache/**`, `**/DawnGraphiteCache/**`, `**/DawnWebGPUCache/**`, `**/Code Cache/**`, `**/Extensions Update Cache/**` | Regenerable Electron/Chromium caches. |
| `**/Crashpad/**`, `**/VideoDecodeStats/**`, `**/Partitions/**`, `**/Network/**` | Electron subsystems. |
| `**/*.ldb.tmp`, `**/*-journal`, `**/*-wal` | LevelDB/SQLite transient files; `-wal` added 2026-04-23 after observing `DIPS-wal` churn. |
| `**/LOCK` | LevelDB lock file - cannot be read while the owning process is alive. |
| `**/debug/latest` | Reparse-point / symlink to the most recent debug log; unreadable without elevation. |

## Claude Desktop version detection

`Get-AppxPackage -Name Claude | Select-Object -ExpandProperty Version` returns the installed version (e.g. `1.3883.0.0`). The `BackupEngine` calls this and stores the result in `manifest.claudeDesktopVersion`. On restore, `VersionGating.Evaluate(...)` emits an Info/Warn/Block result based on semantic-version comparison (issue #6).

## Dynamic captures (session 6, 2026-04-23)

Ran `scripts/claude-path-diff.ps1 -Scenario idle60s -Duration 60` while Claude Desktop was running (two `claude.exe` processes). Raw report: `docs/phase0-captures/2026-04-23_idle60s.diff.txt`. Summary in `docs/phase0-captures/README.md`. Key facts:

- **Reparse-point redirection**: `%APPDATA%\Claude` is a reparse point whose `Target` is `%LOCALAPPDATA%\Packages\Claude_pzs8sxrjxfjjc\LocalCache\Roaming\Claude`. The two paths share files; backing up `%APPDATA%\Claude` already covers the Packages path. `WindowsPathDiscovery` was not changed.
- **Only transient files move during idle**: 44 modified files, all log / LevelDB journal / Chromium cache / sentry state. Added `**/*-wal` exclusion with test coverage (`DIPS-wal`, `*-wal` endings). No credential-bearing files appeared.
- **Residual scenarios** (new chat, MCP install, connector OAuth, MCP call): still require interactive user flow. Run `pwsh scripts/claude-path-diff.ps1 -Scenario <name>` between the two actions and drop the diff in `docs/phase0-captures/`. No code change is pending on these.

## 2026-10-29 update (Claude Desktop 4.x, Codex app 26.x)

Verified on the dev machine with Claude Desktop 4.29.0.0 (claude.ai MSIX `Claude_pzs8sxrjxfjjc`), the Microsoft Store build 3.6556.2.0 (`AnthropicPBC.Claude_fnn82j28hfe8t`, installed side by side) and the Codex app 26.1021.3705.0 (`OpenAI.Codex_2p2nqsd0c76g0`).

| Finding | Consequence |
|---|---|
| `%APPDATA%\Claude` is a **plain folder** now, not a reparse point: both Claude packages declare `unvirtualizedResources` and write it directly. `Packages\Claude_*\LocalCache\Roaming\Claude` no longer exists. | Fallback candidates kept for older builds; `AnthropicPBC.Claude_*` added. Safety-backup rename now works for it, so `config.json` and device files are carried over on a same-machine restore. |
| Two Claude packages can be installed at once and share `%APPDATA%\Claude`. Their versions are numbered independently (4.x vs 3.x). The claude.ai build jumped 1.22209 -> 4.29. | Editions are recorded; version gating compares within an edition and treats a newer app as informational. |
| Claude Desktop runs Claude Code from `%APPDATA%\Claude\claude-code\<version>\` (~300 MB per version) and `claude-code-vm\`; the Cowork VM image is `vm_bundles\claudevm.bundle\rootfs.vhdx` (9.5 GB) next to `sessiondata.vhdx` (2 GB). | Code-tab binaries excluded. The Cowork VM (`vm_bundles\`, `claude-code-vm\`) is backed up whole since 0.5.1: the VM writes to `rootfs.vhdx` too. |
| Claude Desktop 4.16xxx moves its data folder out of the package to `%LOCALAPPDATA%\Claude-Data\<package family, lower case>` (e.g. `claude_pzs8sxrjxfjjc`), verified 2026-11-05 on 4.16336. `%APPDATA%\Claude` / `Packages\...\LocalCache\Roaming\Claude` keep only a 0-byte lock file. The 3p profile lives in `%LOCALAPPDATA%\Claude-3p`. | Discovery tries `Claude-Data` first and only accepts a folder with app data in it. |
| New files with device identity or tokens: `ant-did`, `ant-device-registry.json`, `remote-control-state.json` (identities), `ssh-remote-server-state.json` (tokens), `bridge-state.json`. | Excluded + carried over on the same machine. |
| Services `CoworkVMService` / `CoworkVMServiceStore` (cowork-svc.exe) and `CodexSandboxService.OpenAI.Codex` run as LocalSystem from the app packages. Their security descriptors grant Authenticated Users start and stop (`RPWP` for `AU`), verified 2026-10-30. | Recognised by the binary path in the registry `ImagePath`; stopped with their app and started again afterwards. An elevated `sc.exe` (UAC) is the fallback where a build lacks that right; never from a scheduled run. |
| Codex app runs as `ChatGPT.exe`; its Electron profile is `%APPDATA%\Codex\web\Codex` (`--user-data-dir`), virtualized into the package's `LocalCache`. The top-level Chromium folders are stale since 2026-05. `web\Codex\codex-browser-app` is an embedded browser (cookies, logins). | App state in `web\Codex` kept, browser and components excluded. |
| Codex app-server and runtimes run from `%LOCALAPPDATA%\OpenAI\Codex\{bin,runtimes}` and `~\.cache\codex-runtimes`, as children of `ChatGPT.exe`. They outlive the app when it exits uncleanly. The separately installed Codex CLI lives in `%LOCALAPPDATA%\Programs\OpenAI\Codex\bin`. | Process detection assigns children to the app by ancestry; `%LOCALAPPDATA%\OpenAI\Codex` is also an app root, so orphaned helpers are still the app's. |
| `~\.codex\sqlite\codex-dev.db-wal` held 6 MB not yet checkpointed. | All SQLite databases are archived from an online-backup snapshot. |
| Installed plugins live in `~\.claude\plugins\cache` and `~\.codex\plugins\cache`; the case-insensitive `**/Cache/**` glob had excluded both. | Re-included. |
| Agent configs reference folders outside the agent folders (MCP servers under `D:\tool\...`, hook scripts, local marketplaces, junctioned skills). | Backed up as `external/<hash>`; see README "External folders". |

## Still pending

- **Win32 Claude Desktop installs** (as opposed to the Microsoft Store variant confirmed above): if Anthropic ever ships a non-Store MSI, re-run this inventory on that install and add a second column to this table.
