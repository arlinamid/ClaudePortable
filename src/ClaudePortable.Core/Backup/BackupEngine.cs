using System.Runtime.Versioning;
using ClaudePortable.Core.Abstractions;
using ClaudePortable.Core.Archive;
using ClaudePortable.Core.Discovery;
using ClaudePortable.Core.Manifest;
using ClaudePortable.Core.Post;

namespace ClaudePortable.Core.Backup;

[SupportedOSPlatform("windows")]
public sealed class BackupEngine : IBackupEngine
{
    private const string CoworkProjectKeyPrefix = "coworkProject:";
    private const string CoworkProjectArchivePrefix = "cowork-projects/";

    private readonly IPathDiscovery _pathDiscovery;
    private readonly IArchiveWriter _archiveWriter;
    private readonly ICoworkProjectDiscovery _coworkDiscovery;
    private readonly TimeProvider _clock;

    public BackupEngine(
        IPathDiscovery pathDiscovery,
        IArchiveWriter archiveWriter,
        ICoworkProjectDiscovery? coworkDiscovery = null,
        TimeProvider? clock = null)
    {
        _pathDiscovery = pathDiscovery;
        _archiveWriter = archiveWriter;
        _coworkDiscovery = coworkDiscovery ?? new CoworkProjectDiscovery();
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<BackupOutcome> CreateBackupAsync(
        BackupRequest request,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var destination = request.DestinationFolder
            ?? throw new ArgumentException("DestinationFolder is required.", nameof(request));

        Directory.CreateDirectory(destination);

        progress?.Report(new OperationProgress("Discovering agent paths"));
        // Sources outside the selection are left out entirely: not backed
        // up, not reported as missing, not recorded in the manifest.
        var discovered = _pathDiscovery.Discover()
            .Where(p => SourceGroups.IsSelected(SourceGroups.ForSourceKey(p.Key), request.Groups))
            .ToList();
        var existingPaths = discovered.Where(p => p.Exists).ToList();
        var skippedPaths = discovered.Where(p => !p.Exists).ToList();
        var coworkSelected = SourceGroups.IsSelected([SourceGroups.Cowork], request.Groups);
        if (existingPaths.Count == 0 && !coworkSelected)
        {
            throw new InvalidOperationException(request.Groups is null
                ? "No Claude or Codex paths found on this machine. Nothing to back up."
                : $"Nothing to back up for the selected groups ({string.Join(", ", request.Groups)}) on this machine.");
        }

        // Cowork project folders (userSelectedFolders inside every Cowork
        // session's metadata). Each one becomes its own backup source with
        // archive prefix "cowork-projects/<hash>/".
        progress?.Report(new OperationProgress("Discovering Cowork projects"));
        var coworkProjects = coworkSelected
            ? _coworkDiscovery.Discover()
            : Array.Empty<CoworkProjectFolder>();
        if (existingPaths.Count == 0 && coworkProjects.Count == 0)
        {
            throw new InvalidOperationException("No Cowork project folders found on this machine. Nothing to back up.");
        }

        var exclusions = new ExclusionGlob(DefaultExclusions.Globs);
        var filesPerSource = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var archiveTargets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<ArchiveEntry>();
        var links = new List<BackupLink>();

        foreach (var p in existingPaths)
        {
            progress?.Report(new OperationProgress($"Enumerating {p.Key}"));
            var before = entries.Count;
            var archivePrefix = SourceLayout.PrefixFor(p.Key);
            entries.AddRange(FileEnumerator.Enumerate(p.Path, archivePrefix, exclusions, links));
            filesPerSource[p.Key] = entries.Count - before;
            archiveTargets[archivePrefix] = p.Path;
        }

        foreach (var project in coworkProjects)
        {
            progress?.Report(new OperationProgress($"Enumerating Cowork project {project.Hash}"));
            var key = CoworkProjectKeyPrefix + project.Hash;
            var archivePrefix = CoworkProjectArchivePrefix + project.Hash;
            var before = entries.Count;
            entries.AddRange(FileEnumerator.Enumerate(project.Path, archivePrefix, exclusions, links));
            filesPerSource[key] = entries.Count - before;
            archiveTargets[archivePrefix] = project.Path;
        }

        var createdAt = _clock.GetUtcNow();
        var filename = BuildFilename(createdAt, request.Tier, SourceGroups.FilenameTag(request.Groups));
        var zipPath = Path.Combine(destination, filename);

        // Compute approximate size + count BEFORE archive write so the
        // manifest embedded in the zip reports the real shape. The archive
        // writer may later drop locked / unreadable files; the zip's
        // manifest will then slightly over-count, which is preferable to
        // the old behaviour (always zeroed) because humans can see that
        // the backup intended to cover ~10k files even if a handful got
        // skipped at write time.
        var approxFileCount = entries.Count + 1; // + checklist
        var approxSizeBytes = entries.Sum(e => TryGetFileSize(e.AbsolutePath));

        var manifestSeed = ManifestBuilder.Build(
            discovered,
            DefaultExclusions.Globs,
            request.Tier,
            createdAt,
            sizeBytes: approxSizeBytes,
            fileCount: approxFileCount,
            claudeDesktopVersion: ClaudeDesktopVersionReader.TryRead(),
            coworkProjects: coworkProjects,
            archiveTargets: archiveTargets,
            links: links,
            groups: request.Groups);

        if (request.DryRun)
        {
            return new BackupOutcome(zipPath, manifestSeed, WasDryRun: true, skippedPaths, filesPerSource);
        }

        var checklistEntry = BuildChecklistEntry();
        var combinedEntries = entries.Append(checklistEntry).ToList();
        try
        {
            var manifestJson = ManifestBuilder.Serialize(manifestSeed);
            var archiveResult = await _archiveWriter
                .WriteAsync(zipPath, combinedEntries, manifestJson, progress, cancellationToken)
                .ConfigureAwait(false);

            var finalManifest = manifestSeed with
            {
                SizeBytes = archiveResult.SizeBytes,
                FileCount = archiveResult.FileCount,
                Sha256 = archiveResult.Sha256,
            };

            return new BackupOutcome(zipPath, finalManifest, WasDryRun: false, skippedPaths, filesPerSource);
        }
        finally
        {
            if (File.Exists(checklistEntry.AbsolutePath))
            {
                try
                {
                    File.Delete(checklistEntry.AbsolutePath);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    private static long TryGetFileSize(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static string BuildFilename(DateTimeOffset timestamp, RetentionTier tier, string? selectionTag)
    {
        var iso = timestamp.UtcDateTime.ToString("yyyy-MM-ddTHH-mm-ssZ", System.Globalization.CultureInfo.InvariantCulture);
        var tierLower = tier.ToString().ToLowerInvariant();
        // Partial backups carry their selection so they are recognisable in
        // the folder: claude-backup_<ts>_<host>_codex_daily.zip. The tier
        // stays the last segment, which retention renames rely on.
        var tag = selectionTag is null ? string.Empty : $"_{selectionTag}";
        return $"claude-backup_{iso}_{Environment.MachineName}{tag}_{tierLower}.zip";
    }

    private static ArchiveEntry BuildChecklistEntry()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"post-restore-{Guid.NewGuid():N}.md");
        File.WriteAllText(tempFile, PostRestoreChecklistBuilder.Build());
        return new ArchiveEntry("post-restore-checklist.md", tempFile);
    }
}
