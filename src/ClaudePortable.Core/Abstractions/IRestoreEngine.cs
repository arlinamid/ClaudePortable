using ClaudePortable.Core.Manifest;
using ClaudePortable.Core.Restore;

namespace ClaudePortable.Core.Abstractions;

/// <param name="Groups">
/// Source groups to restore (see SourceGroups); null restores everything in
/// the backup.
/// </param>
public sealed record RestoreRequest(
    string SourceZipPath,
    string? TargetUserProfile = null,
    bool Confirmed = false,
    bool IgnoreVersionMismatch = false,
    IReadOnlySet<string>? Groups = null);

public sealed record RestoreTargetReport(
    string ArchivePrefix,
    string TargetFolder,
    bool SafetyBackedUp,
    string? SafetyBackupPath,
    int FilesWritten,
    IReadOnlyList<string> Warnings);

public sealed record RestoreOutcome(
    BackupManifest Manifest,
    IReadOnlyList<string> SafetyBackups,
    string PostRestoreChecklistPath,
    VersionGateResult VersionGate,
    IReadOnlyList<RestoreTargetReport> PerTargetReports);

public interface IRestoreEngine
{
    Task<RestoreOutcome> RestoreAsync(
        RestoreRequest request,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
