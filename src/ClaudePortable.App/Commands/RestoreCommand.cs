using System.CommandLine;
using System.Globalization;
using System.Runtime.Versioning;
using ClaudePortable.App.Localization;
using ClaudePortable.Core.Restore;

namespace ClaudePortable.App.Commands;

[SupportedOSPlatform("windows")]
public static class RestoreCommand
{
    public static Command Build()
    {
        var fromOption = new Option<FileInfo>(
            aliases: new[] { "--from", "-f" },
            description: Loc.T("Cli_Restore_From"))
        {
            IsRequired = true,
        };

        var yesOption = new Option<bool>(
            aliases: new[] { "--yes", "-y" },
            description: Loc.T("Cli_Restore_Yes"),
            getDefaultValue: () => false);

        var targetUserOption = new Option<string?>(
            aliases: new[] { "--target-user" },
            description: Loc.T("Cli_Restore_TargetUser"));

        var ignoreVersionOption = new Option<bool>(
            aliases: new[] { "--ignore-version-mismatch" },
            description: Loc.T("Cli_Restore_IgnoreVersion"),
            getDefaultValue: () => false);

        var cmd = new Command("restore", Loc.T("Cli_Restore_Desc"))
        {
            fromOption,
            yesOption,
            targetUserOption,
            ignoreVersionOption,
        };

        cmd.SetHandler(async (from, yes, targetUser, ignoreVersion) =>
        {
            if (!yes)
            {
                Console.Error.WriteLine(Loc.T("Cli_Restore_NeedYes"));
                Environment.ExitCode = 1;
                return;
            }

            var engine = new RestoreEngine(new PathRewriter());
            try
            {
                var outcome = await engine.RestoreAsync(new(from.FullName, targetUser, Confirmed: true, IgnoreVersionMismatch: ignoreVersion)).ConfigureAwait(false);
                Console.WriteLine(Loc.T("Cli_Restore_Complete"));
                Console.WriteLine(Loc.F("Cli_Restore_ManifestSchema", outcome.Manifest.SchemaVersion));
                Console.WriteLine(Loc.F("Cli_Restore_CreatedAt", outcome.Manifest.CreatedAt.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)));
                Console.WriteLine(Loc.F("Cli_Restore_OriginalHost", outcome.Manifest.Hostname));
                Console.WriteLine(Loc.F("Cli_Restore_VersionGate", outcome.VersionGate.Level, outcome.VersionGate.Message));
                Console.WriteLine(Loc.F("Cli_Restore_SafetyBackups", outcome.SafetyBackups.Count));
                foreach (var sb in outcome.SafetyBackups)
                {
                    Console.WriteLine($"  - {sb}");
                }
                Console.WriteLine(Loc.F("Cli_Restore_Checklist", outcome.PostRestoreChecklistPath));
            }
            catch (FileNotFoundException ex)
            {
                Console.Error.WriteLine(Loc.F("Cli_Restore_ErrGeneric", ex.Message));
                Environment.ExitCode = 2;
            }
            catch (InvalidDataException ex)
            {
                Console.Error.WriteLine(Loc.F("Cli_Restore_ErrInvalid", ex.Message));
                Environment.ExitCode = 3;
            }
            catch (InvalidOperationException ex)
            {
                Console.Error.WriteLine(Loc.F("Cli_Restore_ErrGeneric", ex.Message));
                Environment.ExitCode = 3;
            }
        }, fromOption, yesOption, targetUserOption, ignoreVersionOption);

        return cmd;
    }
}
