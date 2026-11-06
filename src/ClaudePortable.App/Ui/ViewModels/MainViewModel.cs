using System.Collections.ObjectModel;
using System.Reflection;
using ClaudePortable.App.Localization;
using ClaudePortable.App.Logging;
using System.Runtime.Versioning;
using ClaudePortable.App.Ui.Services;
using ClaudePortable.Core.Abstractions;
using ClaudePortable.Core.Archive;
using ClaudePortable.Core.Backup;
using ClaudePortable.Core.Discovery;
using ClaudePortable.Core.Manifest;
using ClaudePortable.Core.Restore;
using ClaudePortable.Scheduler.Retention;
using ClaudePortable.Scheduler.Scheduling;
using ClaudePortable.Targets;
using ClaudePortable.Targets.Models;

namespace ClaudePortable.App.Ui.ViewModels;

[SupportedOSPlatform("windows")]
public sealed class MainViewModel : ViewModelBase
{
    private const string DefaultBackupFolderName = "ClaudePortable";

    private readonly TargetStore _store = new();
    private readonly SettingsStore _settings = new();
    private string _status = Loc.T("Vm_Ready");
    private string _targetUserProfileOverride = string.Empty;
    private bool _ignoreVersionMismatch;
    private bool _closeAppsDuringBackup;
    private bool _isBusy;
    private string _progressMessage = string.Empty;
    private double _progressValue;
    private bool _progressIsIndeterminate = true;
    private CancellationTokenSource? _backupCts;

    public ObservableCollection<TargetEntry> Targets { get; } = new();
    public ObservableCollection<BackupEntry> Backups { get; } = new();
    public ObservableCollection<DiscoveredPathRow> ClaudePaths { get; } = new();

    /// <summary>"What to back up" checkboxes; persisted in settings.json and
    /// also used when installing the scheduled backup task.</summary>
    public ObservableCollection<GroupToggle> BackupGroups { get; } = new();

    /// <summary>"What to restore" checkboxes; all ticked by default.</summary>
    public ObservableCollection<GroupToggle> RestoreGroups { get; } = new();
    public ObservableCollection<DiscoveredSyncClient> SyncClients { get; } = new();
    public ScheduledTasksViewModel ScheduledTasks { get; } = new(new TaskSchedulerInstaller());

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822", Justification = "Instance binding target for XAML.")]
    public LogsViewModel Logs => UiLogSink.Instance.Viewer;

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822", Justification = "Instance binding target for XAML.")]
    public string AppVersionLabel
    {
        get
        {
            var asm = Assembly.GetExecutingAssembly();
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            var version = string.IsNullOrWhiteSpace(info)
                ? asm.GetName().Version?.ToString(3) ?? "0.0.0"
                : info!.Split('+')[0];
            return $"v{version} · alpha";
        }
    }

    public string Status
    {
        get => _status;
        set => SetField(ref _status, value);
    }

    /// <summary>
    /// Optional override for the restore destination root. When set, the
    /// RestoreEngine routes %USERPROFILE%, %APPDATA%, and %LOCALAPPDATA%
    /// under this path instead of the current user's home. Leave empty to
    /// use the process's %USERPROFILE%.
    /// </summary>
    public string TargetUserProfileOverride
    {
        get => _targetUserProfileOverride;
        set => SetField(ref _targetUserProfileOverride, value);
    }

    public bool IgnoreVersionMismatch
    {
        get => _ignoreVersionMismatch;
        set => SetField(ref _ignoreVersionMismatch, value);
    }

    /// <summary>
    /// Close Claude Desktop / the Codex app (with their background helpers)
    /// for a backup and reopen them afterwards, without asking. Persisted in
    /// settings.json; also passed to the scheduled task as --close-apps.
    /// </summary>
    public bool CloseAppsDuringBackup
    {
        get => _closeAppsDuringBackup;
        set
        {
            if (SetField(ref _closeAppsDuringBackup, value))
            {
                _settings.SaveCloseAppsDuringBackup(value);
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (SetField(ref _isBusy, value))
            {
                Raise(nameof(IsNotBusy));
            }
        }
    }

    public bool IsNotBusy => !IsBusy;

    /// <summary>
    /// The folder "Backup now" writes into: the first (top) configured target.
    /// "Set as active" moves a target to the top, and the list order persists
    /// in targets.json, so this survives restarts.
    /// </summary>
    public string ActiveTargetPath => Targets.FirstOrDefault()?.Path ?? Loc.T("Vm_ActiveTargetNone");

    public string ProgressMessage
    {
        get => _progressMessage;
        set => SetField(ref _progressMessage, value);
    }

    public double ProgressValue
    {
        get => _progressValue;
        set => SetField(ref _progressValue, value);
    }

    public bool ProgressIsIndeterminate
    {
        get => _progressIsIndeterminate;
        set => SetField(ref _progressIsIndeterminate, value);
    }

    public AsyncRelayCommand BackupNowCommand { get; }
    public RelayCommand CancelBackupCommand { get; }

    /// <summary>True while a backup runs; shows the Cancel button.</summary>
    public bool IsBackupRunning => _backupCts is not null;
    public AsyncRelayCommand RefreshCommand { get; }
    public RelayCommand AddTargetCommand { get; }
    public RelayCommand RemoveTargetCommand { get; }
    public RelayCommand SetActiveTargetCommand { get; }
    public AsyncRelayCommand RestoreCommand { get; }
    public AsyncRelayCommand RestoreFromFileCommand { get; }
    public RelayCommand PickTargetProfileCommand { get; }
    public RelayCommand OpenChecklistCommand { get; }
    public AsyncRelayCommand RepairProfilePathsCommand { get; }

    private TargetEntry? _selectedTarget;

    public TargetEntry? SelectedTarget
    {
        get => _selectedTarget;
        set
        {
            if (SetField(ref _selectedTarget, value))
            {
                RemoveTargetCommand?.RaiseCanExecuteChanged();
                SetActiveTargetCommand?.RaiseCanExecuteChanged();
            }
        }
    }

    private BackupEntry? _selectedBackup;

    public BackupEntry? SelectedBackup
    {
        get => _selectedBackup;
        set
        {
            if (SetField(ref _selectedBackup, value))
            {
                RestoreCommand?.RaiseCanExecuteChanged();
            }
        }
    }

    private string _postRestoreChecklistPath = string.Empty;

    public string PostRestoreChecklistPath
    {
        get => _postRestoreChecklistPath;
        private set => SetField(ref _postRestoreChecklistPath, value);
    }

    public MainViewModel()
    {
        foreach (var path in _store.Load())
        {
            Targets.Add(new TargetEntry(path));
        }

        var discoveredCount = AutoDiscoverSyncedTargets();
        if (Targets.Count == 0)
        {
            var suggested = SuggestDefaultTarget();
            if (suggested is not null)
            {
                Targets.Add(new TargetEntry(suggested));
                _store.Save(Targets.Select(t => t.Path).ToArray());
            }
        }
        if (discoveredCount > 0)
        {
            _store.Save(Targets.Select(t => t.Path).ToArray());
            UiLogSink.Instance.Append($"auto-discovered {discoveredCount} ClaudePortable folder(s) from sync clients.");
        }

        _closeAppsDuringBackup = _settings.Load().CloseAppsDuringBackup ?? true;
        var savedGroups = _settings.LoadBackupGroups();
        foreach (var id in SourceGroups.All)
        {
            BackupGroups.Add(new GroupToggle(id, savedGroups is null || savedGroups.Contains(id), OnBackupGroupsChanged));
            RestoreGroups.Add(new GroupToggle(id, isChecked: true));
        }

        BackupNowCommand = new AsyncRelayCommand(BackupNowAsync);
        CancelBackupCommand = new RelayCommand(
            () =>
            {
                _backupCts?.Cancel();
                ProgressMessage = Loc.T("Vm_Cancelling");
            },
            () => _backupCts is not null);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        AddTargetCommand = new RelayCommand(AddTarget);
        RemoveTargetCommand = new RelayCommand(RemoveTarget, () => SelectedTarget is not null);
        SetActiveTargetCommand = new RelayCommand(
            SetActiveTarget,
            () => SelectedTarget is not null && Targets.Count > 0 && !ReferenceEquals(Targets[0], SelectedTarget));
        RestoreCommand = new AsyncRelayCommand(RestoreAsync, () => SelectedBackup is not null);
        RestoreFromFileCommand = new AsyncRelayCommand(RestoreFromFileAsync);
        PickTargetProfileCommand = new RelayCommand(PickTargetProfile);
        OpenChecklistCommand = new RelayCommand(OpenChecklist, () => !string.IsNullOrEmpty(PostRestoreChecklistPath));
        RepairProfilePathsCommand = new AsyncRelayCommand(RepairProfilePathsAsync);

        ScheduledTasks.ActiveTargetProvider = () => Targets.FirstOrDefault()?.Path;
        ScheduledTasks.BackupGroupsProvider = () => GroupToggle.ToSelection(BackupGroups);
        ScheduledTasks.CloseAppsProvider = () => CloseAppsDuringBackup;

        Raise(nameof(ActiveTargetPath));
        _ = RefreshAsync();
        _ = Logs.LoadHistoryAsync();
    }

    public void PickTargetProfile()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = Loc.T("Dlg_PickProfileTitle"),
            InitialDirectory = @"C:\Users",
        };
        if (dlg.ShowDialog() == true)
        {
            TargetUserProfileOverride = dlg.FolderName;
        }
    }

    /// <summary>
    /// Called after the UI language switched: re-evaluate computed strings
    /// and reload the lists so cached row labels (e.g. StatusLabel) pick up
    /// the new language. An idle status is reset to the localized "Ready.".
    /// </summary>
    public void OnLanguageChanged()
    {
        Raise(nameof(ActiveTargetPath));
        foreach (var toggle in BackupGroups.Concat(RestoreGroups))
        {
            toggle.RefreshLanguage();
        }
        if (!IsBusy)
        {
            Status = Loc.T("Vm_Ready");
        }
        _ = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        ClaudePaths.Clear();
        foreach (var p in new WindowsPathDiscovery().Discover())
        {
            ClaudePaths.Add(new DiscoveredPathRow(GroupLabels.ForSourceKey(p.Key), p.Key, p.Path, p.Exists));
        }
        SyncClients.Clear();
        foreach (var c in new SyncClientDiscovery().Discover())
        {
            SyncClients.Add(c);
        }
        Backups.Clear();
        foreach (var t in Targets)
        {
            var target = new FolderTarget(t.Path);
            foreach (var b in target.ListBackups())
            {
                Backups.Add(new BackupEntry(t.Path, b.FileName, b.FullPath, b.SizeBytes, b.Manifest, b.IsCloudOnly));
            }
        }
        await Task.CompletedTask.ConfigureAwait(true);
    }

    public async Task BackupNowAsync()
    {
        if (Targets.Count == 0)
        {
            Status = Loc.T("Vm_NoTarget");
            return;
        }
        if (!BackupGroups.Any(g => g.IsChecked))
        {
            Status = Loc.T("Vm_NothingSelected");
            return;
        }
        var groups = GroupToggle.ToSelection(BackupGroups);
        var target = Targets.First();
        var run = UiLogSink.Instance.BeginRun("backup", target.Path);
        var closed = await CloseAgentsForBackupAsync(groups, run).ConfigureAwait(true);
        if (closed is null)
        {
            run.Info("Cancelled before starting: running apps were not closed");
            UiLogSink.Instance.EndRun(run, RunStatus.Cancelled);
            Status = Loc.T("Vm_BackupCancelled");
            return;
        }
        Status = Loc.T("Vm_BackingUp");
        BeginBusy(Loc.T("Vm_StartingBackup"));
        var progress = CreateProgress();
        using var cts = new CancellationTokenSource();
        SetBackupCts(cts);
        RunStatus? finalStatus = null;
        string? summary = null;
        try
        {
            var outcome = await Task.Run(async () =>
            {
                var engine = new BackupEngine(new WindowsPathDiscovery(), new ZipArchiveWriter(new OperationLogWriter(run)), log: run);
                return await engine.CreateBackupAsync(
                        new BackupRequest(target.Path, RetentionTier.Daily, Groups: groups),
                        progress,
                        cts.Token)
                    .ConfigureAwait(false);
            }, cts.Token).ConfigureAwait(true);

            ProgressMessage = Loc.T("Vm_RotatingRetention");
            ProgressIsIndeterminate = true;
            var report = await Task.Run(() => new RetentionManager().Rotate(new FolderTarget(target.Path))).ConfigureAwait(true);
            RotationLog.Write(run, report);

            var skippedFiles = run.WarningsIn(LogSections.Archive);
            summary = $"{Path.GetFileName(outcome.ZipPath)}: {outcome.Manifest.FileCount:N0} files, {OperationLogExtensions.Bytes(outcome.Manifest.SizeBytes)}";
            Status = skippedFiles == 0
                ? Loc.T("Vm_BackupComplete")
                : Loc.F("Vm_BackupCompleteSkipped", skippedFiles);
        }
        catch (OperationCanceledException)
        {
            finalStatus = RunStatus.Cancelled;
            Status = Loc.T("Vm_BackupCancelled");
        }
        catch (Exception ex)
        {
            run.Error($"Backup failed: {ex.Message}");
            finalStatus = RunStatus.Failed;
            Status = Loc.F("Vm_BackupFailed", ex.Message);
        }
        finally
        {
            SetBackupCts(null);
            EndBusy();
            await RestartServicesAsync(closed, run).ConfigureAwait(true);
            if (closed.Closed.Count > 0)
            {
                AgentProcesses.Reopen(closed.Closed, run);
            }
            UiLogSink.Instance.EndRun(run, finalStatus, summary);
        }
        await RefreshAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Before a backup: close the running desktop apps of the selected
    /// groups when the setting says so or the user agrees. Returns what to
    /// restart afterwards, or null when the user cancelled the backup.
    /// </summary>
    private async Task<CloseOutcome?> CloseAgentsForBackupAsync(IReadOnlySet<string>? groups, RunLog run)
    {
        var log = run.In(LogSections.RunningApps);
        var running = AgentProcesses.Find(AgentProcesses.KindsToClose(groups, AgentOperation.Backup));
        if (running.Count == 0)
        {
            log.Info("No Claude Desktop or Codex app running");
            return CloseOutcome.None;
        }

        var close = CloseAppsDuringBackup;
        if (!close)
        {
            var answer = System.Windows.MessageBox.Show(
                Loc.F("Dlg_BackupAgentsText", AgentLabels.Summarize(running)),
                Loc.T("Dlg_BackupAgentsTitle"),
                System.Windows.MessageBoxButton.YesNoCancel,
                System.Windows.MessageBoxImage.Question);
            if (answer == System.Windows.MessageBoxResult.Cancel)
            {
                return null;
            }
            close = answer == System.Windows.MessageBoxResult.Yes;
        }
        if (!close)
        {
            log.Warn($"Backing up while running (chosen in the dialog): {AgentProcesses.Summarize(running)}");
            return CloseOutcome.None;
        }

        BeginBusy(Loc.T("Vm_ClosingAgents"));
        try
        {
            return await AgentProcesses.CloseAsync(running, TimeSpan.FromSeconds(5), log: run, allowElevation: true).ConfigureAwait(true);
        }
        finally
        {
            EndBusy();
        }
    }

    /// <summary>Start the apps' services stopped by <see cref="AgentProcesses.CloseAsync"/> again (UAC prompt if they need it).</summary>
    private static async Task RestartServicesAsync(CloseOutcome closed, RunLog run)
    {
        if (closed.StoppedServices.Count > 0)
        {
            await AgentServices.StartAsync(closed.StoppedServices, allowElevation: true, run).ConfigureAwait(true);
        }
    }

    public void AddTarget()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = Loc.T("Dlg_AddTargetTitle"),
        };
        if (dlg.ShowDialog() == true)
        {
            if (!Targets.Any(t => string.Equals(t.Path, dlg.FolderName, StringComparison.OrdinalIgnoreCase)))
            {
                Targets.Add(new TargetEntry(dlg.FolderName));
                _store.Save(Targets.Select(t => t.Path).ToArray());
                Raise(nameof(ActiveTargetPath));
                _ = RefreshAsync();
            }
        }
    }

    public void RemoveTarget()
    {
        if (SelectedTarget is null)
        {
            return;
        }
        Targets.Remove(SelectedTarget);
        _store.Save(Targets.Select(t => t.Path).ToArray());
        Raise(nameof(ActiveTargetPath));
        SetActiveTargetCommand.RaiseCanExecuteChanged();
        _ = RefreshAsync();
    }

    /// <summary>
    /// Move the selected target to the top of the list so it becomes the
    /// active backup destination, and persist the new order.
    /// </summary>
    public void SetActiveTarget()
    {
        if (SelectedTarget is null)
        {
            return;
        }
        var index = Targets.IndexOf(SelectedTarget);
        if (index <= 0)
        {
            return;
        }
        Targets.Move(index, 0);
        _store.Save(Targets.Select(t => t.Path).ToArray());
        Raise(nameof(ActiveTargetPath));
        SetActiveTargetCommand.RaiseCanExecuteChanged();
        _ = RefreshAsync();
    }

    public async Task RestoreAsync()
    {
        if (SelectedBackup is null)
        {
            return;
        }
        if (SelectedBackup.IsCloudOnly)
        {
            var proceed = System.Windows.MessageBox.Show(
                Loc.F("Dlg_CloudOnlyText", SelectedBackup.FileName),
                Loc.T("Dlg_CloudOnlyTitle"),
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Information);
            if (proceed != System.Windows.MessageBoxResult.Yes)
            {
                return;
            }
        }
        await RunRestoreAsync(SelectedBackup.FullPath, SelectedBackup.FileName).ConfigureAwait(true);
    }

    public async Task RestoreFromFileAsync()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.T("Dlg_PickZipTitle"),
            Filter = Loc.T("Dlg_ZipFilter"),
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dlg.ShowDialog() != true)
        {
            return;
        }
        await RunRestoreAsync(dlg.FileName, Path.GetFileName(dlg.FileName)).ConfigureAwait(true);
    }

    private async Task RunRestoreAsync(string zipPath, string displayName)
    {
        var confirm = System.Windows.MessageBox.Show(
            Loc.F("Dlg_ConfirmRestoreText", displayName),
            Loc.T("Dlg_ConfirmRestoreTitle"),
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);
        if (confirm != System.Windows.MessageBoxResult.Yes)
        {
            return;
        }

        if (!RestoreGroups.Any(g => g.IsChecked))
        {
            Status = Loc.T("Vm_NothingSelected");
            return;
        }
        var groups = GroupToggle.ToSelection(RestoreGroups);
        var restoring = RestoreEngine.GroupsToRestore(zipPath, groups);
        if (restoring.Count == 0)
        {
            Status = Loc.T("Vm_RestoreNothingInBackup");
            return;
        }

        var run = UiLogSink.Instance.BeginRun("restore", displayName);
        var closed = await EnsureAgentsClosedAsync(AgentProcesses.KindsToClose(restoring, AgentOperation.Restore), run).ConfigureAwait(true);
        if (closed is null)
        {
            UiLogSink.Instance.EndRun(run, RunStatus.Cancelled);
            return;
        }

        Status = Loc.T("Vm_Restoring");
        BeginBusy(Loc.T("Vm_StartingRestore"));
        var progress = CreateProgress();
        RunStatus? finalStatus = null;
        string? summary = null;
        try
        {
            var targetOverride = string.IsNullOrWhiteSpace(TargetUserProfileOverride) ? null : TargetUserProfileOverride.Trim();
            var outcome = await Task.Run(async () =>
            {
                var engine = new RestoreEngine(new PathRewriter(), log: run);
                return await engine.RestoreAsync(
                        new RestoreRequest(
                            zipPath,
                            TargetUserProfile: targetOverride,
                            Confirmed: true,
                            IgnoreVersionMismatch: IgnoreVersionMismatch,
                            Groups: groups),
                        progress)
                    .ConfigureAwait(false);
            }).ConfigureAwait(true);

            var totalWarnings = outcome.PerTargetReports.Sum(r => r.Warnings.Count(w => !w.StartsWith("Not present", StringComparison.Ordinal)));
            summary = $"{outcome.PerTargetReports.Sum(r => r.FilesWritten):N0} files restored from {outcome.Manifest.Hostname}; checklist {outcome.PostRestoreChecklistPath}";
            PostRestoreChecklistPath = outcome.PostRestoreChecklistPath;
            Status = totalWarnings == 0
                ? Loc.F("Vm_RestoreCompleteChecklist", outcome.PostRestoreChecklistPath)
                : Loc.F("Vm_RestoreCompleteWarnings", totalWarnings);
        }
        catch (Exception ex)
        {
            // The engine has logged the details; make sure the run says it failed.
            if (run.ErrorCount == 0)
            {
                run.Error($"Restore failed: {ex.Message}");
            }
            finalStatus = RunStatus.Failed;
            Status = Loc.F("Vm_RestoreFailed", ex.Message);
        }
        finally
        {
            EndBusy();
            await RestartServicesAsync(closed, run).ConfigureAwait(true);
            UiLogSink.Instance.EndRun(run, finalStatus, summary);
        }
        await RefreshAsync().ConfigureAwait(true);
    }

    private void SetBackupCts(CancellationTokenSource? cts)
    {
        _backupCts = cts;
        Raise(nameof(IsBackupRunning));
        CancelBackupCommand.RaiseCanExecuteChanged();
    }

    private void BeginBusy(string initialMessage)
    {
        IsBusy = true;
        ProgressIsIndeterminate = true;
        ProgressValue = 0;
        ProgressMessage = initialMessage;
    }

    private void EndBusy()
    {
        IsBusy = false;
        ProgressIsIndeterminate = false;
        ProgressValue = 0;
        ProgressMessage = string.Empty;
    }

    private Progress<OperationProgress> CreateProgress()
    {
        // Progress<T> captures the current SynchronizationContext so the
        // callback is marshalled back to the UI thread.
        return new Progress<OperationProgress>(p =>
        {
            if (p.Fraction is { } f)
            {
                ProgressIsIndeterminate = false;
                ProgressValue = f;
                ProgressMessage = $"{p.Phase} ({p.Current} / {p.Total}, {f * 100:F0}%)";
            }
            else
            {
                ProgressIsIndeterminate = true;
                ProgressMessage = p.Phase;
            }
        });
    }

    /// <summary>
    /// Ask to close the running agents of the given kinds (whole process
    /// trees, background helpers and services included) and close them.
    /// Returns the services to start again afterwards; null, with the status
    /// line set, when the user declined or something is still running.
    /// </summary>
    private async Task<CloseOutcome?> EnsureAgentsClosedAsync(IReadOnlySet<AgentProcessKind> kinds, RunLog run)
    {
        var log = run.In(LogSections.RunningApps);
        var running = AgentProcesses.Find(kinds);
        if (running.Count == 0)
        {
            log.Info($"Nothing running that has to be closed ({string.Join(", ", kinds.Select(AgentProcesses.Describe))})");
            return CloseOutcome.None;
        }

        var result = System.Windows.MessageBox.Show(
            Loc.F("Dlg_CloseAgentsText", AgentLabels.Summarize(running)),
            Loc.T("Dlg_CloseAgentsTitle"),
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);
        if (result != System.Windows.MessageBoxResult.Yes)
        {
            log.Info($"Cancelled: still running, user chose not to close {AgentProcesses.Summarize(running)}");
            Status = Loc.F("Vm_CancelledAgentsRunning", AgentLabels.Summarize(running));
            return null;
        }

        var closed = await AgentProcesses.CloseAsync(running, TimeSpan.FromSeconds(5), log: run, allowElevation: true).ConfigureAwait(true);
        var still = AgentProcesses.Find(kinds);
        if (still.Count > 0)
        {
            log.Error($"Cancelled: could not close {AgentProcesses.Summarize(still)}");
            Status = Loc.F("Vm_CancelledAgentsRunning", AgentLabels.Summarize(still));
            await RestartServicesAsync(closed, run).ConfigureAwait(true);
            return null;
        }
        return closed;
    }

    /// <summary>
    /// Points Codex / Claude Code state that still references another user's
    /// profile (e.g. restored from a laptop "Janos" onto "János") at the
    /// current profile. Same as `claudeportable repair-paths --yes`.
    /// </summary>
    public async Task RepairProfilePathsAsync()
    {
        var current = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var run = UiLogSink.Instance.BeginRun("repair", current);
        BeginBusy(Loc.T("Vm_RepairScanning"));
        IReadOnlyList<(string Key, string Folder)> folders;
        IReadOnlyList<ProfilePathRepair.ForeignProfile> profiles;
        try
        {
            folders = ProfilePathRepair.AgentFolders(new WindowsPathDiscovery());
            foreach (var (key, folder) in folders)
            {
                run.In(LogSections.Paths).Info($"Scanning {key}: {folder}");
            }
            profiles = await Task.Run(() => ProfilePathRepair.Detect(folders.Select(f => f.Folder), current)).ConfigureAwait(true);
        }
        finally
        {
            EndBusy();
        }

        if (profiles.Count == 0)
        {
            run.Info($"No paths into other user profiles found; everything already points to {current}");
            UiLogSink.Instance.EndRun(run, RunStatus.Succeeded, "Nothing to repair");
            Status = Loc.F("Vm_RepairNothing", current);
            return;
        }
        foreach (var p in profiles)
        {
            run.In(LogSections.Paths).Info($"Found {p.Occurrences:N0} path(s) into {p.ProfileRoot}");
        }

        var list = string.Join(Environment.NewLine, profiles.Select(p => $"  {p.ProfileRoot}  ({p.Occurrences})"));
        var confirm = System.Windows.MessageBox.Show(
            Loc.F("Dlg_RepairText", list, current),
            Loc.T("Dlg_RepairTitle"),
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question);
        if (confirm != System.Windows.MessageBoxResult.Yes)
        {
            run.Info("Cancelled in the confirmation dialog");
            UiLogSink.Instance.EndRun(run, RunStatus.Cancelled);
            return;
        }
        var closed = await EnsureAgentsClosedAsync(AgentProcesses.KindsToClose(null, AgentOperation.RepairPaths), run).ConfigureAwait(true);
        if (closed is null)
        {
            UiLogSink.Instance.EndRun(run, RunStatus.Cancelled);
            return;
        }

        var backupRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClaudePortable",
            $"path-repair-{DateTime.UtcNow.ToString("yyyy-MM-dd-HHmmss", System.Globalization.CultureInfo.InvariantCulture)}");
        BeginBusy(Loc.T("Vm_RepairRunning"));
        var progress = CreateProgress();
        try
        {
            var result = await Task.Run(() => ProfilePathRepair.Repair(folders, profiles, current, backupRoot, progress)).ConfigureAwait(true);
            await RestartServicesAsync(closed, run).ConfigureAwait(true);
            run.Info($"{string.Join(", ", profiles.Select(p => p.ProfileRoot))} -> {current}: {result.FilesChanged:N0} files, {result.ValuesChanged:N0} paths changed");
            run.In(LogSections.Paths).Info($"Originals of the changed files: {result.OriginalsBackupFolder ?? "(none changed)"}");
            foreach (var w in result.Warnings)
            {
                run.In(LogSections.Paths).Warn(w);
            }
            UiLogSink.Instance.EndRun(run, summary: $"{result.ValuesChanged:N0} paths in {result.FilesChanged:N0} files");
            Status = Loc.F("Vm_RepairDone", result.ValuesChanged, result.FilesChanged);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            run.Error($"Path repair failed: {ex.Message}");
            await RestartServicesAsync(closed, run).ConfigureAwait(true);
            UiLogSink.Instance.EndRun(run, RunStatus.Failed);
            Status = Loc.F("Vm_RepairFailed", ex.Message);
        }
        finally
        {
            EndBusy();
        }
    }

    private void OnBackupGroupsChanged()
    {
        var selection = GroupToggle.ToSelection(BackupGroups);
        // An empty selection is not saved: it would read back as "everything".
        if (BackupGroups.Any(g => g.IsChecked))
        {
            _settings.SaveBackupGroups(selection);
        }
    }

    private void OpenChecklist()
    {
        if (string.IsNullOrEmpty(PostRestoreChecklistPath) || !File.Exists(PostRestoreChecklistPath))
        {
            Status = Loc.T("Vm_NoChecklist");
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = PostRestoreChecklistPath,
                UseShellExecute = true,
            });
            Status = Loc.F("Vm_OpenedChecklist", PostRestoreChecklistPath);
        }
        catch (Exception ex)
        {
            Status = Loc.F("Vm_ChecklistError", ex.Message);
        }
    }

    /// <summary>
    /// For every detected sync client (OneDrive, Dropbox, GDrive Desktop, ...)
    /// check whether the user already has a folder called "ClaudePortable"
    /// inside it. If yes and it is not already tracked, add it. This is how a
    /// second machine (laptop) sees backups created on the first machine
    /// (workstation) without the user having to configure anything.
    /// </summary>
    private int AutoDiscoverSyncedTargets()
    {
        var added = 0;
        foreach (var client in new SyncClientDiscovery().Discover())
        {
            if (!client.IsAvailable || string.IsNullOrEmpty(client.Path) || !Directory.Exists(client.Path))
            {
                continue;
            }
            var candidate = Path.Combine(client.Path, DefaultBackupFolderName);
            if (!Directory.Exists(candidate))
            {
                continue;
            }
            if (Targets.Any(t => string.Equals(t.Path, candidate, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            Targets.Add(new TargetEntry(candidate));
            added++;
        }
        return added;
    }

    /// <summary>
    /// Fallback when no target is configured and no existing ClaudePortable
    /// folder was found: propose one inside the first available sync client.
    /// </summary>
    private static string? SuggestDefaultTarget()
    {
        foreach (var c in new SyncClientDiscovery().Discover())
        {
            if (c.IsAvailable && Directory.Exists(c.Path))
            {
                var proposed = Path.Combine(c.Path, DefaultBackupFolderName);
                Directory.CreateDirectory(proposed);
                return proposed;
            }
        }
        return null;
    }
}

public sealed record TargetEntry(string Path);

public sealed record DiscoveredPathRow(string Label, string Key, string Path, bool Exists);

public sealed record BackupEntry(
    string TargetFolder,
    string FileName,
    string FullPath,
    long SizeBytes,
    BackupManifest? Manifest,
    bool IsCloudOnly)
{
    public string StatusLabel => IsCloudOnly
        ? ClaudePortable.App.Localization.Loc.T("Label_CloudOnly")
        : Manifest is null
            ? ClaudePortable.App.Localization.Loc.T("Label_Unreadable")
            : ClaudePortable.App.Localization.Loc.T("Label_Synced");

    /// <summary>Agents with data in this backup, e.g. "Claude Code, Codex".</summary>
    public string ContentsLabel => Manifest is null
        ? string.Empty
        : GroupLabels.Join(SourceGroups.ContentsOf(Manifest));
}
