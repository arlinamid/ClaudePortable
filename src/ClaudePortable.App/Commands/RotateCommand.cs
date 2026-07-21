using System.CommandLine;
using System.Runtime.Versioning;
using ClaudePortable.App.Localization;
using ClaudePortable.Scheduler.Retention;
using ClaudePortable.Targets;

namespace ClaudePortable.App.Commands;

[SupportedOSPlatform("windows")]
public static class RotateCommand
{
    public static Command Build()
    {
        var inOption = new Option<DirectoryInfo>(
            aliases: new[] { "--in", "-i" },
            description: Loc.T("Cli_Rotate_In"))
        {
            IsRequired = true,
        };
        var dailyOption = new Option<int>(new[] { "--daily" }, () => 7, Loc.T("Cli_Rotate_Daily"));
        var weeklyOption = new Option<int>(new[] { "--weekly" }, () => 3, Loc.T("Cli_Rotate_Weekly"));
        var monthlyOption = new Option<int>(new[] { "--monthly" }, () => 2, Loc.T("Cli_Rotate_Monthly"));

        var cmd = new Command("rotate", Loc.T("Cli_Rotate_Desc"))
        {
            inOption, dailyOption, weeklyOption, monthlyOption,
        };

        cmd.SetHandler((folder, daily, weekly, monthly) =>
        {
            var target = new FolderTarget(folder.FullName);
            var policy = new RetentionPolicy(daily, weekly, monthly, DayOfWeek.Sunday);
            var manager = new RetentionManager(policy);
            var report = manager.Rotate(target);
            Console.WriteLine(Loc.F("Cli_Rotate_Promoted", report.Promoted.Count));
            foreach (var item in report.Promoted)
            {
                Console.WriteLine($"  {item}");
            }
            Console.WriteLine(Loc.F("Cli_Rotate_Pruned", report.Pruned.Count));
            foreach (var item in report.Pruned)
            {
                Console.WriteLine($"  {item}");
            }
            Console.WriteLine(Loc.F("Cli_Rotate_Counts", report.DailyAfter, report.WeeklyAfter, report.MonthlyAfter));
        }, inOption, dailyOption, weeklyOption, monthlyOption);

        return cmd;
    }
}
