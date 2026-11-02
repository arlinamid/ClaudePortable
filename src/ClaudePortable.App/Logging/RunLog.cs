using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudePortable.Core.Abstractions;
using Serilog;
using Serilog.Events;

namespace ClaudePortable.App.Logging;

public enum RunStatus
{
    Running,
    Succeeded,
    Warnings,
    Failed,
    Cancelled,

    /// <summary>Loaded from a file that has no end record: the process was ended mid-run.</summary>
    Interrupted,
}

public sealed record RunEntry(DateTimeOffset Time, OperationLogLevel Level, string? Section, string Message);

/// <summary>
/// The log of one operation (a backup, a restore, a path repair, or the
/// app's general activity), with every entry tagged by section. Each entry
/// goes to three places:
/// <list type="bullet">
/// <item>the text log (<c>logs\claudeportable-&lt;date&gt;.log</c>, Serilog);</item>
/// <item>a structured file per run (<c>logs\runs\&lt;start&gt;-&lt;kind&gt;.jsonl</c>), which the
/// Logs tab reads back, so backups run by the scheduled task or from a
/// terminal show up there too;</item>
/// <item>subscribers (the Logs tab, live) and, for the CLI, stderr for warnings.</item>
/// </list>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RunLog : IOperationLog, IDisposable
{
    private const int KeepRunFiles = 150;

    private readonly object _gate = new();
    private readonly List<RunEntry> _entries = [];
    private readonly TextWriter? _echo;
    private readonly bool _persist;
    private readonly string _folder;
    private StreamWriter? _file;
    private bool _fileOpened;

    private RunLog(string id, string kind, string title, string origin, DateTimeOffset started, TextWriter? echo, bool persist, string? folder = null)
    {
        _folder = folder ?? RunsFolder;
        Id = id;
        Kind = kind;
        Title = title;
        Origin = origin;
        Started = started;
        _echo = echo;
        _persist = persist;
    }

    public string Id { get; }

    /// <summary>"backup", "restore", "repair", "app".</summary>
    public string Kind { get; }

    /// <summary>What the run is about, e.g. the destination folder or the ZIP.</summary>
    public string Title { get; }

    /// <summary>"app", "cli" (a terminal) or "scheduled" (no console: Task Scheduler).</summary>
    public string Origin { get; }

    public DateTimeOffset Started { get; }

    public DateTimeOffset? Ended { get; private set; }

    public RunStatus Status { get; private set; } = RunStatus.Running;

    public string? Summary { get; private set; }

    public string? FilePath { get; private set; }

    public int WarningCount { get; private set; }

    public int ErrorCount { get; private set; }

    public event Action<RunLog, RunEntry>? EntryAdded;

    public event Action<RunLog>? Completed;

    public IReadOnlyList<RunEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return _entries.ToList();
            }
        }
    }

    public static string RunsFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClaudePortable",
        "logs",
        "runs");

    /// <param name="echo">CLI: warnings and errors are also written here (stderr).</param>
    /// <param name="persist">False for runs that should not leave a file.</param>
    /// <param name="folder">Where the run file goes; default <see cref="RunsFolder"/> (tests pass their own).</param>
    public static RunLog Start(string kind, string title, string origin, TextWriter? echo = null, bool persist = true, string? folder = null)
    {
        var started = DateTimeOffset.Now;
        var id = $"{started:yyyyMMdd-HHmmss}-{kind}-{Environment.ProcessId}-{Guid.NewGuid().ToString("N")[..6]}";
        var run = new RunLog(id, kind, title, origin, started, echo, persist, folder);
        Log.Information("[{Kind}] === {Title} started (run {Id}) ===", kind, title, id);
        return run;
    }

    public void Write(OperationLogLevel level, string? section, string message)
    {
        var entry = new RunEntry(DateTimeOffset.Now, level, section, message);
        lock (_gate)
        {
            _entries.Add(entry);
            if (level == OperationLogLevel.Warning)
            {
                WarningCount++;
            }
            else if (level == OperationLogLevel.Error)
            {
                ErrorCount++;
            }
            WriteRecord(new JsonObject
            {
                ["type"] = "entry",
                ["time"] = entry.Time.ToString("O", CultureInfo.InvariantCulture),
                ["level"] = level.ToString(),
                ["section"] = section,
                ["message"] = message,
            });
        }

        Log.Write(ToSerilog(level), "[{Kind}] {Section}{Message}", Kind, section is null ? string.Empty : $"[{section}] ", message);
        if (_echo is not null && level >= OperationLogLevel.Warning)
        {
            lock (_echo)
            {
                _echo.WriteLine($"{(level == OperationLogLevel.Error ? "error" : "warning")}: {(section is null ? string.Empty : $"[{section}] ")}{message}");
            }
        }
        EntryAdded?.Invoke(this, entry);
    }

    /// <summary>Warnings logged in one section, e.g. files the archive writer had to skip.</summary>
    public int WarningsIn(string section)
    {
        lock (_gate)
        {
            return _entries.Count(e => e.Level == OperationLogLevel.Warning && e.Section == section);
        }
    }

    /// <summary>
    /// End the run. Without an explicit status it is derived from what was
    /// logged: any error = failed, any warning = finished with warnings.
    /// </summary>
    public void Complete(RunStatus? status = null, string? summary = null)
    {
        lock (_gate)
        {
            if (Status != RunStatus.Running)
            {
                return;
            }
            Status = status ?? (ErrorCount > 0 ? RunStatus.Failed : WarningCount > 0 ? RunStatus.Warnings : RunStatus.Succeeded);
            Summary = summary;
            Ended = DateTimeOffset.Now;
            WriteRecord(new JsonObject
            {
                ["type"] = "end",
                ["time"] = Ended.Value.ToString("O", CultureInfo.InvariantCulture),
                ["status"] = Status.ToString(),
                ["summary"] = summary,
                ["warnings"] = WarningCount,
                ["errors"] = ErrorCount,
            });
            _file?.Dispose();
            _file = null;
        }
        Log.Information(
            "[{Kind}] === {Title} {Status} after {Duration}: {Warnings} warning(s), {Errors} error(s){Summary} ===",
            Kind, Title, Status, OperationLogExtensions.Duration(Ended!.Value - Started), WarningCount, ErrorCount,
            summary is null ? string.Empty : $" - {summary}");
        Completed?.Invoke(this);
    }

    public void Dispose()
    {
        if (Status == RunStatus.Running)
        {
            Complete();
        }
    }

    /// <summary>The file is created with the first entry, so runs that log nothing leave nothing behind.</summary>
    private void WriteRecord(JsonObject record)
    {
        if (!_persist)
        {
            return;
        }
        try
        {
            if (!_fileOpened)
            {
                _fileOpened = true;
                Directory.CreateDirectory(_folder);
                PruneOldRuns(_folder);
                FilePath = Path.Combine(_folder, $"{Id}.jsonl");
                _file = new StreamWriter(new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
                _file.WriteLine(new JsonObject
                {
                    ["type"] = "start",
                    ["id"] = Id,
                    ["kind"] = Kind,
                    ["title"] = Title,
                    ["origin"] = Origin,
                    ["time"] = Started.ToString("O", CultureInfo.InvariantCulture),
                    ["host"] = Environment.MachineName,
                    ["version"] = typeof(RunLog).Assembly.GetName().Version?.ToString(),
                }.ToJsonString());
            }
            _file?.WriteLine(record.ToJsonString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A full disk must not break the operation being logged; the
            // text log and the live view still have the entry.
            _file = null;
        }
    }

    private static void PruneOldRuns(string folder)
    {
        try
        {
            foreach (var old in new DirectoryInfo(folder).EnumerateFiles("*.jsonl")
                         .OrderByDescending(f => f.Name, StringComparer.Ordinal)
                         .Skip(KeepRunFiles))
            {
                old.Delete();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>The most recent runs stored on disk, newest first.</summary>
    public static IReadOnlyList<RunLog> LoadRecent(int count, IReadOnlySet<string>? skipIds = null, string? folder = null)
    {
        folder ??= RunsFolder;
        try
        {
            if (!Directory.Exists(folder))
            {
                return [];
            }
            return new DirectoryInfo(folder).EnumerateFiles("*.jsonl")
                .OrderByDescending(f => f.Name, StringComparer.Ordinal)
                .Where(f => skipIds is null || !skipIds.Contains(Path.GetFileNameWithoutExtension(f.Name)))
                .Take(count)
                .Select(f => Load(f.FullName))
                .OfType<RunLog>()
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Read a run file back; null when it cannot be read.</summary>
    public static RunLog? Load(string path)
    {
        try
        {
            RunLog? run = null;
            using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
            while (reader.ReadLine() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line) || JsonNode.Parse(line) is not JsonObject o)
                {
                    continue;
                }
                switch ((string?)o["type"])
                {
                    case "start":
                        run = new RunLog(
                            (string?)o["id"] ?? Path.GetFileNameWithoutExtension(path),
                            (string?)o["kind"] ?? "run",
                            (string?)o["title"] ?? string.Empty,
                            (string?)o["origin"] ?? "app",
                            ParseTime(o["time"]),
                            echo: null,
                            persist: false)
                        {
                            FilePath = path,
                        };
                        break;
                    case "entry" when run is not null:
                        var level = Enum.TryParse<OperationLogLevel>((string?)o["level"], out var l) ? l : OperationLogLevel.Info;
                        run._entries.Add(new RunEntry(ParseTime(o["time"]), level, (string?)o["section"], (string?)o["message"] ?? string.Empty));
                        if (level == OperationLogLevel.Warning)
                        {
                            run.WarningCount++;
                        }
                        else if (level == OperationLogLevel.Error)
                        {
                            run.ErrorCount++;
                        }
                        break;
                    case "end" when run is not null:
                        run.Ended = ParseTime(o["time"]);
                        run.Status = Enum.TryParse<RunStatus>((string?)o["status"], out var s) ? s : RunStatus.Succeeded;
                        run.Summary = (string?)o["summary"];
                        break;
                }
            }
            if (run is not null && run.Status == RunStatus.Running)
            {
                run.Status = RunStatus.Interrupted;
                run.Ended = run._entries.Count > 0 ? run._entries[^1].Time : run.Started;
            }
            return run;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static DateTimeOffset ParseTime(JsonNode? node)
        => DateTimeOffset.TryParse((string?)node, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t) ? t : DateTimeOffset.MinValue;

    private static LogEventLevel ToSerilog(OperationLogLevel level) => level switch
    {
        OperationLogLevel.Debug => LogEventLevel.Debug,
        OperationLogLevel.Warning => LogEventLevel.Warning,
        OperationLogLevel.Error => LogEventLevel.Error,
        _ => LogEventLevel.Information,
    };
}
