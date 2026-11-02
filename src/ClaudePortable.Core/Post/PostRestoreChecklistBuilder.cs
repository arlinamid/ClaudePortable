using System.Globalization;
using ClaudePortable.Core.Abstractions;
using ClaudePortable.Core.Manifest;
using ClaudePortable.Core.Restore;

namespace ClaudePortable.Core.Post;

public static class PostRestoreChecklistBuilder
{
    public static string Build(
        BackupManifest manifest,
        VersionGateResult versionGate,
        IReadOnlyList<string> safetyBackups,
        IReadOnlyList<RestoreTargetReport> targetReports)
    {
        var sb = new System.Text.StringBuilder();

        sb.AppendLine("# Post-Restore Checklist\n");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Backup from: **{manifest.Hostname}** ({manifest.WindowsUser}) — {manifest.CreatedAt:yyyy-MM-dd HH:mm} UTC\n");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Claude Desktop version in backup: `{manifest.ClaudeDesktopVersion ?? "unknown"}`\n");

        // Version gate warning
        if (versionGate.Level == VersionGateLevel.Warn)
        {
            sb.AppendLine("## ⚠ Version Mismatch Detected\n");
            sb.AppendLine(CultureInfo.InvariantCulture, $"> **{versionGate.Message}**\n");
            sb.AppendLine("- [ ] Be aware that chat history migration may not be guaranteed due to LevelDB schema differences\n");
        }
        else if (versionGate.Level == VersionGateLevel.Block)
        {
            sb.AppendLine("## 🚫 Major Version Mismatch\n");
            sb.AppendLine(CultureInfo.InvariantCulture, $"> **{versionGate.Message}**\n");
            sb.AppendLine("- [ ] Chat history may be corrupted or unreadable — verify carefully\n");
        }

        // Required steps
        sb.AppendLine("## Required Steps\n");
        sb.AppendLine("- [ ] Start Claude Desktop and sign in once\n");
        sb.AppendLine("- [ ] Claude Code: run `claude login`\n");
        sb.AppendLine("- [ ] Re-authorize each connector (Gmail, Slack, GDrive, etc.)\n");
        if (manifest.SourcePaths.Keys.Any(k => k.StartsWith("codex", StringComparison.OrdinalIgnoreCase)))
        {
            sb.AppendLine("- [ ] Codex: run `codex login` or sign in once in the Codex app (`auth.json` is never backed up)\n");
            sb.AppendLine("- [ ] Codex: start it once so it re-downloads its runtimes, plugin cache and Windows sandbox setup\n");
        }

        // Dependencies outside the agent folders (MCP servers, hook scripts,
        // local marketplaces, link targets): what came back, and what the
        // user has to provide on this PC.
        var restoredExternal = manifest.ExternalPaths.Where(e => e.ArchivePrefix is not null).ToList();
        var missingExternal = manifest.ExternalPaths.Where(e => e.ArchivePrefix is null).ToList();
        if (restoredExternal.Count > 0)
        {
            sb.AppendLine("\n## External folders from the backup\n");
            sb.AppendLine("Folders outside the agent folders that their settings use. They were restored where nothing existed yet; dependencies such as `node_modules` or `.venv` are not in the backup, so reinstall them (`npm install`, `pip install -r requirements.txt`, ...):\n");
            foreach (var e in restoredExternal)
            {
                var git = e.GitRemote is null ? string.Empty : $" (restored without its git history; to get the repository back: `git clone {e.GitRemote}`)";
                sb.AppendLine(CultureInfo.InvariantCulture, $"- [ ] `{e.Path}` — used by {string.Join("; ", e.ReferencedBy)}{git}");
            }
            sb.AppendLine();
        }
        if (missingExternal.Count > 0)
        {
            sb.AppendLine("\n## Provide on this PC\n");
            sb.AppendLine("The agents' settings refer to these, but they were not backed up. Install or copy them, or update the setting:\n");
            foreach (var e in missingExternal)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"- [ ] `{e.Path}` — {e.SkipReason} (used by {string.Join("; ", e.ReferencedBy)})");
            }
            sb.AppendLine();
        }

        // Safety backups
        sb.AppendLine("\n## Safety Backups\n");
        if (safetyBackups.Count > 0)
        {
            sb.AppendLine("The following folders were backed up before restore:\n");
            foreach (var backup in safetyBackups)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"- `{backup}`\n");
            }
            sb.AppendLine("You can delete these after verifying the restore succeeded.\n");
        }
        else
        {
            sb.AppendLine("No existing folders were found — nothing was backed up before overlay.\n");
        }

        // Target reports
        if (targetReports.Count > 0)
        {
            sb.AppendLine("## Restore Summary\n");
            foreach (var report in targetReports)
            {
                var status = report.FilesWritten == 0 && report.Warnings.Count == 1 && report.Warnings[0].StartsWith("Not present", StringComparison.Ordinal)
                    ? "SKIPPED (not in backup)"
                    : report.SafetyBackedUp
                        ? $"RESTORED ({report.FilesWritten} files, safety backup created)"
                        : $"OVERLAY ({report.FilesWritten} files written)";
                sb.AppendLine(CultureInfo.InvariantCulture, $"### `{report.ArchivePrefix}`\n");
                sb.AppendLine(CultureInfo.InvariantCulture, $"Target: `{report.TargetFolder}`\n");
                sb.AppendLine(CultureInfo.InvariantCulture, $"Status: {status}\n");
                foreach (var warning in report.Warnings)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"> ⚠ {warning}\n");
                }
            }
        }

        // Troubleshooting
        sb.AppendLine("## Troubleshooting\n");
        sb.AppendLine("- **Chat history empty:** Check Claude Desktop version. Mismatch with backup version means chat history migration is not guaranteed.\n");
        sb.AppendLine("- **Plugins not loading:** Installed plugins are restored with their files; if one still fails, reinstall it with `claude plugin install <name>@<marketplace>`.\n");
        sb.AppendLine("- **Connectors not working:** Re-authorize. Tokens are intentionally not backed up.\n");

        // Scope note
        sb.AppendLine("\n## Scope Note\n");
        sb.AppendLine("This tool does NOT back up OAuth refresh tokens, API keys, or DPAPI-protected credentials.\n");
        sb.AppendLine("This is intentional: these artifacts are user- and machine-bound and would be invalid on another PC anyway.\n");

        return sb.ToString();
    }

    // Generic static checklist embedded in the backup zip at backup time
    // (see BackupEngine.BuildChecklistEntry). Restore regenerates a richer,
    // manifest-aware checklist via the overload above and ignores this copy;
    // it is kept so the archive stays self-describing on its own.
    public static string Build()
    {
        return """
            # Post-Restore Checklist

            Steps to complete manually after a restore finishes.

            ## Required Steps
            - [ ] Start Claude Desktop and sign in once
            - [ ] Claude Code: run `claude login`
            - [ ] Re-authorize each connector (Gmail, Slack, GDrive, etc.)
            - [ ] Codex (if included): run `codex login` or sign in once in the Codex app

            ## Safety Backups
            Existing folders are moved aside to `<folder>_backup_<timestamp>` before overlay.
            Delete them once you have verified the restore succeeded.

            ## Troubleshooting
            - **Chat history empty:** Check the Claude Desktop version. A mismatch with the backup version means chat-history migration is not guaranteed.
            - **Plugins not loading:** Installed plugins are restored with their files; if one still fails, reinstall it with `claude plugin install <name>@<marketplace>`.
            - **Connectors not working:** Re-authorize them. Tokens are intentionally not backed up.

            ## Scope Note
            This tool does NOT back up OAuth refresh tokens, API keys, or DPAPI-protected credentials.
            They are user- and machine-bound and would be invalid on another PC anyway.
            """;
    }
}
