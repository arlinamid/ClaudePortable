using ClaudePortable.App.Logging;
using ClaudePortable.Core.Abstractions;
using ClaudePortable.Core.Archive;
using ClaudePortable.Core.Backup;
using ClaudePortable.Core.Manifest;
using Microsoft.Data.Sqlite;

namespace ClaudePortable.Tests;

/// <summary>The sectioned operation log the Logs tab and the log files are built from.</summary>
public class OperationLogTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, $"cp-log-{Guid.NewGuid():N}");

    public OperationLogTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task Backup_ReportsEveryStepInItsSection()
    {
        var dotClaude = Path.Combine(_root, "home", ".claude");
        Directory.CreateDirectory(Path.Combine(dotClaude, "Cache"));
        File.WriteAllText(Path.Combine(dotClaude, "settings.json"), "{}");
        File.WriteAllText(Path.Combine(dotClaude, "Cache", "blob"), "x");
        File.WriteAllText(Path.Combine(dotClaude, ".credentials.json"), "{}");
        using (var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(dotClaude, "state.sqlite"), Pooling = false }.ToString()))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "CREATE TABLE t(x)";
            cmd.ExecuteNonQuery();
        }
        var log = new CapturingLog();
        var engine = new BackupEngine(
            new FakeDiscovery(new("claudeCodeUserProfile", dotClaude, true, "test"), new("codexUserProfile", Path.Combine(_root, "none"), false, "test")),
            new ZipArchiveWriter(new OperationLogWriter(log)),
            NullCoworkProjectDiscovery.Instance,
            log: log);

        await engine.CreateBackupAsync(new(Path.Combine(_root, "out"), RetentionTier.Daily));

        Assert.Contains(log.Entries, e => e.Section is null && e.Message.StartsWith("Backup started", StringComparison.Ordinal));
        Assert.Contains(log.Entries, e => e.Section is null && e.Message.StartsWith("Backup finished", StringComparison.Ordinal));
        Assert.Contains(log.Entries, e => e.Section == LogSections.InstalledApps);
        Assert.Contains(log.Entries, e => e.Section == LogSections.Sources && e.Message.Contains("claudeCodeUserProfile", StringComparison.Ordinal) && e.Message.Contains("left out 1 files", StringComparison.Ordinal));
        Assert.Contains(log.Entries, e => e.Section == LogSections.Sources && e.Message.Contains("codexUserProfile: not present", StringComparison.Ordinal));
        Assert.Contains(log.Entries, e => e.Section == LogSections.LeftOut && e.Level == OperationLogLevel.Debug && e.Message.EndsWith("/Cache/", StringComparison.Ordinal));
        Assert.Contains(log.Entries, e => e.Section == LogSections.LeftOut && e.Message.EndsWith(".credentials.json", StringComparison.Ordinal));
        Assert.Contains(log.Entries, e => e.Section == LogSections.Databases && e.Message.Contains("state.sqlite", StringComparison.Ordinal));
        Assert.Contains(log.Entries, e => e.Section == LogSections.Archive && e.Message.Contains("sha256", StringComparison.Ordinal));
        Assert.DoesNotContain(log.Entries, e => e.Level >= OperationLogLevel.Warning);
    }

    [Fact]
    public void RunLog_RoundTripsThroughItsFile()
    {
        var folder = Path.Combine(_root, "runs");
        var run = RunLog.Start("backup", @"D:\Backups", "scheduled", folder: folder);
        run.Info("Backup started");
        run.In(LogSections.Sources).Info("claudeCodeUserProfile: 3 files");
        run.In(LogSections.Archive).Warn("skipping locked file 'x'");
        run.Complete(summary: "claude-backup.zip");

        var loaded = Assert.Single(RunLog.LoadRecent(10, folder: folder));

        Assert.Equal("backup", loaded.Kind);
        Assert.Equal("scheduled", loaded.Origin);
        Assert.Equal(@"D:\Backups", loaded.Title);
        Assert.Equal(RunStatus.Warnings, loaded.Status);
        Assert.Equal("claude-backup.zip", loaded.Summary);
        Assert.Equal(1, loaded.WarningCount);
        Assert.Equal([null, LogSections.Sources, LogSections.Archive], loaded.Entries.Select(e => e.Section));
        Assert.Equal(1, run.WarningsIn(LogSections.Archive));
    }

    [Fact]
    public void RunLog_WithoutEndRecord_IsInterrupted()
    {
        var folder = Path.Combine(_root, "runs2");
        var run = RunLog.Start("restore", "x.zip", "cli", folder: folder);
        run.Info("Restore started");
        // No Complete(): the process was killed mid-run.

        var loaded = Assert.Single(RunLog.LoadRecent(10, folder: folder));

        Assert.Equal(RunStatus.Interrupted, loaded.Status);
        run.Complete();
    }

    [Fact]
    public void RunLog_EchoesWarningsForTheCli()
    {
        var echo = new StringWriter();
        var run = RunLog.Start("backup", "t", "cli", echo: echo, persist: false);

        run.Info("quiet");
        run.In(LogSections.External).Warn("too large");
        run.Complete();

        Assert.Equal($"warning: [{LogSections.External}] too large{Environment.NewLine}", echo.ToString());
        Assert.Equal(RunStatus.Warnings, run.Status);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        GC.SuppressFinalize(this);
    }

    private sealed class CapturingLog : IOperationLog
    {
        public List<(OperationLogLevel Level, string? Section, string Message)> Entries { get; } = [];

        public void Write(OperationLogLevel level, string? section, string message)
        {
            lock (Entries)
            {
                Entries.Add((level, section, message));
            }
        }
    }

    private sealed class FakeDiscovery(params DiscoveredClaudePath[] paths) : IPathDiscovery
    {
        public IReadOnlyList<DiscoveredClaudePath> Discover() => paths;
    }
}
