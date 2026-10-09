using System.Runtime.Versioning;
using ClaudePortable.Core.Archive;
using ClaudePortable.Core.Manifest;
using ClaudePortable.Targets;

namespace ClaudePortable.Scheduler.Retention;

[SupportedOSPlatform("windows")]
public sealed class RetentionManager
{
    private readonly RetentionPolicy _policy;
    private readonly TimeProvider _clock;

    public RetentionManager(RetentionPolicy? policy = null, TimeProvider? clock = null)
    {
        _policy = policy ?? RetentionPolicy.Default;
        _clock = clock ?? TimeProvider.System;
    }

    public RotationReport Rotate(FolderTarget target)
    {
        var backups = target.ListBackups()
            .Where(b => b.Manifest is not null)
            .ToList();

        if (backups.Count == 0)
        {
            return RotationReport.Empty;
        }

        // Rotate each (machine, selection) set on its own. A sync folder is
        // often shared by several PCs, and a user may mix full backups with
        // Claude-only or Codex-only ones; pooling them would let one set's
        // dailies prune another set's only copies.
        var promoted = new List<string>();
        var pruned = new List<string>();
        foreach (var partition in backups.Select(PartitionKey).Distinct(StringComparer.OrdinalIgnoreCase).ToList())
        {
            RotatePartition(target, partition, promoted, pruned);
        }

        var final = target.ListBackups().Where(b => b.Manifest is not null).ToList();
        return new RotationReport(
            promoted,
            pruned,
            DailyAfter: final.Count(b => b.Manifest!.RetentionTier == RetentionTier.Daily),
            WeeklyAfter: final.Count(b => b.Manifest!.RetentionTier == RetentionTier.Weekly),
            MonthlyAfter: final.Count(b => b.Manifest!.RetentionTier == RetentionTier.Monthly));
    }

    private void RotatePartition(FolderTarget target, string partition, List<string> promoted, List<string> pruned)
    {
        List<BackupDescriptor> Load() => target.ListBackups()
            .Where(b => b.Manifest is not null
                && string.Equals(PartitionKey(b), partition, StringComparison.OrdinalIgnoreCase))
            .ToList();

        PromoteToWeekly(target, Load(), promoted);
        PromoteToMonthly(target, Load(), promoted);

        var backups = Load();
        PruneTier(target, backups, RetentionTier.Daily, _policy.DailyKeep, pruned);
        PruneTier(target, backups, RetentionTier.Weekly, _policy.WeeklyKeep, pruned);
        PruneTier(target, backups, RetentionTier.Monthly, _policy.MonthlyKeep, pruned);
    }

    internal static string PartitionKey(BackupDescriptor backup)
        => $"{backup.Manifest!.Hostname}|{string.Join('+', SourceGroups.SelectionOf(backup.Manifest))}";

    private void PromoteToWeekly(FolderTarget target, IReadOnlyList<BackupDescriptor> backups, List<string> promoted)
    {
        if (_policy.WeeklyKeep == 0)
        {
            return;
        }

        var dailyOnWeeklyAnchor = backups
            .Where(b => b.Manifest!.RetentionTier == RetentionTier.Daily
                && b.Manifest!.CreatedAt.DayOfWeek == _policy.WeeklyAnchor)
            .OrderByDescending(b => b.Manifest!.CreatedAt)
            .ToList();

        if (dailyOnWeeklyAnchor.Count == 0)
        {
            return;
        }

        var existingWeeklyKeys = backups
            .Where(b => b.Manifest!.RetentionTier == RetentionTier.Weekly)
            .Select(b => IsoWeekKey(b.Manifest!.CreatedAt))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var candidate in dailyOnWeeklyAnchor)
        {
            var key = IsoWeekKey(candidate.Manifest!.CreatedAt);
            if (existingWeeklyKeys.Contains(key))
            {
                continue;
            }
            PromoteFile(target, candidate, RetentionTier.Weekly, promoted);
            existingWeeklyKeys.Add(key);
        }
    }

    private void PromoteToMonthly(FolderTarget target, IReadOnlyList<BackupDescriptor> backups, List<string> promoted)
    {
        if (_policy.MonthlyKeep == 0)
        {
            return;
        }

        var weeklyByMonth = backups
            .Where(b => b.Manifest!.RetentionTier == RetentionTier.Weekly)
            .GroupBy(b => YearMonthKey(b.Manifest!.CreatedAt))
            .ToDictionary(g => g.Key, g => g.OrderByDescending(b => b.Manifest!.CreatedAt).First());

        var existingMonthlyKeys = backups
            .Where(b => b.Manifest!.RetentionTier == RetentionTier.Monthly)
            .Select(b => YearMonthKey(b.Manifest!.CreatedAt))
            .ToHashSet(StringComparer.Ordinal);

        var now = _clock.GetUtcNow();
        foreach (var (monthKey, candidate) in weeklyByMonth)
        {
            if (existingMonthlyKeys.Contains(monthKey))
            {
                continue;
            }
            if (!IsPastMonth(candidate.Manifest!.CreatedAt, now))
            {
                continue;
            }
            PromoteFile(target, candidate, RetentionTier.Monthly, promoted);
            existingMonthlyKeys.Add(monthKey);
        }
    }

    private static void PromoteFile(FolderTarget target, BackupDescriptor descriptor, RetentionTier newTier, List<string> promoted)
    {
        var newName = BackupRenamer.ReplaceTierInFilename(descriptor.FileName, newTier);
        if (string.Equals(newName, descriptor.FileName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        BackupRenamer.UpdateManifestTier(descriptor.FullPath, newTier);
        target.Rename(descriptor.FileName, newName);
        promoted.Add($"{descriptor.FileName} -> {newName}");
    }

    private static void PruneTier(FolderTarget target, IReadOnlyList<BackupDescriptor> backups, RetentionTier tier, int keep, List<string> pruned)
    {
        var inTier = backups
            .Where(b => b.Manifest!.RetentionTier == tier)
            .OrderByDescending(b => b.Manifest!.CreatedAt)
            .ToList();

        if (inTier.Count <= keep)
        {
            return;
        }

        foreach (var stale in inTier.Skip(keep))
        {
            target.Delete(stale.FileName);
            pruned.Add(stale.FileName);
        }
    }

    private static string IsoWeekKey(DateTimeOffset dt)
    {
        var iso = System.Globalization.ISOWeek.GetWeekOfYear(dt.UtcDateTime);
        var isoYear = System.Globalization.ISOWeek.GetYear(dt.UtcDateTime);
        return $"{isoYear:D4}-W{iso:D2}";
    }

    private static string YearMonthKey(DateTimeOffset dt)
        => dt.UtcDateTime.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);

    private static bool IsPastMonth(DateTimeOffset candidate, DateTimeOffset now)
        => candidate.UtcDateTime.Year < now.UtcDateTime.Year
           || (candidate.UtcDateTime.Year == now.UtcDateTime.Year
               && candidate.UtcDateTime.Month < now.UtcDateTime.Month);
}
