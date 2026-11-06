using System.CommandLine;
using System.Globalization;
using System.Runtime.Versioning;
using ClaudePortable.App.Localization;
using ClaudePortable.App.Logging;
using ClaudePortable.Core.Abstractions;
using ClaudePortable.Core.Archive;
using ClaudePortable.Core.Backup;
using ClaudePortable.Core.Discovery;
using ClaudePortable.Core.Manifest;
using ClaudePortable.Scheduler.Retention;
using ClaudePortable.Targets;

namespace ClaudePortable.App.Commands;

[SupportedOSPlatform("windows")]
public static class BackupCommand
{
    public static Command Build()
    {
        var toOption = new Option<DirectoryInfo>(
            aliases: new[] { "--to", "-t" },
            description: Loc.T("Cli_Backup_To"))
        {
            IsRequired = true,
        };

        var tierOption = new Option<RetentionTier>(
            aliases: new[] { "--tier" },
            description: Loc.T("Cli_Backup_Tier"),
            getDefaultValue: () => RetentionTier.Daily);

        var dryRunOption = new Option<bool>(
            aliases: new[] { "--dry-run" },
            description: Loc.T("Cli_Backup_DryRun"),
            getDefaultValue: () => false);

        var noRotateOption = new Option<bool>(
            aliases: new[] { "--no-rotate" },
            description: Loc.T("Cli_Backup_NoRotate"),
            getDefaultValue: () => false);

        var closeAppsOption = new Option<bool>(
            aliases: new[] { "--close-apps" },
            description: Loc.T("Cli_Backup_CloseApps"),
            getDefaultValue: () => false);

        var includeOption = GroupOptions.CreateInclude();
        var skipOption = GroupOptions.CreateSkip();

        var cmd = new Command("backup", Loc.T("Cli_Backup_Desc"))
        {
            toOption,
            tierOption,
            dryRunOption,
            noRotateOption,
            includeOption,
            skipOption,
            closeAppsOption,
        };

        cmd.SetHandler(async (toValue, tier, dryRun, noRotate, include, skip, closeApps) =>
        {
            if (!GroupOptions.TryResolve(include, skip, out var groups))
            {
                return;
            }

            var target = new FolderTarget(toValue.FullName);
            try
            {
                target.EnsureWritable();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine(Loc.F("Cli_Backup_ErrNotWritable", toValue.FullName, ex.Message));
                Environment.ExitCode = 2;
                return;
            }

            if (target.HasPendingCloudUploadFlags())
            {
                Console.Error.WriteLine(Loc.F("Cli_Backup_WarnCloudFlags", target.FolderPath));
            }

            // One run in the log (file + Logs tab history); warnings are
            // echoed to stderr as before.
            using var run = RunLog.Start("backup", target.FolderPath, Program.CliOrigin, echo: Console.Error);
            run.Info($"Command line: backup --to {target.FolderPath}{(dryRun ? " --dry-run" : string.Empty)}{(closeApps ? " --close-apps" : string.Empty)}{(noRotate ? " --no-rotate" : string.Empty)}");

            // Running desktop apps change their stores while they are copied.
            // With --close-apps they are closed (whole process tree, so the
            // background helpers too, and their services) and reopened after
            // the backup. Only a run from a console may ask for administrator
            // rights; a scheduled run has nobody to answer the prompt.
            var allowElevation = Program.CliOrigin == "cli";
            var closed = CloseOutcome.None;
            if (!dryRun)
            {
                var running = AgentProcesses.Find(AgentProcesses.KindsToClose(groups, AgentOperation.Backup));
                if (running.Count > 0 && closeApps)
                {
                    Console.WriteLine(Loc.F("Cli_Backup_ClosedApps", AgentLabels.Summarize(running)));
                    closed = await AgentProcesses.CloseAsync(running, TimeSpan.FromSeconds(5), log: run, allowElevation: allowElevation).ConfigureAwait(false);
                }
                else if (running.Count > 0)
                {
                    run.In(LogSections.RunningApps).Warn($"Running during the backup (pass --close-apps to close them): {AgentProcesses.Summarize(running)}");
                }
                else
                {
                    run.In(LogSections.RunningApps).Info("No Claude Desktop or Codex app running");
                }
            }

            try
            {
                await RunAsync(target, tier, dryRun, noRotate, groups, run).ConfigureAwait(false);
            }
            finally
            {
                if (closed.StoppedServices.Count > 0)
                {
                    await AgentServices.StartAsync(closed.StoppedServices, allowElevation, run).ConfigureAwait(false);
                }
                if (closed.Closed.Count > 0)
                {
                    AgentProcesses.Reopen(closed.Closed, run);
                    Console.WriteLine(Loc.F("Cli_Backup_Reopened", closed.Closed.Count));
                }
                run.Complete(Environment.ExitCode != 0 ? RunStatus.Failed : null);
            }
        }, toOption, tierOption, dryRunOption, noRotateOption, includeOption, skipOption, closeAppsOption);

        return cmd;
    }

    /// <summary>External folders the settings depend on: archived ones, then what was left out and why.</summary>
    private static void PrintExternal(BackupManifest manifest)
    {
        foreach (var e in manifest.ExternalPaths.Where(e => e.ArchivePrefix is not null))
        {
            Console.WriteLine(Loc.F("Cli_Backup_External", e.Path, string.Join("; ", e.ReferencedBy)));
        }
        foreach (var e in manifest.ExternalPaths.Where(e => e.ArchivePrefix is null))
        {
            Console.Error.WriteLine(Loc.F("Cli_Backup_ExternalSkipped", e.Path, e.SkipReason, string.Join("; ", e.ReferencedBy)));
        }
    }

    private static async Task RunAsync(FolderTarget target, RetentionTier tier, bool dryRun, bool noRotate, IReadOnlySet<string>? groups, RunLog run)
    {
        var engine = new BackupEngine(new WindowsPathDiscovery(), new ZipArchiveWriter(new OperationLogWriter(run)), log: run);
        BackupOutcome outcome;
        try
        {
            outcome = await engine.CreateBackupAsync(new(target.FolderPath, tier, dryRun, groups)).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Environment.ExitCode = 2;
            return;
        }

        if (dryRun)
        {
            Console.WriteLine(Loc.F("Cli_Backup_DryRunWouldCreate", outcome.ZipPath));
            Console.WriteLine(Loc.F("Cli_Backup_DryRunFiles", outcome.Manifest.FileCount));
            foreach (var (key, cnt) in outcome.FilesPerSource)
            {
                Console.WriteLine(Loc.F("Cli_Backup_DryRunSource", key, cnt));
            }
            foreach (var link in outcome.Manifest.Links)
            {
                Console.WriteLine(Loc.F("Cli_Backup_Link", link.Path, link.Target));
            }
            PrintExternal(outcome.Manifest);
            foreach (var skipped in outcome.SkippedPaths)
            {
                Console.Error.WriteLine(Loc.F("Cli_Backup_DryRunSkipped", skipped.Key, skipped.Path));
            }
            return;
        }

        Console.WriteLine(Loc.F("Cli_Backup_Created", outcome.ZipPath));
        Console.WriteLine(Loc.F("Cli_Backup_Files", outcome.Manifest.FileCount));
        Console.WriteLine(Loc.F("Cli_Backup_Bytes", outcome.Manifest.SizeBytes.ToString("N0", CultureInfo.CurrentCulture)));
        Console.WriteLine(Loc.F("Cli_Backup_Sha256", outcome.Manifest.Sha256));
        foreach (var (key, cnt) in outcome.FilesPerSource)
        {
            Console.WriteLine(Loc.F("Cli_Backup_PerSource", key, cnt));
        }
        foreach (var link in outcome.Manifest.Links)
        {
            Console.WriteLine(Loc.F("Cli_Backup_Link", link.Path, link.Target));
        }
        PrintExternal(outcome.Manifest);
        foreach (var skipped in outcome.SkippedPaths)
        {
            Console.Error.WriteLine(Loc.F("Cli_Backup_SkippedWarn", skipped.Key, skipped.Path));
        }

        if (noRotate)
        {
            return;
        }

        try
        {
            var manager = new RetentionManager();
            var report = manager.Rotate(target);
            RotationLog.Write(run, report);
            Console.WriteLine(Loc.F(
                "Cli_Rotation_Summary",
                report.Promoted.Count, report.Pruned.Count, report.DailyAfter, report.WeeklyAfter, report.MonthlyAfter));
            foreach (var item in report.Promoted)
            {
                Console.WriteLine(Loc.F("Cli_Rotation_Promoted", item));
            }
            foreach (var item in report.Pruned)
            {
                Console.WriteLine(Loc.F("Cli_Rotation_Pruned", item));
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            run.In(LogSections.Retention).Warn($"Rotation failed: {ex.Message}");
            Console.Error.WriteLine(Loc.F("Cli_Rotation_Failed", ex.Message));
        }
    }
}
