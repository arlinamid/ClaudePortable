# AgentPortable

Windows desktop app (WPF + CLI) that backs up and restores your AI coding agents' local state - Claude Desktop, Cowork projects, Claude Code, the OpenAI Codex CLI and the Codex desktop app - as a single ZIP. The ZIP is written to any local folder - USB stick, OneDrive-synced folder, Google Drive Desktop mount, Dropbox, NAS share - and whatever sync client you already have takes it from there. **No cloud APIs, no OAuth registrations, no upload-resume logic.**

> **Formerly ClaudePortable.** Only the product name changed. The CLI is still `claudeportable.exe`, and app data (`%LOCALAPPDATA%\ClaudePortable`), sync folders (`<SyncClient>\ClaudePortable`), backup file names (`claude-backup_*.zip`) and scheduled task names (`ClaudePortable-Daily`) keep their old names, so existing backups, settings and tasks keep working after an upgrade.

> Status: alpha, usable. Tested roundtrip from a live workstation (~14 GB `.claude` + Cowork state) to a fresh laptop with a different Windows username, via OneDrive. See the [latest release](../../releases/latest) for the current build.

<img width="2208" height="1422" alt="restore complete" src="https://github.com/user-attachments/assets/18c10a51-dbe5-42f3-876d-8a866be66426" />


## What gets backed up

| Category | Location | Notes |
|---|---|---|
| Claude Desktop state | `%APPDATA%\Claude` (incl. IndexedDB, Local Storage, preferences, MCP config, installed extensions) | Reparse-point-aware: on Store installs the redirect to `%LOCALAPPDATA%\Packages\Claude_pzs8sxrjxfjjc\LocalCache\...` is followed transparently, with a direct-path fallback when the reparse lies. |
| Claude Desktop version | Read via `Get-AppxPackage` | Stored in the manifest; restore warns/blocks on major-version mismatch. |
| Cowork session metadata | `%APPDATA%\Claude\local-agent-mode-sessions\<guid>\...\.claude\` | Per-project memory (`CLAUDE.md`), settings, sessions, agents, plugins, skills. |
| **Cowork project folders** | Auto-discovered from `userSelectedFolders` in each session's `local_*.json` | Every folder you opened in a Cowork session is backed up to its own archive prefix with sensible project-noise exclusions (`node_modules`, `.git/objects`, `.venv`, etc.). |
| Claude Code user profile | `%USERPROFILE%\.claude` | Skills, plugins manifests, projects, sessions, settings, CLAUDE.md. |
| Claude Code plugins / skills | Under `.claude\plugins\` and `.claude\skills\` | Content preserved. Remote plugin binary cache (`.remote-plugins/`) is excluded; a fresh `claude plugin sync` after restore refills it. |
| **Codex CLI / app state** | `%CODEX_HOME%`, default `%USERPROFILE%\.codex` | `config.toml`, `AGENTS.md`, `sessions/` + `archived_sessions/` (conversation history), `skills/`, `rules/`, `agents/`, memories, `hooks.json`, `generated_images/`, and the sqlite state/thread-history databases. Excluded: `auth.json`, sandbox identity (`.sandbox*`, `cap_sid`, `installation_id`), downloaded binaries (`packages/`, `plugins/.plugin-appserver/`), plugin cache, logs, locks and temp folders. |
| Codex desktop app | `%APPDATA%\Codex` (Store package `OpenAI.Codex_*`, resolved by wildcard) | Preferences, Local Storage. The embedded browser profile (`web/`, cookies and site logins) and Chromium caches are excluded. |
| Shared agent skills | `%USERPROFILE%\.agents` | Skills installed with `npx skills add` live here and are linked into `.claude\skills`; Codex reads `.agents\skills` directly. |
| Links (junctions / symlinks) | Any linked folder inside the sources above | Recorded in the manifest, **not** followed. See [Links](#links-junctions-and-symlinks). |

Explicitly **not** in scope, on purpose:

- OAuth refresh tokens, API keys, `config.json` with `oauth:tokenCache`, DPAPI blobs, Claude Code `.credentials.json`, Codex `auth.json` - user re-authenticates connectors, Claude Code (`claude login`) and Codex (`codex login`) after restore. On a same-machine restore the existing Claude Code / Codex credentials and the Codex sandbox setup are carried over from the safety backup, so you stay signed in.
- `%USERPROFILE%\.claude.json` (Claude Code's global settings file next to the `.claude` folder, incl. user-scoped MCP servers). It also holds machine and account identifiers, so it is not restored over a fresh install; re-add user-scoped MCP servers with `claude mcp add --scope user`.
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

Launch with no arguments (or `--gui`). Warm-dark UI in the Claude Desktop style, WCAG 2.1 AA contrast throughout, visible keyboard focus rings. Six sections:

- **Status** - summary cards for backups / targets / discovered paths, plus a grid of existing snapshots per target.
- **Targets** - folder list. Auto-discovers `<SyncClient>\ClaudePortable` on every recognised sync client (OneDrive Personal / Business, Dropbox, Google Drive Desktop), so a restore on a second machine picks up the first machine's backups without configuration. Manual add/remove available.
- **Discovery** - read-only view of detected Claude + Codex paths and sync clients.
- **Restore** - backup grid with per-row `STATUS` (`Synced` / `Cloud-only` / `Unreadable`), `Restore from file...` escape hatch for a ZIP that is not in any configured target, and an **Advanced options** panel for overriding the target user profile (e.g. restoring a `sascha` backup onto a laptop with `sasch` as the user) and for the version-gate override.
- **Logs** - last 500 log lines from the current session, rendered mono.
- **Schedule** - enumerates every Windows scheduled task on this machine via `schtasks.exe /Query /FO CSV /V`. AgentPortable-managed entries are flagged green (name starts with `ClaudePortable-` / `AgentPortable-`, or the author contains either name). Tasks that aren't managed but touch a Claude/Cowork/`.claude`/`.codex` path - including hand-written backup PowerShell scripts that compete with AgentPortable - are flagged orange. Per-row buttons run/disable/enable/delete the task and copy its raw XML to the clipboard. Use this to spot legacy `\Claude-Desktop-Backup`-style tasks that write loose-file backups into a long-path OneDrive folder and break sync.

A ProgressBar on the status bar appears for the duration of any backup or restore, showing the current phase (`Extracting archive`, `Writing cowork-projects/<hash>`, etc.) with file-level percentage. Both commands run on the thread pool so the window stays responsive during multi-GB operations.

A tray icon keeps the app alive in the background; closing the window hides it, `Quit` in the tray menu actually exits.

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
claudeportable discover                                # detected Claude / Codex paths + sync clients
claudeportable backup   --to <folder> [--tier daily]   # create a backup ZIP (auto-rotates unless --no-rotate)
claudeportable list     --in <folder> [--json]         # list backups
claudeportable restore  --from <zip>  --yes [--target-user <path>] [--ignore-version-mismatch]
claudeportable rotate   --in <folder> [--daily 7] [--weekly 3] [--monthly 2]
claudeportable schedule install|show|remove|emit       # Windows Task Scheduler integration
claudeportable schedule list [--all|--managed|--relevant] [--json]  # enumerate all scheduled tasks, flag Claude relevance
claudeportable schedule disable|enable|run <name>       # toggle / trigger a scheduled task by full name
```

Exit codes: `0` ok, `1` usage error, `2` precondition fail (destination unwritable, Claude Desktop or Codex running), `3` runtime error (I/O, invalid backup, version block).

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

Auto-rotation runs after every successful backup: the newest daily of each Sunday promotes to weekly; the newest weekly of each finished month promotes to monthly. Prune rules keep 7 daily / 3 weekly / 2 monthly per folder target. Promotion renames instead of copying, so the sync client only uploads one delta per promotion.

Schedule yourself a recurring backup via the Windows Task Scheduler (`claudeportable schedule install --folder <path> --at 23:00`) or from the GUI tray icon.

## Cross-machine restore

The typical workflow across two machines:

1. Workstation runs `Backup now`. ZIP lands in `%USERPROFILE%\OneDrive\ClaudePortable\claude-backup_<ts>_<host>_daily.zip`.
2. OneDrive syncs to the laptop.
3. Laptop launches AgentPortable. Auto-discovery finds the ZIP in `<OneDrive>\ClaudePortable\`. The Status column tells you whether it is fully synced or still a cloud-only placeholder.
4. If the laptop's Windows username differs, open **Advanced options** on the Restore tab and pick the target user profile (`C:\Users\<other-user>`). The restore engine rewrites every reference in JSON and TOML configs (and the filesystem destination) from the old username to the new.
5. Claude Desktop must be closed on the laptop before restoring. If it is running, the app offers to close it. When the backup contains Codex data, the Codex app and any `codex` CLI sessions must be closed too.
6. Click **Restore selected snapshot**. Existing `.claude` and `%APPDATA%\Claude` content is moved aside to `<folder>_backup_<timestamp>` before the new data is written.
7. After completion, `claude login` and `codex login` on the laptop and re-authorise any connectors - token caches were deliberately excluded.

Where each folder is restored is decided **on the restore machine**: if Claude Desktop, Claude Code, Codex or `.agents` already has a data folder there (Store or non-Store install, a custom `%CODEX_HOME%`, redirected AppData), that folder is used. Otherwise the backup machine's path is re-rooted to the current profile. Paths inside JSON / TOML configs are rewritten from the backup machine's `%USERPROFILE%` (recorded in the manifest) to the new one, so profiles on another drive or outside `C:\Users` work too, e.g. `C:\Users\anna` -> `D:\Profiles\anna.CORP`.

Store-app reparse points (Claude Desktop from the Microsoft Store) refuse `Directory.Move` on their targets, so the restore engine detects them and overlays files instead of renaming. This is expected and logged as a single informational warning, not an error.

## Links (junctions and symlinks)

Skill and plugin folders are often links: `npx skills add` links `.claude\skills\<name>` to `.agents\skills\<name>`, and many people junction a skill to the git checkout they develop it in. Backups do **not** follow links inside a source, because the linked content lives elsewhere, can be huge, and can form cycles. Instead:

- **Backup** records every linked folder (`links` in `manifest.json`: archive path + original target) and prints it (`link (not followed, recreated on restore): ...`). Links inside excluded folders (`node_modules` etc.) are ignored. A source folder that is itself a link (e.g. `.claude` junctioned to a dotfiles repo) is still backed up normally. OneDrive / cloud-files folders are reparse points too but not links, so they are backed up normally.
- **Restore** recreates each link after all folders are written, when its target exists on the restore machine. Absolute targets under the old profile are re-rooted to the new one. Absolute local targets become junctions (no admin rights needed). Relative targets become symlinks when Developer Mode allows it, and junctions to the resolved path otherwise.
- If the target does **not** exist (e.g. `D:\src\my-skill` was never cloned on the new machine), restore lists a warning with the exact `mklink /J` command to run once you have restored that folder.

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
  claude-path-diff.ps1           # Before/after snapshot diff for Phase 0 research
```

`manifest.json` inside every ZIP lists:

- schema version, created-at, hostname, Windows user, Claude Desktop version, file count + size, SHA-256 over ordered content
- **`sourcePaths`**: key -> absolute path captured at backup time (includes all Cowork projects under `coworkProject:<hash>`)
- **`archiveTargets`**: archive-prefix -> absolute path, used by restore to reconstruct original locations
- `excludedPaths`: every glob applied (auditable; no surprises)

## Security model

- The app **never** archives OAuth tokens or credentials. `config.json` (contains `oauth:tokenCache`), `tokens.dat`, Codex `auth.json` and `.sandbox-secrets`, the Codex app's embedded browser profile, `Login Data*`, `Cookies*`, and `mcp-needs-auth-cache.json` are all explicitly excluded.
- Live Claude Desktop files are opened with `FileShare.ReadWrite | FileShare.Delete`; unreadable ones are logged and skipped rather than failing the whole backup.
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

174 xUnit cases cover exclusion globs (incl. Claude Extensions paths that must NOT be excluded), manifest (de)serialisation (incl. older manifests missing newer fields), path rewriter across escaped / single-backslash / forward-slash and arbitrary home-relative paths, profiles on other drives / outside `\Users` / with prefix-sharing or space-containing user names, junction recording and recreation (absolute, relative, missing target), Store package wildcard resolution, ZIP path-traversal rejection, retention rotation simulated over 10 weeks with a fake clock, FolderTarget atomic I/O, end-to-end backup roundtrip on synthetic data, Task Scheduler XML emission, version gating, and the scheduled-task enumerator (CSV parser for German-locale `schtasks.exe` output, Claude-relevance classifier, and command-shape assertions for the installer wrapper).

CI runs the same commands on `windows-latest` via `.github/workflows/ci.yml`. The release pipeline at `.github/workflows/release.yml` builds the MSI + portable exe + SHA-256 on `v*` tag push and attaches them to the GitHub Release.

Local portable-exe build:

```powershell
pwsh scripts/build-exe.ps1 -Version 0.2.0
```

Local MSI build (needs the WiX dotnet tool):

```powershell
dotnet tool install --global wix --version 7.0.0
wix eula accept wix7
pwsh src/ClaudePortable.Installer/build-msi.ps1 -Version 0.2.0
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
