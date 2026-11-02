using System.Globalization;
using System.Text;

namespace ClaudePortable.Core.Abstractions;

public enum OperationLogLevel
{
    /// <summary>Detail: excluded files, every snapshot, every link.</summary>
    Debug,

    /// <summary>Steps and results.</summary>
    Info,

    /// <summary>Something was skipped or degraded, but the operation went on.</summary>
    Warning,

    /// <summary>The operation failed.</summary>
    Error,
}

/// <summary>
/// Where the engines report what they do, step by step. Every entry belongs
/// to a section ("Sources", "External folders", ...; null = the operation's
/// overview), so the App can show a run as collapsible groups instead of a
/// flat list, and write the same structure to the log files. Engines never
/// write to the console themselves, so a scheduled backup leaves the same
/// trail as one started from the GUI.
/// </summary>
public interface IOperationLog
{
    void Write(OperationLogLevel level, string? section, string message);
}

/// <summary>Section names shared by the engines and the App.</summary>
public static class LogSections
{
    public const string RunningApps = "Running apps";
    public const string InstalledApps = "Installed apps";
    public const string Sources = "Sources";
    public const string LeftOut = "Left out";
    public const string Links = "Links";
    public const string External = "External folders";
    public const string Databases = "Databases";
    public const string Archive = "Archive";
    public const string BackupDetails = "Backup details";
    public const string VersionCheck = "Version check";
    public const string Paths = "Paths";
    public const string Targets = "Restored folders";
    public const string ProvideHere = "Provide on this PC";
    public const string Retention = "Retention";
}

public static class OperationLogExtensions
{
    public static void Write(this IOperationLog log, OperationLogLevel level, string message) => log.Write(level, null, message);

    public static void Debug(this IOperationLog log, string message) => log.Write(OperationLogLevel.Debug, null, message);

    public static void Info(this IOperationLog log, string message) => log.Write(OperationLogLevel.Info, null, message);

    public static void Warn(this IOperationLog log, string message) => log.Write(OperationLogLevel.Warning, null, message);

    public static void Error(this IOperationLog log, string message) => log.Write(OperationLogLevel.Error, null, message);

    /// <summary>A log whose entries all go to <paramref name="section"/>.</summary>
    public static IOperationLog In(this IOperationLog log, string section) => new SectionLog(log, section);

    /// <summary>"1.2 GB", "340 KB": sizes for log lines.</summary>
    public static string Bytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return string.Create(CultureInfo.InvariantCulture, $"{value:0.#} {units[unit]}");
    }

    /// <summary>"4.2 s", "1 min 12 s".</summary>
    public static string Duration(TimeSpan elapsed)
        => elapsed.TotalSeconds < 60
            ? string.Create(CultureInfo.InvariantCulture, $"{elapsed.TotalSeconds:0.0} s")
            : string.Create(CultureInfo.InvariantCulture, $"{(int)elapsed.TotalMinutes} min {elapsed.Seconds} s");

    private sealed class SectionLog(IOperationLog inner, string section) : IOperationLog
    {
        public void Write(OperationLogLevel level, string? ignored, string message) => inner.Write(level, section, message);
    }
}

public sealed class NullOperationLog : IOperationLog
{
    public static readonly NullOperationLog Instance = new();

    public void Write(OperationLogLevel level, string? section, string message)
    {
    }
}

/// <summary>
/// Adapts an <see cref="IOperationLog"/> to the TextWriter warning sink of
/// <see cref="Archive.ZipArchiveWriter"/>: each line becomes a warning in
/// the "Archive" section.
/// </summary>
public sealed class OperationLogWriter(IOperationLog log) : TextWriter
{
    public override Encoding Encoding => Encoding.UTF8;

    public override void WriteLine(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }
        const string prefix = "warning: ";
        log.Write(OperationLogLevel.Warning, LogSections.Archive, value.StartsWith(prefix, StringComparison.Ordinal) ? value[prefix.Length..] : value);
    }

    public override void Write(char value)
    {
        // The archive writer only writes whole lines.
    }
}
