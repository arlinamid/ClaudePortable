# AgentPortable

Windows desktop app (WPF + CLI) that backs up and restores your AI coding agents' local state - Claude Desktop, Cowork projects, Claude Code, the OpenAI Codex CLI and the Codex desktop app - as a single ZIP. The ZIP is written to any local folder - USB stick, OneDrive-synced folder, Google Drive Desktop mount, Dropbox, NAS share - and whatever sync client you already have takes it from there. **No cloud APIs, no OAuth registrations, no upload-resume logic.**

> **Formerly ClaudePortable.** Only the product name changed. The CLI is still `claudeportable.exe`, and app data (`%LOCALAPPDATA%\ClaudePortable`), sync folders (`<SyncClient>\ClaudePortable`), backup file names (`claude-backup_*.zip`) and scheduled task names (`ClaudePortable-Daily`) keep their old names, so existing backups, settings and tasks keep working after an upgrade.

> Status: alpha, usable. Tested roundtrip from a live workstation (~14 GB `.claude` + Cowork state) to a fresh laptop with a different Windows username, via OneDrive. See the [latest release](../../releases/latest) for the current build.

<img width="2208" height="1422" alt="restore complete" src="https://github.com/user-attachments/assets/18c10a51-dbe5-42f3-876d-8a866be66426" />


## What gets backed up

| Category | Location | Notes |
|---|---|---|
| Claude Desktop state | `%APPDATA%\Claude` (incl. IndexedDB, Local Storage, preferences, MCP config, installed extensions, Code-tab session metadata, the Cowork VM's `sessiondata.vhdx`) | Works for every edition: the claude.ai download (`Claude` MSIX), the Microsoft Store build (`AnthropicPBC.Claude`) and the legacy per-user installer. Older builds that redirect AppData into `%LOCALAPPDATA%\Packages\<package>\LocalCache\...` are resolved by wildcard. Left out: the Claude Code / VM binaries Claude Desktop downloads itself (`claude-code\`, `claude-code-vm\`, the VM image in `vm_bundles\`), logs, GPU caches, and device identity / pairing files (`ant-did`, `remote-control-state.json`, `ssh-remote-server-state.json`, ...). |
| Claude Desktop (3rd-party inference) | `%APPDATA%\Claude-3p` | Profile of Claude Desktop in Bedrock / Vertex / gateway mode, when present. |
| Claude Desktop version + edition | Per-user package registry (no PowerShell, no admin) | Stored in the manifest with every installed Claude / Codex app. Restore informs when the installed app is newer, warns or blocks only when it is older; versions of different editions are not compared. |
| Cowork session metadata | `%APPDATA%\Claude\local-agent-mode-sessions\<guid>\...\.claude\` | Per-project memory (`CLAUDE.md`), settings, sessions, agents, plugins, skills. |
| **Cowork project folders** | Auto-discovered from `userSelectedFolders` in each session's `local_*.json` | Every folder you opened in a Cowork session is backed up to its own archive prefix with sensible project-noise exclusions (`node_modules`, `.git/objects`, `.venv`, etc.). |
| Claude Code user profile | `%CLAUDE_CONFIG_DIR%`, default `%USERPROFILE%\.claude` | Skills, installed plugins, projects, sessions, settings, CLAUDE.md. |
| Claude Code global settings | `.claude.json` next to it | User-scoped MCP servers and per-project settings (allowed tools, project MCP servers, trust). **Merged** on restore: account / machine ids and server caches are never copied over (see below). |
| Claude Code plugins / skills | Under `.claude\plugins\` and `.claude\skills\` | Installed plugins (`plugins\cache\`, where `installed_plugins.json` points) are included, so they work right after a restore. Remote plugin binary cache (`.remote-plugins/`) is excluded. |
| **Codex CLI / app state** | `%CODEX_HOME%`, default `%USERPROFILE%\.codex` | `config.toml`, `AGENTS.md`, `sessions/` + `archived_sessions/` (conversation history), `skills/`, `rules/`, `agents/`, memories, `hooks.json` + `hooks/`, `automations/`, `.chatgpt-projects/`, installed plugins (`plugins/cache/`, minus the app-bundled ones), `generated_images/`, and the sqlite state/thread-history databases. Excluded: `auth.json`, sandbox and install identity (`.sandbox*`, `cap_sid`, `installation_id`, `chrome-native-hosts-v2.json`, `project-chatgpt-provisioning/`), downloaded binaries (`packages/`, `plugins/.plugin-appserver/`), `cache/`, logs, locks and temp folders. |
| Codex desktop app | `%APPDATA%\Codex` (Store package `OpenAI.Codex_*`, resolved by wildcard; since 26.x the app runs as `ChatGPT.exe`) | The app's state from its profile `web\Codex` (Local State, Preferences, Local Storage, IndexedDB, artifact sessions). Its embedded browser (`codex-browser-app`: cookies, logins, history, autofill), downloaded browser components and Chromium caches are excluded. |
| Shared agent skills | `%USERPROFILE%\.agents` | Skills installed with `npx skills add` live here and are linked into `.claude\skills`; Codex reads `.agents\skills` directly. |
| Links (junctions / symlinks) | Any linked folder inside the sources above | Recorded in the manifest, **not** followed; a target outside the backed-up folders is backed up as an external folder. See [Links](#links-junctions-and-symlinks). |
| **External folders the settings use** | Read from the agents' own configuration | MCP server code, hook and status-line scripts, local plugin marketplaces and link targets that live outside the agent folders (e.g. `D:\tools\my-mcp`). See [External folders](#external-folders). |
| SQLite databases | Everywhere above | Archived from a consistent snapshot (SQLite online backup), so transactions still in a `-wal` file are included even while Codex or a CLI is running. |

Explicitly **not** in scope, on purpose:

- OAuth refresh tokens, API keys, `config.json` with `oauth:tokenCache`, DPAPI blobs, Claude Code `.credentials.json`, Codex `auth.json` - user re-authenticates connectors, Claude Code (`claude login`) and Codex (`codex login`) after restore. On a same-machine restore the existing Claude Code / Codex credentials and the Codex sandbox setup are carried over from the safety backup, so you stay signed in.
- The identity part of `.claude.json` (`userID`, `machineID`, `oauthAccount`, `anonymousId`, ...) and its server-side caches. The rest of the file is backed up and merged into the target machine's `.claude.json`: the backup's MCP servers and project settings are added, the target keeps its own identity.
- Active Cowork VM runtime state (processes, scheduled tasks) - only persistent artifacts.
- Cloud-client upload status - AgentPortable writes to a folder, the sync client propagates. Zip destination is flagged if the folder carries `FILE_ATTRIBUTE_OFFLINE` / `RECALL_ON_DATA_ACCESS` so you know OneDrive / GDrive is behind.

## Download

Grab [the latest release](../../releases/latest). Two artifacts:

| Artifact | Best for | Install | Uninstall |
|---|---|---|---|
| `AgentPortable-<version>-portable.exe` | Running on any machine, no admin | Double-click | Delete exe + `%LOCALAPPDATA%\ClaudePortable` |
| `AgentPortable-<version>.msi` | Permanent install with Start-menu entry | Run the MSI | Apps & Features -> AgentPortable -> Uninstall |

Both are **self-contained** and bundle the .NET 10 Windows Desktop runtime. A `.sha256` file ships next to the portable exe for integrity verification.

### First-run SmartScreen warning (unsigned)

Releases are not code-signed yet (see [issues](../../issues) for the signing plan). On first run SmartScreen shows "Windows protected your PC". To unblock:

1. Right-click the downloaded file -> **Properties** -> tick **Unblock** -> **OK**.
2. Double-click to run.

Optional integrity check:

```powershell
(Get-FileHash .\AgentPortable-<version>-portable.exe -Algorithm SHA256).Hash
# compare against the content of the .sha256 file
```

## GUI flows

Launch with no arguments (or `--gui`). Warm-dark UI in the Claude Desktop style, built to WCAG 2.1 AA (see [Accessibility](#accessibility)). Six sections:

- **Status** - summary cards for backups / targets / discovered paths, the **What to back up** checkboxes (Claude Desktop, Cowork projects, Claude Code, Codex; saved in `settings.json` and used by *Backup now* and by the automatic backup task), plus a grid of existing snapshots per target with a **Contents** column.
- **Targets** - folder list. Auto-discovers `<SyncClient>\ClaudePortable` on every recognised sync client (OneDrive Personal / Business, Dropbox, Google Drive Desktop), so a restore on a second machine picks up the first machine's backups without configuration. Manual add/remove available.
- **Discovery** - read-only view of detected Claude + Codex paths and sync clients.
- **Restore** - backup grid with per-row `STATUS` (`Synced` / `Cloud-only` / `Unreadable`) and `CONTENTS`, **What to restore** checkboxes (e.g. restore only Codex from a full backup; untouched parts of the machine stay as they are), `Restore from file...` escape hatch for a ZIP that is not in any configured target, and an **Advanced options** panel for overriding the target user profile (e.g. restoring a `sascha` backup onto a laptop with `sasch` as the user) and for the version-gate override.
- **Logs** - every backup, restore and path repair as a collapsible card, newest first: status (completed / with warnings / failed / cancelled / interrupted), how it was started (app, command line, scheduled task), time, duration and a one-line result. Inside a card the run is split into collapsible sections - *Running apps*, *Installed apps*, *What is backed up*, *Left out (details)*, *Links*, *External folders*, *Databases*, *ZIP archive*, *Retention*; for a restore *Backup details*, *Version check*, *Paths*, *Restored folders*, *Provide on this PC*. Sections with a warning or error open by themselves. Scheduled and command-line runs show up here too, so you can see what last night's backup did. *Copy this run* puts the run on the clipboard as text; *Open log folder* opens the files (see [Logging](#logging)).
- **Schedule** - enumerates every Windows scheduled task on this machine via `schtasks.exe /Query /FO CSV /V`. AgentPortable-managed entries are flagged green (name starts with `ClaudePortable-` / `AgentPortable-`, or the author contains either name). Tasks that aren't managed but touch a Claude/Cowork/`.claude`/`.codex` path - including hand-written backup PowerShell scripts that compete with AgentPortable - are flagged orange. Per-row buttons run/disable/enable/delete the task and copy its raw XML to the clipboard. Use this to spot legacy `\Claude-Desktop-Backup`-style tasks that write loose-file backups into a long-path OneDrive folder and break sync.

**Running apps.** A running app keeps writing while it is copied, so the backup can miss your latest chats or contain half-written stores. With **Close Claude Desktop and the Codex app during the backup** ticked on the Status page (the default), *Backup now* closes them - whole process trees, so the background helpers too: the Claude Code sessions Claude Desktop runs, the Codex app-server and runtimes, even ones a crashed app left behind - and reopens them when the backup is done. Their Windows services (`CoworkVMService`, `CodexSandboxService`) are stopped with them and started again afterwards; normally no prompt appears, but if Windows refuses, AgentPortable asks for administrator rights (never in a scheduled run, which logs a warning instead). Unticked, it asks each time (close / back up anyway / cancel). Re-install the scheduled task after changing it; the task then runs `backup --close-apps`. Processes are recognised by where they were started from, not by name, so all editions are covered; AgentPortable never ends a process tree it runs inside of (e.g. started from a terminal in Claude Desktop) and says so instead. Codex and Claude Code CLI sessions are not closed for a backup (their data is append-only JSONL plus SQLite, which is snapshotted), but they are for a restore.

A ProgressBar on the status bar appears for the duration of any backup or restore, showing the current phase (`Extracting archive`, `Writing cowork-projects/<hash>`, etc.) with file-level percentage. Both commands run on the thread pool so the window stays responsive during multi-GB operations. A running backup can be stopped with **Cancel** next to the progress bar; no partial ZIP is left behind.

A tray icon keeps the app alive in the background; closing the window hides it, `Quit` in the tray menu actually exits.

## Logging

Two kinds of files in `%LOCALAPPDATA%\ClaudePortable\logs`, written by the GUI, the CLI and the scheduled task alike:

- `claudeportable-<date>.log` - the full text log, one line per step with level and section, kept 30 days. It includes the detail the Logs tab keeps folded away: every excluded folder and file, every database snapshot, every closed process with its path, timings per phase.
- `runs\<start>-<kind>-<pid>-<id>.jsonl` - one structured file per run (start record, entries with time / level / section / message, end record with status). The Logs tab is built from these; the newest 150 are kept.

The CLI still prints its usual output; warnings also go to stderr with their section, e.g. `warning: [External folders] Not backed up: ...`. A full disk never fails a backup or restore because of logging.

## Accessibility

The GUI targets **WCAG 2.1 AA** and is usable with a keyboard and screen readers (Narrator, NVDA, JAWS):

- **Screen readers see every page.** All controls are in the Windows UI Automation tree with an accessible name: sidebar items, buttons, check boxes, text boxes (named after their visible label), grids, and the language selector. Page titles and section titles are exposed as level-1 / level-2 headings.
- **Logs are navigable.** Every run card and section is an expand/collapse control with a spoken name that includes its status and warning count ("Backup D:\Backups, 2026-10-29 23:00, Completed with warnings, 1 warning"); warning and error lines are announced with their level.
- **Status messages are announced.** The status bar and the Schedule status line are live regions, so a screen reader reads "Backup complete", errors and so on without moving focus.
- **Contrast.** Text is at least 4.5:1 (body text 7:1 or more). Input outlines and focus rings are at least 3:1 against what surrounds them. The primary button's focus ring is drawn outside the coral fill so it contrasts with the page (8.7:1).
- **Not by colour alone.** Scheduled tasks show a TYPE column (AgentPortable / Agent-related / Other) next to the coloured dot, and read-only status columns say Yes / No instead of showing disabled check boxes.
- **Keyboard.** Every action is reachable with Tab / arrow keys and has a visible focus ring. The per-task ⋮ menu opens with Enter or Space.

`scripts/a11y-verify.ps1` checks the automation tree of a running build: it visits every page, fails on unnamed interactive controls, and toggles a backup check box through UI Automation to confirm the change is saved. Run it after UI changes:

```powershell
pwsh scripts/a11y-verify.ps1
```

## Languages (i18n)

The GUI and CLI are fully localized in **English** and **Hungarian (magyar)**.

- **GUI**: language selector (English / Magyar) in the sidebar footer. Switching re-renders the whole window live - no restart - and the choice persists in `%LOCALAPPDATA%\ClaudePortable\settings.json`.
- **CLI**: pass `--lang en` or `--lang hu` to any command to override the language for that run.
- **Default**: without an explicit choice, the saved GUI language wins; failing that, the Windows display language decides (Hungarian Windows → Hungarian, everything else → English).
- Machine-readable output (`--json`), the diagnostic log lines (Logs tab, `claudeportable-*.log`), and System.CommandLine's built-in help/version boilerplate intentionally stay in English.

Strings live in `src/ClaudePortable.App/Localization/Strings.resx` (English, neutral) and `Strings.hu.resx` (Hungarian); `LocalizationParityTests` fails the build's test run if the two catalogs ever drift (missing keys or mismatched `{n}` placeholders). To add a language, copy `Strings.hu.resx` to `Strings.<code>.resx`, translate, and extend `Loc.Normalize` plus the GUI selector.

## CLI

Same binary - if you pass arguments it attaches to the parent console.

```
claudeportable discover                                # also: installed app editions, running agent processes, external folders
claudeportable backup   --to <folder> [--tier daily] [--include <groups>] [--skip <groups>] [--close-apps]
                                                       # create a backup ZIP (auto-rotates unless --no-rotate);
                                                       # --close-apps closes Claude Desktop / the Codex app and reopens them after
claudeportable list     --in <folder> [--json]         # list backups
claudeportable restore  --from <zip>  --yes [--target-user <path>] [--ignore-version-mismatch] [--include <groups>] [--skip <groups>]
claudeportable rotate   --in <folder> [--daily 7] [--weekly 3] [--monthly 2]
claudeportable repair-paths [--dry-run] [--yes] [--from <old profile>]  # point restored Codex / Claude Code data at this user's profile
claudeportable schedule install|show|remove|emit       # Windows Task Scheduler integration (install/emit accept --include/--skip/--close-apps)
claudeportable schedule list [--all|--managed|--relevant] [--json]  # enumerate all scheduled tasks, flag Claude relevance
claudeportable schedule disable|enable|run <name>       # toggle / trigger a scheduled task by full name
```

### Choosing what to back up or restore

`--include` and `--skip` take a comma-separated list of **groups**:

| Group | Contains |
|---|---|
| `claude-desktop` | Claude Desktop app data, incl. Cowork session metadata |
| `cowork` | Project folders opened in Cowork sessions |
| `claude-code` | `%USERPROFILE%\.claude` (+ the shared `.agents` skill store) |
| `codex` | `%CODEX_HOME%` / `.codex` and the Codex desktop app (+ the shared `.agents` skill store) |

Aliases: `claude` = `claude-desktop,cowork,claude-code`; `all` = everything (the default). Examples:

```
claudeportable backup  --to D:\Backups --include codex           # Codex only
claudeportable backup  --to D:\Backups --include claude          # Claude only (Desktop, Cowork, Code)
claudeportable backup  --to D:\Backups --skip cowork             # everything except Cowork project folders
claudeportable restore --from <zip> --yes --include claude-code   # restore just Claude Code from a full backup
```

Partial backups are named after their selection (`claude-backup_<ts>_<host>_codex_daily.zip`) and record it in `manifest.json` (`groups`). A restore only asks you to close the apps whose data it is actually writing: restoring Codex does not require quitting Claude Desktop, and the Claude Desktop version check only applies when Claude Desktop data is restored.

Exit codes: `0` ok, `1` usage error, `2` precondition fail (destination unwritable, an agent still running for a restore), `3` runtime error (I/O, invalid backup, version block).

### Example

```
> claudeportable backup --to C:\Users\Sascha\OneDrive\ClaudePortable
created: C:\Users\Sascha\OneDrive\ClaudePortable\claude-backup_2026-04-23T08-08-42Z_DESKTOP-V9US9HF_daily.zip
files:   128314
bytes:   13,906,054,997
sha256:  dfd9656bcc24...
  claudeDesktopAppData: 4957 files
  claudeCodeUserProfile: 719 files
  claudeDesktopLocalAppData: 1 files
  coworkProject:9cb53e6e1c: 19123 files
  coworkProject:09f3bab3e5: 9824 files
  ...
rotation: promoted=0 pruned=0 -> daily=1 weekly=0 monthly=0
```

## Retention (7/3/2)

Auto-rotation runs after every successful backup: the newest daily of each Sunday promotes to weekly; the newest weekly of each finished month promotes to monthly. Prune rules keep 7 daily / 3 weekly / 2 monthly per folder target **and per machine and selection**: several PCs can share one sync folder, and Claude-only or Codex-only backups never push out full backups (or each other). Promotion renames instead of copying, so the sync client only uploads one delta per promotion.

Schedule yourself a recurring backup via the Windows Task Scheduler (`claudeportable schedule install --folder <path> --at 23:00`) or from the GUI tray icon.

## Cross-machine restore

The typical workflow across two machines:

1. Workstation runs `Backup now`. ZIP lands in `%USERPROFILE%\OneDrive\ClaudePortable\claude-backup_<ts>_<host>_daily.zip`.
2. OneDrive syncs to the laptop.
3. Laptop launches AgentPortable. Auto-discovery finds the ZIP in `<OneDrive>\ClaudePortable\`. The Status column tells you whether it is fully synced or still a cloud-only placeholder.
4. If the laptop's Windows username differs, open **Advanced options** on the Restore tab and pick the target user profile (`C:\Users\<other-user>`). The restore engine rewrites every reference in JSON and TOML configs (and the filesystem destination) from the old username to the new.
5. The agents whose data is restored must be closed on the laptop, background helpers included: Claude Desktop (with its Claude Code sessions), Claude Code CLI sessions, the Codex app and `codex` CLI sessions. The app lists what is running and offers to close it.
6. Click **Restore selected snapshot**. Existing `.claude` and `%APPDATA%\Claude` content is moved aside to `<folder>_backup_<timestamp>` before the new data is written.
7. After completion, `claude login` and `codex login` on the laptop and re-authorise any connectors - token caches were deliberately excluded.

Where each folder is restored is decided **on the restore machine**: if Claude Desktop, Claude Code, Codex or `.agents` already has a data folder there (Store or non-Store install, a custom `%CODEX_HOME%`, redirected AppData), that folder is used. Otherwise the backup machine's path is re-rooted to the current profile. Paths inside JSON / TOML configs, `.jsonl` session files and SQLite databases (Codex keeps every conversation's file path in `state_5.sqlite`) are rewritten from the backup machine's `%USERPROFILE%` (recorded in the manifest) to the new one, and Claude Code's path-named project folders (`~\.claude\projects\C--Users-<name>-...`) are renamed to match, so profiles on another drive or outside `C:\Users` work too, e.g. `C:\Users\anna` -> `D:\Profiles\anna.CORP`.

**Already restored onto a different user name with an older version?** If Codex says *failed to resolve rollout path C:\Users\&lt;other name&gt;\...* or Claude Code history is missing, use **Restore -> Advanced options -> Repair paths...** (or `claudeportable repair-paths --dry-run`, then `--yes`). It takes the current user from `%USERPROFILE%`, finds Codex / Claude Code data that still points into another profile, and redirects it in place. Changed files are copied to `%LOCALAPPDATA%\ClaudePortable\path-repair-<timestamp>` first. Close Codex and Claude Code before running it.

Store-app reparse points (Claude Desktop from the Microsoft Store) refuse `Directory.Move` on their targets, so the restore engine detects them and overlays files instead of renaming. This is expected and logged as a single informational warning, not an error.

## Links (junctions and symlinks)

Skill and plugin folders are often links: `npx skills add` links `.claude\skills\<name>` to `.agents\skills\<name>`, and many people junction a skill to the git checkout they develop it in. Backups do **not** follow links inside a source, because the linked content lives elsewhere, can be huge, and can form cycles. Instead:

- **Backup** records every linked folder (`links` in `manifest.json`: archive path + original target) and prints it (`link (not followed, recreated on restore): ...`). Links inside excluded folders (`node_modules` etc.) are ignored. A source folder that is itself a link (e.g. `.claude` junctioned to a dotfiles repo) is still backed up normally. OneDrive / cloud-files folders are reparse points too but not links, so they are backed up normally.
- **Restore** recreates each link after all folders are written, when its target exists on the restore machine. Absolute targets under the old profile are re-rooted to the new one. Absolute local targets become junctions (no admin rights needed). Relative targets become symlinks when Developer Mode allows it, and junctions to the resolved path otherwise.
- If the target does **not** exist (e.g. `D:\src\my-skill` was never cloned on the new machine), restore lists a warning with the exact `mklink /J` command to run once you have restored that folder.

## External folders

Agent setups often depend on folders outside the agent folders: an MCP server checked out under `D:\tools`, a hook script, a local plugin marketplace, a skill junctioned to its git checkout. AgentPortable finds them in the configuration the agents themselves read - nothing is assumed about where they are:

| Read from | Sections |
|---|---|
| Claude Desktop `claude_desktop_config.json` | `mcpServers` |
| Claude Code `settings.json`, `settings.local.json` | `hooks`, `statusLine`, `mcpServers`, `extraKnownMarketplaces` |
| Claude Code `.claude.json` | `mcpServers` (user and per project) |
| Claude Code `plugins/known_marketplaces.json` | local marketplace paths |
| Codex `config.toml` | `[mcp_servers.*]` (command, args, env), `[marketplaces.*]`, `notify` |
| Codex `hooks.json` | `hooks` |
| Links inside any backed-up folder | link targets |

For a script, its folder is backed up (the project folder when the script sits in `dist`, `build`, `src`, ...); a referenced folder is backed up whole. Each goes to `external/<hash>` in the ZIP, with project noise (`node_modules`, `.venv`, `.git`, caches) left out; a git checkout's `origin` URL is recorded instead of its history. A folder over 1 GB / 20,000 files is not archived automatically. Not followed: workspace lists (trusted folders, Codex `[projects]`, Claude Code projects) - those are your projects, not the setup. Not archived, but listed with the reason: runtimes (`python.exe`, `node.exe`, nvm, venvs, npm globals - reinstall them), single data files (they may hold credentials, like a cookie file), and paths that no longer exist. `claudeportable discover` shows the full list before you back up.

On restore, an external folder is written only where nothing exists yet; a checkout already on the new PC is left alone. The post-restore checklist lists what came back, what to reinstall (`npm install`, `pip install`, ...), and what has to be provided by hand.

## Architecture

```
src/
  ClaudePortable.Core/           # Engines, discovery, manifest, path rewriter, version gating
    Abstractions/                # IBackupEngine, IRestoreEngine, IArchiveWriter,
                                 # IPathDiscovery, ICoworkProjectDiscovery,
                                 # IPathRewriter, OperationProgress
    Archive/                     # ZipArchiveWriter, FileEnumerator, ExclusionGlob,
                                 # DefaultExclusions
    Backup/                      # BackupEngine
    Discovery/                   # WindowsPathDiscovery, CoworkProjectDiscovery,
                                 # SyncClientDiscovery, ClaudeDesktopVersionReader
    Manifest/                    # BackupManifest (schemaVersion, archiveTargets,
                                 # sourcePaths, excludedPaths, sha256, tool version)
    Post/                        # PostRestoreChecklistBuilder
    Restore/                     # RestoreEngine, PathRewriter, SafetyBackup,
                                 # VersionGating
  ClaudePortable.Targets/        # FolderTarget (atomic write + safety rename),
                                 # SyncClientDiscovery (Registry-based)
  ClaudePortable.Scheduler/      # RetentionManager (7/3/2), TaskSchedulerEmitter,
                                 # TaskSchedulerInstaller (schtasks.exe wrapper)
  ClaudePortable.App/            # WPF GUI + System.CommandLine CLI in one binary
    Ui/                          # Theme.xaml, MainWindow, ViewModels, Tray, Services
    Commands/                    # Backup, Restore, List, Discover, Schedule, Rotate
  ClaudePortable.Installer/      # WiX 7 MSI (Product.wxs, build-msi.ps1)
tests/
  ClaudePortable.Tests/          # xUnit: exclusion globs, manifest roundtrip,
                                 # path rewriter, retention, folder target, backup
                                 # roundtrip, Task Scheduler XML, version gating
docs/
  spec.md                        # Original German build spec (v2.0, 2026-04-22)
  discovered-paths.md            # Phase 0 inventory + verification history
  phase0-captures/               # FileSystemWatcher-based path-diff captures
  progress.md                    # Per-session checkpoint log
scripts/
  build-exe.ps1                  # Local portable-exe build
  a11y-verify.ps1                # UI Automation accessibility check of the GUI
  claude-path-diff.ps1           # Before/after snapshot diff for Phase 0 research
```

`manifest.json` inside every ZIP lists:

- schema version, created-at, hostname, Windows user, Claude Desktop version, file count + size, SHA-256 over ordered content
- **`sourcePaths`**: key -> absolute path captured at backup time (includes all Cowork projects under `coworkProject:<hash>`)
- **`archiveTargets`**: archive-prefix -> absolute path, used by restore to reconstruct original locations
- `excludedPaths`: every glob applied (auditable; no surprises)

## Security model

- The app **never** archives OAuth tokens or credentials. `config.json` (contains `oauth:tokenCache`), `tokens.dat`, Codex `auth.json` and `.sandbox-secrets`, the Codex app's embedded browser profile, `Login Data*`, `Cookies*`, and `mcp-needs-auth-cache.json` are all explicitly excluded.
- Every file is opened once, read-only, with `FileShare.ReadWrite | FileShare.Delete`, and streamed into the ZIP while it is hashed. A file that is locked, does not open within 30 s (e.g. held by another program or on an unreachable network share), or stops delivering data for 60 s is skipped with a warning naming it (Logs tab in the GUI, stderr on the CLI); the backup always finishes.
- Restore refuses ZIP entries that would land outside its extraction folder (`../`, absolute paths), so a tampered backup cannot write elsewhere on disk.
- Restore is two-stage: safety-rename of the existing folder, then file-by-file overlay. Nothing is deleted until you delete the safety backup manually.
- Cowork project folder auto-discovery refuses drive roots, the user profile root, and every system folder - a misconfigured session cannot ask the tool to back up `C:\`.
- Backups are unencrypted. If that matters, point the tool at a local folder that your sync client encrypts before upload, or keep the ZIP on an encrypted volume (BitLocker, VeraCrypt).

## Development

```bash
dotnet restore
dotnet build
dotnet test
```

189 xUnit cases cover exclusion globs (incl. Claude Extensions paths that must NOT be excluded), manifest (de)serialisation (incl. older manifests missing newer fields), path rewriter across escaped / single-backslash / forward-slash and arbitrary home-relative paths, profiles on other drives / outside `\Users` / with prefix-sharing or space-containing user names, junction recording and recreation (absolute, relative, missing target), backup/restore group selection and per-machine / per-selection retention, Store package wildcard resolution, ZIP path-traversal rejection, retention rotation simulated over 10 weeks with a fake clock, FolderTarget atomic I/O, end-to-end backup roundtrip on synthetic data, Task Scheduler XML emission, version gating, and the scheduled-task enumerator (CSV parser for German-locale `schtasks.exe` output, Claude-relevance classifier, and command-shape assertions for the installer wrapper).

CI runs the same commands on `windows-latest` via `.github/workflows/ci.yml`. The release pipeline at `.github/workflows/release.yml` builds the MSI + portable exe + SHA-256 on `v*` tag push and attaches them to the GitHub Release.

Local portable-exe build:

```powershell
pwsh scripts/build-exe.ps1 -Version 0.5.0
```

Local MSI build (needs the WiX dotnet tool):

```powershell
dotnet tool install --global wix --version 7.0.0
wix eula accept wix7
pwsh src/ClaudePortable.Installer/build-msi.ps1 -Version 0.5.0
```

## Known limitations

- Unsigned binaries; SmartScreen warning on first run. Signing is tracked in [the issues](../../issues).
- First launch of the portable exe extracts its bundled runtime to `%LOCALAPPDATA%\.net\<app>\<hash>\` (~600 MB cached, one-time); the MSI avoids this.
- The portable binary targets `net10.0-windows`. Windows 10 1809+ / Windows 11 x64.
- Content behind links that point outside the backed-up folders (e.g. a skill junctioned to `D:\src\my-skill`) is not in the ZIP; only the link is. Restore that folder yourself (e.g. `git clone`) and the link is recreated on restore or via the printed `mklink /J` command.
- Claude Code or Codex installed inside WSL keep their state in the Linux home (`~/.claude`, `~/.codex` inside the distro) and are not discovered.
- Cowork project auto-discovery only sees the folders referenced in session metadata. Projects you opened only via drag-and-drop or in the terminal are not captured - add them as explicit Targets if needed.
- OneDrive cloud-only placeholders are logged and skipped rather than downloaded; if you need them in the backup, right-click -> "Always keep on this device" before running backup.

## License

MIT, see [LICENSE](LICENSE).

## Acknowledgements

Built from a detailed German specification (`docs/spec.md`, v2.0, 2026-04-22) authored by Sascha Haase. Iterative co-development with Claude Code over v0.1.0 -> v0.1.13.
