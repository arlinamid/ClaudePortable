using ClaudePortable.Core.Abstractions;
using ClaudePortable.Scheduler.Retention;

namespace ClaudePortable.App.Logging;

/// <summary>Retention results in the "Retention" section of a backup run.</summary>
public static class RotationLog
{
    public static void Write(IOperationLog log, RotationReport report)
    {
        var retention = log.In(LogSections.Retention);
        retention.Info($"Kept: {report.DailyAfter} daily, {report.WeeklyAfter} weekly, {report.MonthlyAfter} monthly");
        foreach (var item in report.Promoted)
        {
            retention.Info($"Promoted {item}");
        }
        foreach (var item in report.Pruned)
        {
            retention.Info($"Removed old backup {item}");
        }
    }
}
