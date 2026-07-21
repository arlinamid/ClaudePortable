using System.CommandLine;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;
using ClaudePortable.App.Localization;
using ClaudePortable.Scheduler.Scheduling;

namespace ClaudePortable.App.Commands;

[SupportedOSPlatform("windows")]
public static class ScheduleCommand
{
    public static Command Build()
    {
        var cmd = new Command("schedule", Loc.T("Cli_Schedule_Desc"));
        cmd.AddCommand(BuildInstall());
        cmd.AddCommand(BuildShow());
        cmd.AddCommand(BuildRemove());
        cmd.AddCommand(BuildEmit());
        cmd.AddCommand(BuildList());
        cmd.AddCommand(BuildDisable());
        cmd.AddCommand(BuildEnable());
        cmd.AddCommand(BuildRun());
        return cmd;
    }

    private static Command BuildInstall()
    {
        var folderOption = new Option<DirectoryInfo>(new[] { "--folder", "-f" }, Loc.T("Cli_Schedule_Folder")) { IsRequired = true };
        var timeOption = new Option<string>(new[] { "--at" }, () => "23:00", Loc.T("Cli_Schedule_At"));
        var nameOption = new Option<string>(new[] { "--name" }, () => "ClaudePortable-Daily", Loc.T("Cli_Schedule_Name"));
        var noInstallOption = new Option<bool>(new[] { "--no-install" }, () => false, Loc.T("Cli_Schedule_NoInstall"));
        var install = new Command("install", Loc.T("Cli_Schedule_Install_Desc"))
        {
            folderOption, timeOption, nameOption, noInstallOption,
        };

        install.SetHandler(async (folder, atRaw, taskName, noInstall) =>
        {
            if (!TimeOnly.TryParseExact(atRaw, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at))
            {
                Console.Error.WriteLine(Loc.F("Cli_Schedule_ErrParseAt", atRaw));
                Environment.ExitCode = 1;
                return;
            }

            var exe = Environment.ProcessPath
                ?? throw new InvalidOperationException("Could not determine current executable path.");
            var spec = new ScheduleSpec(
                TaskName: taskName,
                ExecutablePath: exe,
                Arguments: new[] { "backup", "--to", folder.FullName },
                DailyStart: at,
                Description: Loc.F("Cli_Schedule_TaskDescription", folder.FullName));

            var xml = TaskSchedulerEmitter.ToXml(spec, DateTimeOffset.UtcNow);
            var xmlPath = Path.Combine(
                Environment.ExpandEnvironmentVariables("%LOCALAPPDATA%"),
                "ClaudePortable",
                $"{taskName}.xml");
            Directory.CreateDirectory(Path.GetDirectoryName(xmlPath)!);
            await File.WriteAllTextAsync(xmlPath, xml).ConfigureAwait(false);
            Console.WriteLine(Loc.F("Cli_Schedule_Wrote", xmlPath));

            if (noInstall)
            {
                Console.WriteLine(Loc.T("Cli_Schedule_NoInstallSet"));
                return;
            }

            var installer = new TaskSchedulerInstaller();
            var exit = await installer.InstallAsync(taskName, xmlPath).ConfigureAwait(false);
            if (exit != 0)
            {
                Console.Error.WriteLine(Loc.F("Cli_Schedule_ErrCreate", exit));
                Environment.ExitCode = 3;
                return;
            }
            Console.WriteLine(Loc.F("Cli_Schedule_Installed", taskName, at.ToString("HH:mm", CultureInfo.InvariantCulture)));
        }, folderOption, timeOption, nameOption, noInstallOption);
        return install;
    }

    private static Command BuildShow()
    {
        var nameOption = new Option<string>(new[] { "--name" }, () => "ClaudePortable-Daily", Loc.T("Cli_Schedule_Name"));
        var show = new Command("show", Loc.T("Cli_Schedule_Show_Desc"))
        {
            nameOption,
        };
        show.SetHandler(async taskName =>
        {
            var installer = new TaskSchedulerInstaller();
            var (exit, output) = await installer.QueryAsync(taskName).ConfigureAwait(false);
            Console.Write(output);
            if (exit != 0)
            {
                Environment.ExitCode = 2;
            }
        }, nameOption);
        return show;
    }

    private static Command BuildRemove()
    {
        var nameOption = new Option<string>(new[] { "--name" }, () => "ClaudePortable-Daily", Loc.T("Cli_Schedule_Name"));
        var remove = new Command("remove", Loc.T("Cli_Schedule_Remove_Desc"))
        {
            nameOption,
        };
        remove.SetHandler(async taskName =>
        {
            var installer = new TaskSchedulerInstaller();
            var exit = await installer.DeleteAsync(taskName).ConfigureAwait(false);
            if (exit != 0)
            {
                Console.Error.WriteLine(Loc.F("Cli_Schedule_ErrDelete", exit));
                Environment.ExitCode = 3;
                return;
            }
            Console.WriteLine(Loc.F("Cli_Schedule_Deleted", taskName));
        }, nameOption);
        return remove;
    }

    private static Command BuildList()
    {
        var allOption = new Option<bool>(new[] { "--all" }, () => false, Loc.T("Cli_Schedule_List_All"));
        var managedOption = new Option<bool>(new[] { "--managed" }, () => false, Loc.T("Cli_Schedule_List_Managed"));
        var relevantOption = new Option<bool>(new[] { "--relevant" }, () => false, Loc.T("Cli_Schedule_List_Relevant"));
        var jsonOption = new Option<bool>(new[] { "--json" }, () => false, Loc.T("Cli_Schedule_List_Json"));
        var list = new Command("list", Loc.T("Cli_Schedule_List_Desc"))
        {
            allOption, managedOption, relevantOption, jsonOption,
        };
        list.SetHandler(async (all, managed, relevant, json) =>
        {
            var installer = new TaskSchedulerInstaller();
            var tasks = await installer.EnumerateAsync().ConfigureAwait(false);

            IEnumerable<ScheduledTaskInfo> filtered = tasks;
            if (managed)
            {
                filtered = filtered.Where(t => t.ManagedBy == ManagedBy.ClaudePortable);
            }
            else if (all)
            {
                // no filter
            }
            else
            {
                filtered = filtered.Where(t => t.ManagedBy is ManagedBy.ClaudePortable or ManagedBy.ForeignRelevant);
            }

            _ = relevant;

            var ordered = filtered
                .OrderBy(t => t.ManagedBy)
                .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (json)
            {
                var payload = new
                {
                    tasks = ordered.Select(t => new
                    {
                        name = t.Name,
                        fullName = t.FullName,
                        folderPath = t.FolderPath,
                        managedBy = t.ManagedBy.ToString(),
                        state = t.State,
                        author = t.Author,
                        nextRun = t.NextRunTime?.ToString("o", CultureInfo.InvariantCulture),
                        lastRun = t.LastRunTime?.ToString("o", CultureInfo.InvariantCulture),
                        lastResult = t.LastResult,
                        action = new
                        {
                            executable = t.Action.Executable,
                            arguments = t.Action.Arguments,
                            workingDirectory = t.Action.WorkingDirectory,
                        },
                        trigger = t.TriggerSummary,
                    }),
                };
                Console.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
                return;
            }

            if (ordered.Count == 0)
            {
                Console.WriteLine(Loc.T("Cli_Schedule_List_Empty"));
                return;
            }

            var headers = new[]
            {
                Loc.T("Grid_Name"),
                Loc.T("Cli_Hdr_ManagedBy"),
                Loc.T("Grid_State"),
                Loc.T("Cli_Hdr_NextRun"),
                Loc.T("Grid_Action"),
            };
            var rows = ordered.Select(t => new[]
            {
                t.FullName,
                t.ManagedBy.ToString(),
                t.State,
                t.NextRunTime?.LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "-",
                Truncate(string.IsNullOrEmpty(t.Action.Arguments) ? t.Action.Executable : $"{t.Action.Executable} {t.Action.Arguments}", 80),
            }).ToList();

            var widths = headers
                .Select((h, i) => Math.Max(h.Length, rows.Count == 0 ? 0 : rows.Max(r => r[i]?.Length ?? 0)))
                .ToArray();

            Console.WriteLine(string.Join("  ", headers.Select((h, i) => h.PadRight(widths[i]))));
            Console.WriteLine(string.Join("  ", widths.Select(w => new string('-', w))));
            foreach (var row in rows)
            {
                Console.WriteLine(string.Join("  ", row.Select((c, i) => (c ?? string.Empty).PadRight(widths[i]))));
            }
        }, allOption, managedOption, relevantOption, jsonOption);
        return list;
    }

    private static Command BuildDisable()
    {
        var nameArg = new Argument<string>("name", Loc.T("Cli_Schedule_NameArg"));
        var disable = new Command("disable", Loc.T("Cli_Schedule_Disable_Desc"))
        {
            nameArg,
        };
        disable.SetHandler(async taskName =>
        {
            var installer = new TaskSchedulerInstaller();
            var exit = await installer.DisableAsync(taskName).ConfigureAwait(false);
            if (exit != 0)
            {
                Console.Error.WriteLine(Loc.F("Cli_Schedule_ErrChangeDisable", exit));
                Environment.ExitCode = 3;
                return;
            }
            Console.WriteLine(Loc.F("Cli_Schedule_Disabled", taskName));
        }, nameArg);
        return disable;
    }

    private static Command BuildEnable()
    {
        var nameArg = new Argument<string>("name", Loc.T("Cli_Schedule_NameArg"));
        var enable = new Command("enable", Loc.T("Cli_Schedule_Enable_Desc"))
        {
            nameArg,
        };
        enable.SetHandler(async taskName =>
        {
            var installer = new TaskSchedulerInstaller();
            var exit = await installer.EnableAsync(taskName).ConfigureAwait(false);
            if (exit != 0)
            {
                Console.Error.WriteLine(Loc.F("Cli_Schedule_ErrChangeEnable", exit));
                Environment.ExitCode = 3;
                return;
            }
            Console.WriteLine(Loc.F("Cli_Schedule_Enabled", taskName));
        }, nameArg);
        return enable;
    }

    private static Command BuildRun()
    {
        var nameArg = new Argument<string>("name", Loc.T("Cli_Schedule_NameArg"));
        var run = new Command("run", Loc.T("Cli_Schedule_Run_Desc"))
        {
            nameArg,
        };
        run.SetHandler(async taskName =>
        {
            var installer = new TaskSchedulerInstaller();
            var exit = await installer.RunNowAsync(taskName).ConfigureAwait(false);
            if (exit != 0)
            {
                Console.Error.WriteLine(Loc.F("Cli_Schedule_ErrRun", exit));
                Environment.ExitCode = 3;
                return;
            }
            Console.WriteLine(Loc.F("Cli_Schedule_Triggered", taskName));
        }, nameArg);
        return run;
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..(max - 3)] + "...";

    private static Command BuildEmit()
    {
        var folderOption = new Option<DirectoryInfo>(new[] { "--folder", "-f" }, Loc.T("Cli_Schedule_Folder")) { IsRequired = true };
        var timeOption = new Option<string>(new[] { "--at" }, () => "23:00", Loc.T("Cli_Schedule_At"));
        var nameOption = new Option<string>(new[] { "--name" }, () => "ClaudePortable-Daily", Loc.T("Cli_Schedule_Name"));
        var emit = new Command("emit", Loc.T("Cli_Schedule_Emit_Desc"))
        {
            folderOption, timeOption, nameOption,
        };
        emit.SetHandler((folder, atRaw, taskName) =>
        {
            if (!TimeOnly.TryParseExact(atRaw, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at))
            {
                Console.Error.WriteLine(Loc.F("Cli_Schedule_ErrParseAt", atRaw));
                Environment.ExitCode = 1;
                return;
            }
            var exe = Environment.ProcessPath ?? "claudeportable.exe";
            var spec = new ScheduleSpec(
                TaskName: taskName,
                ExecutablePath: exe,
                Arguments: new[] { "backup", "--to", folder.FullName },
                DailyStart: at,
                Description: Loc.F("Cli_Schedule_TaskDescription", folder.FullName));
            Console.WriteLine(TaskSchedulerEmitter.ToXml(spec, DateTimeOffset.UtcNow));
        }, folderOption, timeOption, nameOption);
        return emit;
    }
}
