using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.Versioning;
using ClaudePortable.App.Localization;
using ClaudePortable.App.Ui.Services;
using ClaudePortable.Scheduler.Scheduling;

namespace ClaudePortable.App.Ui.ViewModels;

public enum ScheduledTasksFilter
{
    Relevant,
    Managed,
    All,
}

[SupportedOSPlatform("windows")]
public sealed class ScheduledTasksViewModel : ViewModelBase
{
    private readonly TaskSchedulerInstaller _installer;
    private readonly List<ScheduledTaskInfo> _allTasks = new();
    private ScheduledTasksFilter _filter = ScheduledTasksFilter.Relevant;
    private bool _isLoading;
    private string _statusLine = Loc.T("Sched_ClickRefresh");

    public ScheduledTasksViewModel()
        : this(new TaskSchedulerInstaller())
    {
    }

    public ScheduledTasksViewModel(TaskSchedulerInstaller installer)
    {
        _installer = installer;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        SetFilterAllCommand = new RelayCommand(() => Filter = ScheduledTasksFilter.All);
        SetFilterManagedCommand = new RelayCommand(() => Filter = ScheduledTasksFilter.Managed);
        SetFilterRelevantCommand = new RelayCommand(() => Filter = ScheduledTasksFilter.Relevant);
        InstallBackupTaskCommand = new AsyncRelayCommand(InstallBackupTaskAsync);
        RemoveBackupTaskCommand = new AsyncRelayCommand(RemoveBackupTaskAsync);
    }

    public ObservableCollection<ScheduledTaskInfoVm> Tasks { get; } = new();

    public AsyncRelayCommand RefreshCommand { get; }
    public RelayCommand SetFilterAllCommand { get; }
    public RelayCommand SetFilterManagedCommand { get; }
    public RelayCommand SetFilterRelevantCommand { get; }
    public AsyncRelayCommand InstallBackupTaskCommand { get; }
    public AsyncRelayCommand RemoveBackupTaskCommand { get; }

    /// <summary>Supplies the backup destination for the auto-backup task;
    /// wired by MainViewModel to the active (top) target folder.</summary>
    public Func<string?>? ActiveTargetProvider { get; set; }

    private string _installTime = "23:00";

    public string InstallTime
    {
        get => _installTime;
        set => SetField(ref _installTime, value);
    }

    private string _installTaskName = "ClaudePortable-Daily";

    public string InstallTaskName
    {
        get => _installTaskName;
        set => SetField(ref _installTaskName, value);
    }

    /// <summary>
    /// GUI equivalent of `claudeportable schedule install`: emits the Task
    /// Scheduler XML for a daily backup into the active target and registers
    /// it via schtasks.exe. Re-installing under the same name replaces it.
    /// </summary>
    public async Task InstallBackupTaskAsync()
    {
        if (!TimeOnly.TryParseExact(InstallTime.Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at))
        {
            StatusLine = Loc.F("Schedule_InvalidTime", InstallTime);
            return;
        }

        var targetFolder = ActiveTargetProvider?.Invoke();
        if (string.IsNullOrWhiteSpace(targetFolder))
        {
            StatusLine = Loc.T("Schedule_NoActiveTarget");
            return;
        }

        var taskName = string.IsNullOrWhiteSpace(InstallTaskName) ? "ClaudePortable-Daily" : InstallTaskName.Trim();

        try
        {
            var exe = Environment.ProcessPath
                ?? throw new InvalidOperationException("Could not determine current executable path.");
            var spec = new ScheduleSpec(
                TaskName: taskName,
                ExecutablePath: exe,
                Arguments: new[] { "backup", "--to", targetFolder },
                DailyStart: at,
                Description: Loc.F("Cli_Schedule_TaskDescription", targetFolder));

            var xml = TaskSchedulerEmitter.ToXml(spec, DateTimeOffset.UtcNow);
            var xmlPath = Path.Combine(
                Environment.ExpandEnvironmentVariables("%LOCALAPPDATA%"),
                "ClaudePortable",
                $"{taskName}.xml");
            Directory.CreateDirectory(Path.GetDirectoryName(xmlPath)!);
            await File.WriteAllTextAsync(xmlPath, xml).ConfigureAwait(true);

            var exit = await _installer.InstallAsync(taskName, xmlPath).ConfigureAwait(true);
            if (exit != 0)
            {
                StatusLine = Loc.F("Schedule_InstallFailed", $"schtasks exit {exit}");
                UiLogSink.Instance.Append($"schedule install failed: '{taskName}' exit={exit}");
                return;
            }

            StatusLine = Loc.F("Schedule_InstallOk", taskName, at.ToString("HH:mm", CultureInfo.InvariantCulture));
            UiLogSink.Instance.Append($"schedule install: '{taskName}' daily at {at:HH:mm} -> {targetFolder}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            StatusLine = Loc.F("Schedule_InstallFailed", ex.Message);
            UiLogSink.Instance.Append($"schedule install failed: {ex.Message}");
            return;
        }

        await RefreshAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// GUI equivalent of `claudeportable schedule remove`: deletes the
    /// auto-backup task named in the card after a confirmation prompt.
    /// </summary>
    public async Task RemoveBackupTaskAsync()
    {
        var taskName = string.IsNullOrWhiteSpace(InstallTaskName) ? "ClaudePortable-Daily" : InstallTaskName.Trim();

        var ok = System.Windows.MessageBox.Show(
            Loc.F("Sched_DeleteConfirmText", taskName),
            Loc.T("Sched_DeleteConfirmTitle"),
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning) == System.Windows.MessageBoxResult.Yes;
        if (!ok)
        {
            return;
        }

        var exit = await _installer.DeleteAsync(taskName).ConfigureAwait(true);
        if (exit != 0)
        {
            StatusLine = Loc.F("Schedule_RemoveFailed", $"schtasks exit {exit}");
            UiLogSink.Instance.Append($"schedule remove failed: '{taskName}' exit={exit}");
        }
        else
        {
            StatusLine = Loc.F("Schedule_RemoveOk", taskName);
            UiLogSink.Instance.Append($"schedule remove: '{taskName}'");
        }
        await RefreshAsync().ConfigureAwait(true);
    }

    public ScheduledTasksFilter Filter
    {
        get => _filter;
        set
        {
            if (SetField(ref _filter, value))
            {
                Raise(nameof(IsFilterAll));
                Raise(nameof(IsFilterManaged));
                Raise(nameof(IsFilterRelevant));
                ApplyFilter();
            }
        }
    }

    public bool IsFilterAll
    {
        get => _filter == ScheduledTasksFilter.All;
        set
        {
            if (value)
            {
                Filter = ScheduledTasksFilter.All;
            }
        }
    }

    public bool IsFilterManaged
    {
        get => _filter == ScheduledTasksFilter.Managed;
        set
        {
            if (value)
            {
                Filter = ScheduledTasksFilter.Managed;
            }
        }
    }

    public bool IsFilterRelevant
    {
        get => _filter == ScheduledTasksFilter.Relevant;
        set
        {
            if (value)
            {
                Filter = ScheduledTasksFilter.Relevant;
            }
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        set => SetField(ref _isLoading, value);
    }

    public string StatusLine
    {
        get => _statusLine;
        set => SetField(ref _statusLine, value);
    }

    public async Task RefreshAsync()
    {
        IsLoading = true;
        StatusLine = Loc.T("Sched_Loading");
        try
        {
            var infos = await Task.Run(() => _installer.EnumerateAsync(CancellationToken.None)).ConfigureAwait(true);
            _allTasks.Clear();
            _allTasks.AddRange(infos);
            ApplyFilter();
            var managed = _allTasks.Count(t => t.ManagedBy == ManagedBy.ClaudePortable);
            var relevant = _allTasks.Count(t => t.ManagedBy == ManagedBy.ForeignRelevant);
            StatusLine = Loc.F("Sched_StatusLine", _allTasks.Count, managed, relevant);
        }
        catch (Exception ex)
        {
            StatusLine = Loc.F("Sched_EnumFailed", ex.Message);
            UiLogSink.Instance.Append($"schedule enumerate failed: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void ApplyFilter()
    {
        Tasks.Clear();
        IEnumerable<ScheduledTaskInfo> filtered = _filter switch
        {
            ScheduledTasksFilter.All => _allTasks,
            ScheduledTasksFilter.Managed => _allTasks.Where(t => t.ManagedBy == ManagedBy.ClaudePortable),
            _ => _allTasks.Where(t => t.ManagedBy is ManagedBy.ClaudePortable or ManagedBy.ForeignRelevant),
        };
        foreach (var info in filtered.OrderBy(t => t.ManagedBy).ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
        {
            Tasks.Add(new ScheduledTaskInfoVm(
                info,
                RunNowAsync,
                DisableAsync,
                EnableAsync,
                DeleteAsync,
                ViewXmlAsync));
        }
    }

    private async Task RunNowAsync(ScheduledTaskInfoVm row)
    {
        var exit = await _installer.RunNowAsync(row.FullName).ConfigureAwait(true);
        UiLogSink.Instance.Append(exit == 0
            ? $"schedule run: '{row.FullName}'"
            : $"schedule run failed: '{row.FullName}' exit={exit}");
        await RefreshAsync().ConfigureAwait(true);
    }

    private async Task DisableAsync(ScheduledTaskInfoVm row)
    {
        var ok = System.Windows.MessageBox.Show(
            Loc.F("Sched_DisableConfirmText", row.FullName),
            Loc.T("Sched_DisableConfirmTitle"),
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question) == System.Windows.MessageBoxResult.Yes;
        if (!ok)
        {
            return;
        }
        var exit = await _installer.DisableAsync(row.FullName).ConfigureAwait(true);
        UiLogSink.Instance.Append(exit == 0
            ? $"schedule disable: '{row.FullName}'"
            : $"schedule disable failed: '{row.FullName}' exit={exit}");
        await RefreshAsync().ConfigureAwait(true);
    }

    private async Task EnableAsync(ScheduledTaskInfoVm row)
    {
        var exit = await _installer.EnableAsync(row.FullName).ConfigureAwait(true);
        UiLogSink.Instance.Append(exit == 0
            ? $"schedule enable: '{row.FullName}'"
            : $"schedule enable failed: '{row.FullName}' exit={exit}");
        await RefreshAsync().ConfigureAwait(true);
    }

    private async Task DeleteAsync(ScheduledTaskInfoVm row)
    {
        var ok = System.Windows.MessageBox.Show(
            Loc.F("Sched_DeleteConfirmText", row.FullName),
            Loc.T("Sched_DeleteConfirmTitle"),
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning) == System.Windows.MessageBoxResult.Yes;
        if (!ok)
        {
            return;
        }
        var exit = await _installer.DeleteAsync(row.FullName).ConfigureAwait(true);
        UiLogSink.Instance.Append(exit == 0
            ? $"schedule delete: '{row.FullName}'"
            : $"schedule delete failed: '{row.FullName}' exit={exit}");
        await RefreshAsync().ConfigureAwait(true);
    }

    private async Task ViewXmlAsync(ScheduledTaskInfoVm row)
    {
        var (exit, xml) = await _installer.GetTaskXmlAsync(row.FullName).ConfigureAwait(true);
        if (exit != 0 || string.IsNullOrWhiteSpace(xml))
        {
            System.Windows.MessageBox.Show(Loc.F("Sched_XmlError", row.FullName, exit), Loc.T("Sched_XmlTitle"), System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            return;
        }
        try
        {
            System.Windows.Clipboard.SetText(xml);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Clipboard occasionally throws on contested access; the XML is shown in the dialog regardless.
        }
        System.Windows.MessageBox.Show(
            xml.Length > 4000 ? xml[..4000] + Loc.T("Sched_XmlTruncated") : xml,
            Loc.F("Sched_XmlDialogTitle", row.FullName),
            System.Windows.MessageBoxButton.OK,
            System.Windows.MessageBoxImage.Information);
    }
}
