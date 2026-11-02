using System.Diagnostics;
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
    private const string ExternalKeyPrefix = "external:";

    // An external folder bigger than this (after exclusions) is not
    // archived: it is listed in the manifest and the checklist instead, so a
    // reference to a large repository or media folder cannot balloon every
    // daily backup.
    private const long MaxExternalBytes = 1L << 30;
    private const int MaxExternalFiles = 20_000;

    // How many excluded files / pruned folders are listed per source in the
    // log file; the rest is counted.
    private const int MaxListedExclusions = 200;

    private readonly IPathDiscovery _pathDiscovery;
    private readonly IArchiveWriter _archiveWriter;
    private readonly ICoworkProjectDiscovery _coworkDiscovery;
    private readonly TimeProvider _clock;
    private readonly IOperationLog _log;

    public BackupEngine(
        IPathDiscovery pathDiscovery,
        IArchiveWriter archiveWriter,
        ICoworkProjectDiscovery? coworkDiscovery = null,
        TimeProvider? clock = null,
        IOperationLog? log = null)
    {
        _pathDiscovery = pathDiscovery;
        _archiveWriter = archiveWriter;
        _coworkDiscovery = coworkDiscovery ?? new CoworkProjectDiscovery();
        _clock = clock ?? TimeProvider.System;
        _log = log ?? NullOperationLog.Instance;
    }

    public async Task<BackupOutcome> CreateBackupAsync(
        BackupRequest request,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var destination = request.DestinationFolder
            ?? throw new ArgumentException("DestinationFolder is required.", nameof(request));
        var total = Stopwatch.StartNew();
        var phase = Stopwatch.StartNew();

        _log.Info($"Backup started{(request.DryRun ? " (dry run)" : string.Empty)}: destination {destination}, tier {request.Tier}, groups {(request.Groups is null ? "all" : string.Join(", ", request.Groups))}");
        Directory.CreateDirectory(destination);

        var appsLog = _log.In(LogSections.InstalledApps);
        var sourcesLog = _log.In(LogSections.Sources);
        var linksLog = _log.In(LogSections.Links);
        var externalLog = _log.In(LogSections.External);

        var installs = DetectInstalls();
        var claudeInstall = AgentInstalls.PrimaryClaudeDesktop(installs);
        if (installs.Count == 0)
        {
            appsLog.Info("No Claude Desktop or Codex desktop app found");
        }
        foreach (var install in installs)
        {
            appsLog.Info($"{(install.App == AgentApp.ClaudeDesktop ? "Claude Desktop" : "Codex app")} - {install.Edition} edition {install.Version}{(ReferenceEquals(install, claudeInstall) ? " (in use)" : string.Empty)} - {install.InstallRoot}");
        }

        progress?.Report(new OperationProgress("Discovering agent paths"));
        // Sources outside the selection are left out entirely: not backed
        // up, not reported as missing, not recorded in the manifest.
        var allDiscovered = _pathDiscovery.Discover();
        var discovered = allDiscovered
            .Where(p => SourceGroups.IsSelected(SourceGroups.ForSourceKey(p.Key), request.Groups))
            .ToList();
        foreach (var p in allDiscovered.Except(discovered))
        {
            sourcesLog.Debug($"{p.Key}: not selected ({p.Path})");
        }
        var existingPaths = discovered.Where(p => p.Exists).ToList();
        var skippedPaths = discovered.Where(p => !p.Exists).ToList();
        foreach (var p in skippedPaths)
        {
            sourcesLog.Info($"{p.Key}: not present on this PC (looked for {(string.IsNullOrEmpty(p.Path) ? "?" : p.Path)})");
        }
        var coworkSelected = SourceGroups.IsSelected([SourceGroups.Cowork], request.Groups);
        if (existingPaths.Count == 0 && !coworkSelected)
        {
            var message = request.Groups is null
                ? "No Claude or Codex paths found on this machine. Nothing to back up."
                : $"Nothing to back up for the selected groups ({string.Join(", ", request.Groups)}) on this machine.";
            _log.Error(message);
            throw new InvalidOperationException(message);
        }

        // Cowork project folders (userSelectedFolders inside every Cowork
        // session's metadata). Each one becomes its own backup source with
        // archive prefix "cowork-projects/<hash>/".
        progress?.Report(new OperationProgress("Discovering Cowork projects"));
        var coworkProjects = coworkSelected
            ? _coworkDiscovery.Discover()
            : Array.Empty<CoworkProjectFolder>();
        if (coworkSelected)
        {
            sourcesLog.Info($"Cowork project folders found: {coworkProjects.Count}");
        }
        if (existingPaths.Count == 0 && coworkProjects.Count == 0)
        {
            const string message = "No Cowork project folders found on this machine. Nothing to back up.";
            _log.Error(message);
            throw new InvalidOperationException(message);
        }

        var exclusions = new ExclusionGlob(DefaultExclusions.Globs);
        var filesPerSource = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var archiveTargets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<ArchiveEntry>();
        var links = new List<BackupLink>();

        foreach (var p in existingPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new OperationProgress($"Enumerating {p.Key}"));
            var before = entries.Count;
            var archivePrefix = SourceLayout.PrefixFor(p.Key);
            if (File.Exists(p.Path))
            {
                // Single-file source (.claude.json): stored under its prefix.
                var entryPath = $"{archivePrefix}/{Path.GetFileName(p.Path)}";
                if (!exclusions.IsExcluded(entryPath))
                {
                    entries.Add(new ArchiveEntry(entryPath, p.Path));
                }
                sourcesLog.Info($"{p.Key}: {p.Path} -> {entryPath} ({OperationLogExtensions.Bytes(TryGetFileSize(p.Path))})");
            }
            else
            {
                var stats = new EnumerationStats();
                var linksBefore = links.Count;
                entries.AddRange(FileEnumerator.Enumerate(p.Path, archivePrefix, exclusions, links, stats));
                LogSource(sourcesLog, $"{p.Key}: {p.Path} -> {archivePrefix}", stats, links.Count - linksBefore);
            }
            filesPerSource[p.Key] = entries.Count - before;
            archiveTargets[archivePrefix] = p.Path;
        }

        foreach (var project in coworkProjects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new OperationProgress($"Enumerating Cowork project {project.Hash}"));
            var key = CoworkProjectKeyPrefix + project.Hash;
            var archivePrefix = CoworkProjectArchivePrefix + project.Hash;
            var before = entries.Count;
            var stats = new EnumerationStats();
            var linksBefore = links.Count;
            entries.AddRange(FileEnumerator.Enumerate(project.Path, archivePrefix, exclusions, links, stats));
            LogSource(sourcesLog, $"Cowork project {project.Path} (sessions: {string.Join(", ", project.SessionNames)}) -> {archivePrefix}", stats, links.Count - linksBefore);
            filesPerSource[key] = entries.Count - before;
            archiveTargets[archivePrefix] = project.Path;
        }

        foreach (var link in links)
        {
            linksLog.Info($"{link.Path} -> {link.Target} (recorded, recreated on restore)");
        }
        _log.Debug($"Enumeration took {OperationLogExtensions.Duration(phase.Elapsed)}");
        phase.Restart();

        // Folders outside the agents' own folders that their configuration
        // needs (MCP servers, hook scripts, local marketplaces, link targets).
        progress?.Report(new OperationProgress("Discovering external dependencies"));
        var externalPaths = new List<BackupExternalPath>();
        var references = ExternalReferences.Discover(
            existingPaths,
            links.ToList(),
            coworkProjects.Select(c => c.Path).ToList());
        externalLog.Info($"Referenced by the agents' settings and links: {references.Count(r => r.SkipReason is null)} to back up, {references.Count(r => r.SkipReason is not null)} left out");
        foreach (var reference in references)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var usedBy = string.Join("; ", reference.ReferencedBy);
            if (reference.SkipReason is not null)
            {
                externalLog.Info($"Not backed up: {reference.Path} - {reference.SkipReason} (used by {usedBy})");
                externalPaths.Add(new BackupExternalPath(reference.Path, reference.Groups, reference.ReferencedBy, null, reference.SkipReason));
                continue;
            }
            var gitRemote = GitOrigin(reference.Path);
            progress?.Report(new OperationProgress($"Enumerating external {reference.Path}"));
            var hash = CoworkProjectDiscovery.ShortHash(reference.Path);
            var archivePrefix = SourceGroups.ExternalPrefix + hash;
            var found = new List<ArchiveEntry>();
            var foundLinks = new List<BackupLink>();
            var stats = new EnumerationStats();
            string? tooLarge = null;
            foreach (var entry in FileEnumerator.Enumerate(reference.Path, archivePrefix, exclusions, foundLinks, stats))
            {
                found.Add(entry);
                if (found.Count > MaxExternalFiles || stats.Bytes > MaxExternalBytes)
                {
                    tooLarge = $"too large to include automatically (over {MaxExternalFiles:N0} files or {MaxExternalBytes >> 20:N0} MB); "
                        + (gitRemote is null ? "back it up separately" : $"clone {gitRemote} again");
                    break;
                }
            }
            if (tooLarge is not null)
            {
                externalLog.Warn($"Not backed up: {reference.Path} ({usedBy}) - {tooLarge}");
                externalPaths.Add(new BackupExternalPath(reference.Path, reference.Groups, reference.ReferencedBy, null, tooLarge, gitRemote));
                continue;
            }
            LogSource(
                externalLog,
                $"{reference.Path} -> {archivePrefix} (groups {string.Join(", ", reference.Groups)}; used by {usedBy}{(gitRemote is null ? string.Empty : $"; git origin {gitRemote}")})",
                stats,
                foundLinks.Count);
            foreach (var link in foundLinks)
            {
                linksLog.Info($"{link.Path} -> {link.Target} (recorded, recreated on restore)");
            }
            entries.AddRange(found);
            links.AddRange(foundLinks);
            filesPerSource[ExternalKeyPrefix + hash] = found.Count;
            archiveTargets[archivePrefix] = reference.Path;
            externalPaths.Add(new BackupExternalPath(reference.Path, reference.Groups, reference.ReferencedBy, archivePrefix, null, gitRemote));
        }
        _log.Debug($"External folders took {OperationLogExtensions.Duration(phase.Elapsed)}");
        phase.Restart();

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
        _log.Info($"To archive: {entries.Count:N0} files, {OperationLogExtensions.Bytes(approxSizeBytes)} -> {zipPath}");

        var manifestSeed = ManifestBuilder.Build(
            discovered,
            DefaultExclusions.Globs,
            request.Tier,
            createdAt,
            sizeBytes: approxSizeBytes,
            fileCount: approxFileCount,
            claudeDesktopVersion: claudeInstall?.Version,
            coworkProjects: coworkProjects,
            archiveTargets: archiveTargets,
            links: links,
            groups: request.Groups,
            claudeDesktopEdition: claudeInstall?.Edition,
            apps: installs
                .Select(i => new BackupAppInfo(
                    i.App == AgentApp.ClaudeDesktop ? "claude-desktop" : "codex-desktop",
                    i.Edition,
                    i.Version))
                .ToList(),
            externalPaths: externalPaths);

        if (request.DryRun)
        {
            _log.Info($"Dry run finished in {OperationLogExtensions.Duration(total.Elapsed)}; nothing was written");
            return new BackupOutcome(zipPath, manifestSeed, WasDryRun: true, skippedPaths, filesPerSource);
        }

        var checklistEntry = BuildChecklistEntry();
        var snapshotDir = Path.Combine(Path.GetTempPath(), "ClaudePortable", $"snapshot-{Guid.NewGuid():N}");
        try
        {
            var consistentEntries = SqliteSnapshot.Apply(entries, snapshotDir, _log.In(LogSections.Databases), progress, cancellationToken);
            _log.Debug($"Database snapshots took {OperationLogExtensions.Duration(phase.Elapsed)}");
            phase.Restart();

            var combinedEntries = consistentEntries.Append(checklistEntry).ToList();
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

            var zipSize = TryGetFileSize(zipPath);
            var notArchived = combinedEntries.Count - archiveResult.FileCount;
            _log.In(LogSections.Archive).Info($"Written in {OperationLogExtensions.Duration(phase.Elapsed)}: {archiveResult.FileCount:N0} files, {OperationLogExtensions.Bytes(archiveResult.SizeBytes)} of data, ZIP {OperationLogExtensions.Bytes(zipSize)}, sha256 {archiveResult.Sha256}");
            if (notArchived > 0)
            {
                _log.Warn($"{notArchived:N0} file(s) could not be read and are missing or incomplete in the ZIP (see Archive)");
            }
            _log.Info($"Backup finished in {OperationLogExtensions.Duration(total.Elapsed)}: {zipPath}");
            return new BackupOutcome(zipPath, finalManifest, WasDryRun: false, skippedPaths, filesPerSource);
        }
        catch (OperationCanceledException)
        {
            _log.Warn($"Backup cancelled after {OperationLogExtensions.Duration(total.Elapsed)}; no ZIP was written");
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _log.Error($"Backup failed after {OperationLogExtensions.Duration(total.Elapsed)}: {ex.GetType().Name}: {ex.Message}");
            throw;
        }
        finally
        {
            TryDeleteDirectory(snapshotDir);
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

    /// <summary>
    /// One line per source with what goes in and what is left out; the log
    /// file also gets the excluded files and skipped folders themselves.
    /// </summary>
    private void LogSource(IOperationLog sectionLog, string header, EnumerationStats stats, int links)
    {
        sectionLog.Info($"{header}: {stats.Files:N0} files, {OperationLogExtensions.Bytes(stats.Bytes)}; left out {stats.ExcludedFiles.Count:N0} files ({OperationLogExtensions.Bytes(stats.ExcludedBytes)}) and {stats.PrunedDirectories.Count:N0} folders; {links} link(s)");
        var leftOut = _log.In(LogSections.LeftOut);
        foreach (var dir in stats.PrunedDirectories.Take(MaxListedExclusions))
        {
            leftOut.Debug($"folder {dir}");
        }
        foreach (var file in stats.ExcludedFiles.Take(MaxListedExclusions))
        {
            leftOut.Debug($"file {file}");
        }
        var more = Math.Max(0, stats.PrunedDirectories.Count - MaxListedExclusions) + Math.Max(0, stats.ExcludedFiles.Count - MaxListedExclusions);
        if (more > 0)
        {
            leftOut.Debug($"... and {more:N0} more ({header})");
        }
    }

    /// <summary>The "origin" remote URL of a git checkout, or null.</summary>
    internal static string? GitOrigin(string folder)
    {
        try
        {
            var config = Path.Combine(folder, ".git", "config");
            if (!File.Exists(config))
            {
                return null;
            }
            var inOrigin = false;
            foreach (var raw in File.ReadLines(config))
            {
                var line = raw.Trim();
                if (line.StartsWith('['))
                {
                    inOrigin = line.Replace(" ", string.Empty, StringComparison.Ordinal)
                        .Equals("[remote\"origin\"]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                if (inOrigin && line.StartsWith("url", StringComparison.OrdinalIgnoreCase) && line.IndexOf('=', StringComparison.Ordinal) is var eq and > 0)
                {
                    var url = line[(eq + 1)..].Trim();
                    // Never carry credentials embedded in an https URL.
                    return Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.UserInfo)
                        ? url.Replace(uri.UserInfo + "@", string.Empty, StringComparison.Ordinal)
                        : url;
                }
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static IReadOnlyList<AgentInstall> DetectInstalls()
    {
        try
        {
            return AgentInstalls.Detect();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return Array.Empty<AgentInstall>();
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
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
