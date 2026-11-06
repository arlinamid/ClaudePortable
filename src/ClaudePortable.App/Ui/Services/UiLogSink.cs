using System.Runtime.Versioning;
using ClaudePortable.App.Logging;
using ClaudePortable.App.Ui.ViewModels;
using ClaudePortable.Core.Abstractions;

namespace ClaudePortable.App.Ui.Services;

/// <summary>
/// The GUI's entry point to logging. An operation (backup, restore, path
/// repair) runs inside its own <see cref="RunLog"/>; everything else the app
/// reports goes to one "App activity" run per session. Both show up live on
/// the Logs tab and are written to the log files.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class UiLogSink
{
    private static readonly Lazy<UiLogSink> _instance = new(() => new UiLogSink());
    private RunLog? _session;
    private RunLog? _current;

    public static UiLogSink Instance => _instance.Value;

    /// <summary>The Logs tab.</summary>
    public LogsViewModel Viewer { get; } = new();

    /// <summary>Start an operation; until it ends, <see cref="Append"/> lines belong to it.</summary>
    public RunLog BeginRun(string kind, string title)
    {
        var run = RunLog.Start(kind, title, origin: "app");
        _current = run;
        Viewer.AttachLive(run);
        return run;
    }

    public void EndRun(RunLog run, RunStatus? status = null, string? summary = null)
    {
        run.Complete(status, summary);
        if (ReferenceEquals(_current, run))
        {
            _current = null;
        }
    }

    public void Append(string line) => Target.Info(line);

    public void Warn(string line) => Target.Warn(line);

    /// <summary>End the session's runs (app exit).</summary>
    public void Shutdown()
    {
        _current?.Complete(RunStatus.Interrupted);
        _session?.Complete();
    }

    private IOperationLog Target => _current ?? Session;

    private RunLog Session
    {
        get
        {
            if (_session is null)
            {
                _session = RunLog.Start("app", Environment.MachineName, origin: "app");
                Viewer.AttachLive(_session);
            }
            return _session;
        }
    }
}
