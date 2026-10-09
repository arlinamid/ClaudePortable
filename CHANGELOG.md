# Changelog

All notable changes to AgentPortable (formerly ClaudePortable) are documented
here. Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/)
and the project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.4.1] - 2026-10-09

### Fixed

- **Codex could not resume conversations after restoring onto a different
  user profile** (e.g. laptop `C:\Users\Janos` -> desktop
  `C:\Users\János`). The error was "failed to resolve rollout path
  C:\Users\Janos\.codex\sessions\...jsonl: file does not exist". Codex
  keeps every conversation's absolute file path, working folder and sandbox
  roots in SQLite (`state_5.sqlite`, `threads.rollout_path` etc.) and in its
  session `.jsonl` files, and restore only rewrote `.json` / `.toml`. Restore
  now also rewrites:
  - every text value in SQLite databases (`*.sqlite`, `*.sqlite3`, `*.db`),
    using the same path rules (whole profile root, `\\?\` prefixes,
    JSON-escaped values, prefix-safe: `Janos` never matches `Janosik`).
    The staging copy is rewritten before it is copied into place.
  - `*.jsonl` session files (Codex and Claude Code)
  - user names stored with JSON `\uXXXX` escapes (`J\u00e1nos`)
- **Claude Code history disappeared after restoring onto a different user
  profile.** Claude Code names `~\.claude\projects\<folder>` after the
  project path (`C:\Users\János\proj` -> `C--Users-J-nos-proj`). Restore
  now renames these folders for the new profile.

- **Backup could hang forever at "Checking accessibility".** One file whose
  open never returned stopped the whole backup, with no hint which file. On a
  user's machine that can be a OneDrive / cloud-files placeholder whose
  download cannot complete, a file held by another program or filter driver,
  or a link to an unreachable network share. The ZIP writer now:
  - skips cloud-only placeholders without opening them (the README always
    said so; nothing actually checked)
  - abandons an open after 30 s and a read that delivers no data for 60 s
  - names every skipped file in the Logs tab (GUI) / stderr (CLI)
  - the status bar says how many files were skipped
- **Backups read every file once instead of three times.** The separate
  accessibility check and hashing passes are gone: files are streamed into
  the ZIP and hashed on the way, and `manifest.json` is written as the last
  entry. The content hash is now SHA-256 over each file's path and its own
  SHA-256; it is informational only, and nothing verifies against the old
  definition.
- A failed or cancelled backup no longer leaves a `.tmp` ZIP in the target
  folder.

### Added

- **Cancel** button for a running backup (next to the progress bar).

## [0.4.0] - 2026-10-09

### Changed

- **Renamed to AgentPortable.** Window title, tray icon, MSI product name,
  Start-menu shortcut, release artifacts (`AgentPortable-<version>-portable.exe`
  / `.msi`) and all UI/CLI texts (English + Hungarian) use the new name. For
  upgrade compatibility the CLI stays `claudeportable.exe`, and the install
  folder, `%LOCALAPPDATA%\ClaudePortable`, `<SyncClient>\ClaudePortable`,
  `claude-backup_*.zip` and `ClaudePortable-Daily` task names are unchanged.
  Tasks named `AgentPortable-*` are also recognised as managed.
- **Links are recorded, not followed.** Junctions and directory symlinks
  inside a backup source (e.g. `.claude\skills\x -> .agents\skills\x` from
  `npx skills add`, or a skill junctioned to a git checkout) used to be
  copied into the ZIP wholesale and restored as real folders, and a link
  cycle could recurse until the path got too long. They are now stored in
  the manifest's new `links` list and recreated after all folders are
  restored: a junction for absolute local targets (no admin rights needed),
  otherwise a symlink, with a warning and the exact `mklink /J` command when
  the target does not exist on the restore machine. Folders matched by a
  whole-subtree exclusion (`node_modules`, caches, ...) are now skipped
  without being listed, which speeds up backups of large Cowork projects.
- **Restore targets are resolved on the restore machine.** When Claude
  Desktop, Claude Code, Codex or `.agents` already has a data folder there
  (Store vs. non-Store install, custom `%CODEX_HOME%`, redirected AppData),
  that folder is used instead of the backup machine's path.
- The Claude Desktop Store package folder is matched as `Claude_*` instead of
  one hard-coded publisher id, like `OpenAI.Codex_*`.

- **Choose what to back up and restore.** Backups and restores can be
  limited to source groups: `claude-desktop`, `cowork`, `claude-code` and
  `codex` (aliases `claude`, `all`). The shared `.agents` skill store goes
  with either agent.
  - CLI: `--include` / `--skip` on `backup`, `restore`, `schedule install` and
    `schedule emit`. The scheduled task stores the selection in its command
    line.
  - GUI: **What to back up** checkboxes on the Status page (saved in
    `settings.json`, used by *Backup now* and the automatic backup task),
    **What to restore** checkboxes on the Restore page, a **Contents** column
    in both backup lists, and friendly source names on the Discovery page.
  - Partial backups are tagged in the file name
    (`..._<host>_codex_daily.zip`) and in the manifest (`groups`).
  - Restore asks to close Codex (as it already did for Claude Desktop), but
    only for apps whose data is actually being restored. The Claude Desktop
    version check no longer blocks restores that skip Claude Desktop.

- **Accessibility (WCAG 2.1 AA).**
  - Headings are exposed as level-1 / level-2 headings.
  - The status bar and Schedule status line are live regions.
  - Text boxes, grids, lists, the language selector, the progress bar and
    the ⋮ row button all have accessible names.
  - Scheduled tasks gained a TYPE column, so managed / related / other is no
    longer shown by colour alone.
  - Read-only status columns show Yes / No instead of disabled check boxes.
  - New `scripts/a11y-verify.ps1` checks the UI Automation tree of a running
    build.

### Fixed

- **Screen readers could not see any page content.** The page area hid its
  tab headers, and WPF's TabControl exposes pages to UI Automation only
  through those headers. Narrator / NVDA saw the sidebar and nothing else.
  The page area is now a `PageHost` that exposes the visible page directly,
  and sidebar items are announced by their label instead of
  "System.Windows.Controls.ListBoxItem".
- **Contrast fixes.** Text box / combo box outlines went from 2.0:1 to
  3.6:1 (`ControlBorderColor` `#8A877F`). The white label on the pressed
  primary button went from 4.3:1 to 5.2:1. The primary button's focus ring
  is drawn outside the coral fill (1.8:1 on the fill, 8.7:1 on the page).
- **Retention could delete other machines' backups.** Rotation counted
  every backup in a folder together, so two PCs syncing into the same
  OneDrive folder pruned each other's backups (and partial backups would
  have pruned full ones). Retention now rotates each machine and selection
  separately.
- **CLI exit codes were always 0.** Failures reported through
  `Environment.ExitCode` were overwritten by `Main`'s return value, so
  scripts and Task Scheduler saw failed backups and restores as successful.
- **Claude Code's OAuth tokens were included in backups.**
  `.claude\.credentials.json` is now excluded, as the README always promised,
  and carried over from the safety backup on a same-machine restore.
- Path rewriting on restore only swapped the user name inside `X:\Users\<name>`
  paths. It now rewrites the whole backup-machine profile root, which the
  manifest records as `userProfile`. Profiles on another drive or outside
  `\Users` (`D:\Profiles\anna.CORP`) are therefore handled. A user name that
  prefixes another one (`sam` / `samantha`) or contains a space is not
  rewritten by mistake.
- Manifests from older versions had `null` collections after loading,
  because the JSON source generator assigns missing init-only properties.
  For example, `archiveTargets` was null in pre-0.1.13 backups, which broke
  restore. These collections now always load as empty.
- Restore rejects ZIP entries that would be extracted outside the temp
  folder (`../`, absolute paths).
- Cowork project discovery now finds sessions through the same Store-app
  reparse fallback as the main discovery, instead of assuming
  `%APPDATA%\Claude` is readable.

### Added

- **`%USERPROFILE%\.agents` backup** (`agents/dotagents`): the shared skill
  store used by `npx skills` and read by Codex as its user skills folder.
  Without it, linked skills would be lost on a new machine now that links
  are no longer followed.

- **Codex backup.** The OpenAI Codex state root (`%CODEX_HOME%`, default
  `%USERPROFILE%\.codex`) is backed up under `codex/dotcodex`: `config.toml`,
  `AGENTS.md`, sessions and archived sessions, skills, rules, agents,
  memories, hooks, generated images and the sqlite state databases. The Codex
  desktop app's data (`%APPDATA%\Codex`, Store package `OpenAI.Codex_*`) is
  backed up under `codex-desktop/appdata`. Credentials (`auth.json`,
  `.sandbox-secrets`), machine-bound sandbox identity, downloaded binaries
  (`packages/`, `plugins/.plugin-appserver/`), logs, locks, caches and the
  app's embedded browser profile are excluded.
  - Restore refuses to start while Codex is running if the backup contains
    Codex data, rewrites user-profile paths in `*.toml` (Codex
    `config.toml`) as well as `*.json`, and on a same-machine restore copies
    `auth.json` and the sandbox setup back from the safety backup so you stay
    signed in.
  - The post-restore checklist adds `codex login` steps; the Schedule view
    flags tasks touching `.codex` as relevant.
  - SQLite `*-shm` files are now excluded everywhere (always regenerated).

- **Single-instance GUI.** A second launch of the window/tray app activates the
  already-running instance instead of starting another process. CLI commands
  (`backup`, `schedule`, …) are unaffected so scheduled tasks can still run
  while the GUI is open.
- **Full Hungarian + English localization (i18n) for the GUI and the CLI.**
  All user-facing strings moved into `Strings.resx` (English, neutral) /
  `Strings.hu.resx` (Hungarian) with a runtime-switchable `Loc` service and a
  `{loc:Loc Key}` XAML markup extension, so the whole window - navigation,
  page captions, grid headers, buttons, dialogs, tray menu, status texts -
  re-renders live when the language changes. Language resolution order:
  explicit CLI `--lang en|hu` > choice saved from the GUI
  (`%LOCALAPPDATA%\ClaudePortable\settings.json`) > Windows display language
  (Hungarian Windows -> `hu`, everything else -> `en`).
  - GUI: new language selector (English / Magyar) in the sidebar footer;
    the choice persists across restarts.
  - CLI: new global `--lang en|hu` option; every command/option description
    and human-readable output line is localized. CLI output is written as
    UTF-8 so accented characters survive legacy console code pages.
  - Intentionally left in English: `--json` payloads (machine-readable),
    diagnostic log lines (Logs tab and `claudeportable-*.log`), and
    System.CommandLine's built-in help/version boilerplate.
  - New `LocalizationParityTests` guard the catalogs: every English key must
    exist in Hungarian (and vice versa) with matching `{n}` placeholders.
- **Automatic-backup setup in the GUI.** The Schedule page gained an
  "Automatic backup" card - daily time (HH:mm) + task name + one
  "Install / update task" button - that registers the same Task Scheduler
  entry as `claudeportable schedule install`, targeting the active target
  folder. Re-installing under the same name replaces the task.
- **App icon.** New branded icon (accent rounded square with a suitcase +
  up-arrow glyph) embedded in the exe and used by the window title bar,
  taskbar, and tray icon (extracted from the exe at runtime, so the
  single-file build needs no loose asset).

### Fixed

- **Schedule row actions were unreachable / unreadable.** Five inline
  Run/Disable/Enable/XML/Delete buttons sat in trailing DataGrid columns and
  scrolled off typical widths. Replaced with a frozen ⋮ menu (Run / Disable /
  Enable / XML / Delete). Dark `ContextMenu` / `MenuItem` styles so the popup
  is readable on the warm-dark palette (system light chrome previously left
  white text on a white menu).
- **Language dropdown was unreadable.** The default WPF ComboBox template
  renders a light-chrome popup with system highlight colors, which produced
  white-on-white items on the warm-dark palette. Added fully retemplated
  `DarkCombo` / `DarkComboItem` styles (dark closed control, dark popup,
  hover + selection states from the app palette) plus a shared `DarkTextBox`
  style now also used by the Restore override field and the new
  auto-backup inputs.

## [0.3.2] - 2026-06-03

### Fixed

- **Restore button stayed disabled after selecting a snapshot.** On the Restore
  tab, "Restore selected snapshot" is gated on a backup being selected, but
  `SelectedBackup` raised no change notification and `AsyncRelayCommand` does not
  hook `CommandManager.RequerySuggested`, so its `CanExecute` was evaluated once
  (nothing selected -> disabled) and never re-queried when a row was picked - the
  button was therefore permanently greyed out. `SelectedBackup` is now a notifying
  property that raises `RestoreCommand` `CanExecuteChanged`, matching the existing
  `SelectedTarget` fix. "Restore from file..." was unaffected and was the
  workaround on 0.3.1.

## [0.3.1] - 2026-06-01

### Fixed

- **Discovery no longer shows a permanently-empty `coworkSessions` row.**
  The `%USERPROFILE%\.cowork` candidate was an unverified Spec 1.1 guess that
  does not exist on the Store-app Claude Desktop, so its read-only EXISTS box
  always rendered unchecked and looked like a broken toggle. Removed the
  vestigial entry from `WindowsPathDiscovery` and its `cowork/sessions`
  archive-prefix mapping. Cowork data is unaffected: session state under
  `%APPDATA%\Claude\local-agent-mode-sessions` is already covered by the
  Claude Desktop AppData path, and project folders are captured by
  `CoworkProjectDiscovery` under `cowork-projects/<hash>`. The Discovery
  caption now explains that EXISTS is a status indicator, not a toggle. The
  restore-side legacy mapping for `cowork/sessions` is retained so older ZIPs
  still restore correctly.

## [0.3.0] - 2026-05-29

### Added

- **Serilog file logging.** CLI and GUI write a rolling daily log to
  `%LOCALAPPDATA%\ClaudePortable\logs\claudeportable-.log` (30-day
  retention). The GUI Logs tab still mirrors live output.
- **Dynamic post-restore checklist** generated from the restore result -
  version-gate warnings, `.claude/plugins` reinstall hints, safety-backup
  paths, and a per-target restore summary - written to
  `%LOCALAPPDATA%\ClaudePortable\post-restore-checklist-<timestamp>.md`, with
  an "Open Checklist" button in the Restore tab after a restore completes.
- **`scripts/e2e-verify.ps1`** - automated backup-ZIP verification: manifest
  schema and required fields, expected content directories, credential
  exclusions (`tokens.dat`, `Login Data*`, `Cookies*`,
  `claude-desktop/appdata/config.json`), MCP-server key comparison, and
  post-restore checklist section checks.

### Changed

- Post-restore checklist text is now English (was German).
- `scripts/build-exe.ps1` and `src/ClaudePortable.Installer/build-msi.ps1`
  default version bumped from `0.2.0` to `0.3.0`.

### Fixed

- The logging/checklist/e2e work did not compile; restored a buildable
  state - added the real Serilog sink packages (`Serilog.Sinks.File`,
  `Serilog.Sinks.Console`), a missing `Program` class brace, `CA1305`
  format-provider arguments, an unresolved `RestoreTargetReport` import, the
  `StringToVisibleConverter` `System.Windows` import, and a parameterless
  `PostRestoreChecklistBuilder.Build()` overload so the backup path compiles.
- `scripts/e2e-verify.ps1` correctness - assert the manifest's content hash
  is a valid digest (it is not the zip-file hash), match credential
  exclusions by file name (`-Filter **/...` matched nothing and always
  passed), and count manifest dictionary keys correctly.

## [0.2.0] - 2026-05-10

### Added

- **Schedule sidebar section** that enumerates every Windows scheduled task
  via `schtasks.exe /Query /FO CSV /V` and flags Claude relevance with a
  three-tier classifier (green = ClaudePortable-managed, orange =
  foreign-but-Claude-related, gray = unrelated). Use it to spot a legacy
  `\Claude-Desktop-Backup`-style PowerShell task that writes loose-file
  backups into a long-path OneDrive folder and breaks sync.
- Per-row Run / Disable / Enable / Delete / View XML buttons in the new
  Schedule view, with confirmation dialogs on destructive actions and
  clipboard copy of the raw Task Scheduler XML.
- CLI: `claudeportable schedule list [--all|--managed|--relevant]
  [--json]` to enumerate tasks from the terminal, plus
  `schedule disable|enable|run <name>` for symmetry with the new GUI
  buttons.
- `ScheduledTaskClassifier` in `ClaudePortable.Scheduler` with a stable,
  testable marker list (`.claude`, `Claude_pzs8sxrjxfjjc`, `Cowork`,
  `CoWork\Backup`, `local-agent-mode-sessions`, `claude-desktop`,
  `anthropic`, etc.).
- `TaskSchedulerInstaller.EnumerateAsync` / `DisableAsync` /
  `EnableAsync` / `RunNowAsync` / `GetTaskXmlAsync`, all going through a
  single internal `Func<>` seam so unit tests can assert exact `schtasks`
  argv without invoking the executable.
- Fixture-driven CSV-parser tests covering German `schtasks` headers,
  quoted-path executables (`"C:\Program Files\..."`), unquoted paths with
  spaces, subfolder task names, disabled state, and `ManagedBy`
  classification.

### Changed

- README upgraded from "Five sections" to "Six sections" GUI overview and
  refreshed test count (61 -> 98 xUnit cases).
- `scripts/build-exe.ps1` and `src/ClaudePortable.Installer/build-msi.ps1`
  default version bumped from `0.1.x` to `0.2.0`.

### Not changed (out of scope, intentionally)

- ClaudePortable does NOT auto-disable or auto-delete any task it finds.
  The user explicitly clicks each action; confirmation dialogs gate
  Disable and Delete.
- The existing backup engine, restore engine, and archive format are
  untouched. A 0.1.x backup ZIP restores identically on 0.2.0.

## [0.1.x]

Initial alpha releases focused on backup, restore, retention rotation,
sync-client discovery, and a single-task scheduler install command. See
git history and the GitHub Releases page for per-version detail.
