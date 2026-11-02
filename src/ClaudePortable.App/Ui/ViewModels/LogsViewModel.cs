using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using Brush = System.Windows.Media.Brush;
using System.Windows.Threading;
using ClaudePortable.App.Localization;
using ClaudePortable.App.Logging;
using ClaudePortable.Core.Abstractions;

namespace ClaudePortable.App.Ui.ViewModels;

/// <summary>
/// The Logs tab: one collapsible card per run (backup, restore, repair, app
/// activity), newest first, each split into collapsible sections. Runs of
/// this session update live; earlier runs - including scheduled and
/// command-line backups - are read from the structured run files.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class LogsViewModel : ViewModelBase
{
    private const int HistoryRuns = 40;

    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly HashSet<string> _liveIds = new(StringComparer.Ordinal);
    private bool _isLoading;

    public LogsViewModel()
    {
        RefreshCommand = new AsyncRelayCommand(LoadHistoryAsync);
        OpenFolderCommand = new RelayCommand(OpenFolder);
    }

    public ObservableCollection<RunItemViewModel> Runs { get; } = new();

    public AsyncRelayCommand RefreshCommand { get; }

    public RelayCommand OpenFolderCommand { get; }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetField(ref _isLoading, value))
            {
                Raise(nameof(IsEmpty));
            }
        }
    }

    public bool IsEmpty => !IsLoading && Runs.Count == 0;

    /// <summary>Show a run of this session, open, and keep it updated.</summary>
    public void AttachLive(RunLog run)
    {
        void Attach()
        {
            _liveIds.Add(run.Id);
            foreach (var other in Runs)
            {
                other.IsExpanded = false;
            }
            var vm = new RunItemViewModel(run, isLive: true) { IsExpanded = true };
            Runs.Insert(0, vm);
            Raise(nameof(IsEmpty));
            run.EntryAdded += (_, entry) => Post(() => vm.Add(entry));
            run.Completed += _ => Post(vm.RefreshHeader);
        }
        Post(Attach);
    }

    /// <summary>(Re)load the runs stored on disk below the live ones.</summary>
    public async Task LoadHistoryAsync()
    {
        IsLoading = true;
        try
        {
            var skip = _liveIds.ToHashSet(StringComparer.Ordinal);
            var stored = await Task.Run(() => RunLog.LoadRecent(HistoryRuns, skip)).ConfigureAwait(true);
            for (var i = Runs.Count - 1; i >= 0; i--)
            {
                if (!Runs[i].IsLive)
                {
                    Runs.RemoveAt(i);
                }
            }
            foreach (var run in stored)
            {
                Runs.Add(new RunItemViewModel(run, isLive: false));
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    private static void OpenFolder()
    {
        var folder = Path.GetDirectoryName(RunLog.RunsFolder)!;
        Directory.CreateDirectory(folder);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = false })?.Dispose();
    }

    private void Post(Action action)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            _dispatcher.BeginInvoke(action);
        }
    }

    internal static Brush BrushFor(string key) => (Brush)System.Windows.Application.Current.FindResource(key);
}

/// <summary>One run: header with status, time, duration and counts; sections built on first expand.</summary>
[SupportedOSPlatform("windows")]
public sealed class RunItemViewModel : ViewModelBase
{
    private readonly RunLog _run;
    private bool _isExpanded;
    private bool _sectionsBuilt;

    public RunItemViewModel(RunLog run, bool isLive)
    {
        _run = run;
        IsLive = isLive;
        CopyCommand = new RelayCommand(Copy);
        if (isLive)
        {
            BuildSections();
        }
    }

    public bool IsLive { get; }

    public ObservableCollection<SectionViewModel> Sections { get; } = new();

    public RelayCommand CopyCommand { get; }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (value)
            {
                BuildSections();
            }
            SetField(ref _isExpanded, value);
        }
    }

    public string KindLabel => _run.Kind switch
    {
        "backup" => Loc.T("Run_Backup"),
        "restore" => Loc.T("Run_Restore"),
        "repair" => Loc.T("Run_Repair"),
        "app" => Loc.T("Run_App"),
        _ => _run.Kind,
    };

    public string Detail => _run.Title;

    public string OriginLabel => _run.Origin switch
    {
        "scheduled" => Loc.T("Run_OriginScheduled"),
        "cli" => Loc.T("Run_OriginCli"),
        _ => string.Empty,
    };

    public bool HasOrigin => OriginLabel.Length > 0;

    public string StartedText => _run.Started.LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);

    public string DurationText => _run.Ended is { } end
        ? OperationLogExtensions.Duration(end - _run.Started)
        : Loc.T("Run_InProgress");

    public string StatusText => _run.Status switch
    {
        RunStatus.Running => Loc.T("Run_StatusRunning"),
        RunStatus.Succeeded => Loc.T("Run_StatusSucceeded"),
        RunStatus.Warnings => Loc.T("Run_StatusWarnings"),
        RunStatus.Failed => Loc.T("Run_StatusFailed"),
        RunStatus.Cancelled => Loc.T("Run_StatusCancelled"),
        _ => Loc.T("Run_StatusInterrupted"),
    };

    /// <summary>Segoe Fluent Icons glyph for the status.</summary>
    public string StatusGlyph => _run.Status switch
    {
        RunStatus.Running => "",
        RunStatus.Succeeded => "",
        RunStatus.Warnings => "",
        RunStatus.Failed => "",
        RunStatus.Cancelled => "",
        _ => "",
    };

    public Brush StatusBrush => LogsViewModel.BrushFor(_run.Status switch
    {
        RunStatus.Succeeded => "SuccessBrush",
        RunStatus.Warnings or RunStatus.Interrupted or RunStatus.Cancelled => "WarnBrush",
        RunStatus.Failed => "ErrorBrush",
        _ => "AccentBrush",
    });

    public string CountsText
    {
        get
        {
            var parts = new List<string>();
            if (_run.ErrorCount > 0)
            {
                parts.Add(Loc.F("Run_Errors", _run.ErrorCount));
            }
            if (_run.WarningCount > 0)
            {
                parts.Add(Loc.F("Run_Warnings", _run.WarningCount));
            }
            return string.Join(" · ", parts);
        }
    }

    public string Summary => _run.Summary ?? string.Empty;

    public bool HasSummary => !string.IsNullOrEmpty(_run.Summary);

    public string AutomationName => $"{KindLabel} {Detail}, {StartedText}, {StatusText}{(CountsText.Length > 0 ? ", " + CountsText : string.Empty)}";

    public void Add(RunEntry entry)
    {
        if (_sectionsBuilt)
        {
            AddToSection(entry);
        }
        RefreshHeader();
    }

    public void RefreshHeader()
    {
        Raise(nameof(StatusText));
        Raise(nameof(StatusGlyph));
        Raise(nameof(StatusBrush));
        Raise(nameof(CountsText));
        Raise(nameof(DurationText));
        Raise(nameof(Summary));
        Raise(nameof(HasSummary));
        Raise(nameof(AutomationName));
    }

    private void BuildSections()
    {
        if (_sectionsBuilt)
        {
            return;
        }
        _sectionsBuilt = true;
        foreach (var entry in _run.Entries)
        {
            AddToSection(entry);
        }
    }

    private void AddToSection(RunEntry entry)
    {
        var name = entry.Section ?? string.Empty;
        var section = Sections.FirstOrDefault(s => s.Key == name);
        if (section is null)
        {
            section = new SectionViewModel(name);
            // The overview (entries without a section) always comes first.
            if (name.Length == 0)
            {
                Sections.Insert(0, section);
            }
            else
            {
                Sections.Add(section);
            }
        }
        section.Add(entry);
    }

    private void Copy()
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"{KindLabel} {Detail} - {StartedText} - {StatusText} ({DurationText})");
        foreach (var entry in _run.Entries)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{entry.Time.LocalDateTime:HH:mm:ss} {entry.Level,-7} {(entry.Section is null ? string.Empty : $"[{entry.Section}] ")}{entry.Message}");
        }
        try
        {
            System.Windows.Clipboard.SetText(sb.ToString());
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Clipboard busy (another app holds it); nothing to do.
        }
    }
}

/// <summary>A collapsible group of entries; opens by itself when it holds a warning or error.</summary>
[SupportedOSPlatform("windows")]
public sealed class SectionViewModel : ViewModelBase
{
    private bool _isExpanded;
    private int _warnings;
    private int _errors;

    public SectionViewModel(string key)
    {
        Key = key;
        _isExpanded = key.Length == 0;
    }

    public string Key { get; }

    public ObservableCollection<LineViewModel> Lines { get; } = new();

    public string Name => Key switch
    {
        "" => Loc.T("LogSection_Overview"),
        LogSections.RunningApps => Loc.T("LogSection_RunningApps"),
        LogSections.InstalledApps => Loc.T("LogSection_InstalledApps"),
        LogSections.Sources => Loc.T("LogSection_Sources"),
        LogSections.LeftOut => Loc.T("LogSection_LeftOut"),
        LogSections.Links => Loc.T("LogSection_Links"),
        LogSections.External => Loc.T("LogSection_External"),
        LogSections.Databases => Loc.T("LogSection_Databases"),
        LogSections.Archive => Loc.T("LogSection_Archive"),
        LogSections.BackupDetails => Loc.T("LogSection_BackupDetails"),
        LogSections.VersionCheck => Loc.T("LogSection_VersionCheck"),
        LogSections.Paths => Loc.T("LogSection_Paths"),
        LogSections.Targets => Loc.T("LogSection_Targets"),
        LogSections.ProvideHere => Loc.T("LogSection_ProvideHere"),
        LogSections.Retention => Loc.T("LogSection_Retention"),
        _ => Key,
    };

    public string CountText => Lines.Count.ToString(CultureInfo.CurrentCulture);

    public string IssuesText => _errors > 0
        ? Loc.F("Run_Errors", _errors) + (_warnings > 0 ? " · " + Loc.F("Run_Warnings", _warnings) : string.Empty)
        : _warnings > 0 ? Loc.F("Run_Warnings", _warnings) : string.Empty;

    public bool HasIssues => _errors + _warnings > 0;

    public Brush IssuesBrush => LogsViewModel.BrushFor(_errors > 0 ? "ErrorBrush" : "WarnBrush");

    public string AutomationName => $"{Name}, {CountText}{(HasIssues ? ", " + IssuesText : string.Empty)}";

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetField(ref _isExpanded, value);
    }

    public void Add(RunEntry entry)
    {
        Lines.Add(new LineViewModel(entry));
        if (entry.Level == OperationLogLevel.Warning)
        {
            _warnings++;
            IsExpanded = true;
        }
        else if (entry.Level == OperationLogLevel.Error)
        {
            _errors++;
            IsExpanded = true;
        }
        Raise(nameof(CountText));
        Raise(nameof(IssuesText));
        Raise(nameof(HasIssues));
        Raise(nameof(IssuesBrush));
        Raise(nameof(AutomationName));
    }
}

[SupportedOSPlatform("windows")]
public sealed class LineViewModel(RunEntry entry)
{
    public string Time => entry.Time.LocalDateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    public string Message => entry.Message;

    public bool IsDetail => entry.Level == OperationLogLevel.Debug;

    public string Glyph => entry.Level switch
    {
        OperationLogLevel.Warning => "",
        OperationLogLevel.Error => "",
        _ => string.Empty,
    };

    public Brush Foreground => LogsViewModel.BrushFor(entry.Level switch
    {
        OperationLogLevel.Warning => "WarnBrush",
        OperationLogLevel.Error => "ErrorBrush",
        OperationLogLevel.Debug => "TextMutedBrush",
        _ => "TextSecondaryBrush",
    });

    public string AutomationName => entry.Level switch
    {
        OperationLogLevel.Warning => $"{Loc.T("Log_LevelWarning")}: {Message}",
        OperationLogLevel.Error => $"{Loc.T("Log_LevelError")}: {Message}",
        _ => Message,
    };
}
