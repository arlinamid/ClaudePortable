using System.CommandLine;
using System.Globalization;
using System.Runtime.Versioning;
using ClaudePortable.App.Localization;
using ClaudePortable.Core.Discovery;
using ClaudePortable.Core.Restore;

namespace ClaudePortable.App.Commands;

/// <summary>
/// `repair-paths`: point Codex / Claude Code state that still references
/// another user's profile (after a restore onto a differently named profile)
/// at the current profile, in place.
/// </summary>
[SupportedOSPlatform("windows")]
public static class RepairPathsCommand
{
    public static Command Build()
    {
        var fromOption = new Option<string[]>(new[] { "--from" }, Loc.T("Cli_Repair_From"))
        {
            AllowMultipleArgumentsPerToken = true,
        };
        var dryRunOption = new Option<bool>(new[] { "--dry-run" }, () => false, Loc.T("Cli_Repair_DryRun"));
        var yesOption = new Option<bool>(new[] { "--yes", "-y" }, () => false, Loc.T("Cli_Repair_Yes"));

        var cmd = new Command("repair-paths", Loc.T("Cli_Repair_Desc"))
        {
            fromOption, dryRunOption, yesOption,
        };

        cmd.SetHandler((from, dryRun, yes) =>
        {
            var current = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var folders = ProfilePathRepair.AgentFolders(new WindowsPathDiscovery());
            Console.WriteLine(Loc.F("Cli_Repair_Current", current));

            var profiles = from is { Length: > 0 }
                ? from.Select(f => new ProfilePathRepair.ForeignProfile(f.TrimEnd('\\', '/'), 0)).ToList()
                : ProfilePathRepair.Detect(folders.Select(f => f.Folder), current);

            if (profiles.Count == 0)
            {
                Console.WriteLine(Loc.T("Cli_Repair_NothingFound"));
                return;
            }
            foreach (var p in profiles)
            {
                Console.WriteLine(Loc.F("Cli_Repair_Found", p.ProfileRoot, p.Occurrences, current));
            }
            if (dryRun)
            {
                return;
            }
            if (!yes)
            {
                Console.Error.WriteLine(Loc.T("Cli_Repair_NeedYes"));
                Environment.ExitCode = 1;
                return;
            }
            var codex = System.Diagnostics.Process.GetProcessesByName("Codex");
            if (codex.Length > 0)
            {
                Console.Error.WriteLine(Loc.F("Cli_Repair_CodexRunning", string.Join(", ", codex.Select(p => p.Id))));
                Environment.ExitCode = 2;
                return;
            }

            var backupRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ClaudePortable",
                $"path-repair-{DateTime.UtcNow.ToString("yyyy-MM-dd-HHmmss", CultureInfo.InvariantCulture)}");
            var result = ProfilePathRepair.Repair(folders, profiles, current, backupRoot);

            Console.WriteLine(Loc.F("Cli_Repair_Done", result.FilesChanged, result.ValuesChanged));
            if (result.OriginalsBackupFolder is not null)
            {
                Console.WriteLine(Loc.F("Cli_Repair_Originals", result.OriginalsBackupFolder));
            }
            foreach (var w in result.Warnings)
            {
                Console.Error.WriteLine($"warning: {w}");
            }
        }, fromOption, dryRunOption, yesOption);

        return cmd;
    }
}
