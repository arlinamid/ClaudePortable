using System.CommandLine;
using System.Globalization;
using System.Runtime.Versioning;
using ClaudePortable.App.Localization;
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

        var cmd = new Command("backup", Loc.T("Cli_Backup_Desc"))
        {
            toOption,
            tierOption,
            dryRunOption,
            noRotateOption,
        };

        cmd.SetHandler(async (toValue, tier, dryRun, noRotate) =>
        {
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

            var engine = new BackupEngine(new WindowsPathDiscovery(), new ZipArchiveWriter());
            var outcome = await engine.CreateBackupAsync(new(target.FolderPath, tier, dryRun)).ConfigureAwait(false);

            if (dryRun)
            {
                Console.WriteLine(Loc.F("Cli_Backup_DryRunWouldCreate", outcome.ZipPath));
                Console.WriteLine(Loc.F("Cli_Backup_DryRunFiles", outcome.Manifest.FileCount));
                foreach (var (key, cnt) in outcome.FilesPerSource)
                {
                    Console.WriteLine(Loc.F("Cli_Backup_DryRunSource", key, cnt));
                }
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
                Console.Error.WriteLine(Loc.F("Cli_Rotation_Failed", ex.Message));
            }
        }, toOption, tierOption, dryRunOption, noRotateOption);

        return cmd;
    }
}
