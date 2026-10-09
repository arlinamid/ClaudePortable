using ClaudePortable.Core.Manifest;

namespace ClaudePortable.Core.Abstractions;

/// <param name="Groups">
/// Source groups to include (see SourceGroups); null backs up everything.
/// </param>
public sealed record BackupRequest(
    string DestinationFolder,
    RetentionTier Tier = RetentionTier.Daily,
    bool DryRun = false,
    IReadOnlySet<string>? Groups = null);

public sealed record BackupOutcome(
    string ZipPath,
    BackupManifest Manifest,
    bool WasDryRun,
    IReadOnlyList<DiscoveredClaudePath> SkippedPaths,
    IReadOnlyDictionary<string, int> FilesPerSource);

public interface IBackupEngine
{
    Task<BackupOutcome> CreateBackupAsync(
        BackupRequest request,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
