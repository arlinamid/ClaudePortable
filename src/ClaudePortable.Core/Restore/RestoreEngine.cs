using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.Versioning;
using ClaudePortable.Core.Abstractions;
using ClaudePortable.Core.Discovery;
using ClaudePortable.Core.Manifest;
using ClaudePortable.Core.Post;

namespace ClaudePortable.Core.Restore;

[SupportedOSPlatform("windows")]
public sealed class RestoreEngine : IRestoreEngine
{
    private static readonly string[] NotInBackupWarning = ["Not present in backup — nothing to restore for this target."];

    /// <summary>
    /// Fallback map used when the backup's manifest does not carry
    /// <see cref="BackupManifest.ArchiveTargets"/> (v0.1.12 and earlier).
    /// For those older zips we still need to know where each archive
    /// prefix should be restored.
    /// </summary>
    private static readonly (string ArchivePrefix, string EnvRelative)[] LegacyRestoreMap =
    [
        ("claude-desktop/appdata", @"%APPDATA%\Claude"),
        ("claude-desktop/localappdata", @"%LOCALAPPDATA%\Claude"),
        ("claude-code/dotclaude", @"%USERPROFILE%\.claude"),
        ("cowork/sessions", @"%USERPROFILE%\.cowork"),
    ];

    /// <summary>
    /// Items that are deliberately excluded from the ZIP because they are
    /// credentials or machine-bound (see DefaultExclusions). When a restore
    /// moves the live folder aside, these are copied back from that safety
    /// backup so a same-machine restore does not log the user out or break
    /// Codex's Windows sandbox setup. On a fresh machine there is nothing to
    /// carry and the post-restore checklist asks for a re-login instead.
    /// </summary>
    private static readonly Dictionary<string, string[]> MachineLocalCarryOver = new(StringComparer.OrdinalIgnoreCase)
    {
        ["codex/dotcodex"] = ["auth.json", "installation_id", "cap_sid", ".sandbox", ".sandbox-secrets"],
    };

    private readonly IPathRewriter _pathRewriter;
    private readonly TimeProvider _clock;

    public RestoreEngine(IPathRewriter pathRewriter, TimeProvider? clock = null)
    {
        _pathRewriter = pathRewriter;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<RestoreOutcome> RestoreAsync(
        RestoreRequest request,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(request.SourceZipPath))
        {
            throw new FileNotFoundException("Backup ZIP not found.", request.SourceZipPath);
        }

        if (!request.Confirmed)
        {
            throw new InvalidOperationException(
                "Restore requires explicit confirmation. Pass --yes on the CLI or set Confirmed=true.");
        }

        var running = GetRunningProcesses("Claude");
        if (running.Count > 0)
        {
            throw new InvalidOperationException(
                $"Claude Desktop is running (PID {string.Join(", ", running)}). Close it before restoring; its open file handles will cause 'Access denied' errors on %LOCALAPPDATA%\\Claude and %APPDATA%\\Claude.");
        }

        // Codex keeps its sqlite state open, so only block when this backup
        // actually carries Codex data. Peek at the manifest before the
        // (potentially multi-GB) extraction so the user fails fast.
        if (ArchiveContainsCodex(request.SourceZipPath))
        {
            var runningCodex = GetRunningProcesses("Codex");
            if (runningCodex.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Codex is running (PID {string.Join(", ", runningCodex)}). Close the Codex app and any codex CLI sessions before restoring; they hold %USERPROFILE%\\.codex open.");
            }
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), "ClaudePortable", $"restore-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            progress?.Report(new OperationProgress("Extracting archive"));
            await ExtractWithProgressAsync(request.SourceZipPath, tempRoot, progress, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var manifestPath = Path.Combine(tempRoot, "manifest.json");
            if (!File.Exists(manifestPath))
            {
                throw new InvalidDataException("Backup is missing manifest.json.");
            }

            var manifest = ManifestBuilder.Deserialize(await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false));

            var installedVersion = ClaudeDesktopVersionReader.TryRead();
            var gate = VersionGating.Evaluate(manifest.ClaudeDesktopVersion, installedVersion);
            if (gate.Level == VersionGateLevel.Block && !request.IgnoreVersionMismatch)
            {
                throw new InvalidOperationException(gate.Message);
            }

            var newUserProfile = request.TargetUserProfile
                ?? Environment.ExpandEnvironmentVariables("%USERPROFILE%");
            var oldUserProfile = InferSourceUserProfile(manifest) ?? newUserProfile;

            progress?.Report(new OperationProgress("Rewriting paths"));
            _pathRewriter.Rewrite(tempRoot, oldUserProfile, newUserProfile);

            var now = _clock.GetUtcNow();
            var safetyBackups = new List<string>();
            var perTargetReports = new List<RestoreTargetReport>();

            var restorePlan = BuildRestorePlan(manifest, request, oldUserProfile, newUserProfile);

            foreach (var (archivePrefix, targetDir) in restorePlan)
            {
                var sourceDir = Path.Combine(tempRoot, archivePrefix.Replace('/', Path.DirectorySeparatorChar));

                if (!Directory.Exists(sourceDir))
                {
                    perTargetReports.Add(new RestoreTargetReport(
                        archivePrefix,
                        targetDir,
                        SafetyBackedUp: false,
                        SafetyBackupPath: null,
                        FilesWritten: 0,
                        Warnings: NotInBackupWarning));
                    continue;
                }

                var warnings = new List<string>();

                progress?.Report(new OperationProgress($"Preserving current {archivePrefix}"));
                var preserved = SafetyBackup.TryPreserveExisting(targetDir, now);
                if (preserved.Error is not null)
                {
                    warnings.Add(preserved.Error);
                }
                if (preserved.MovedAside && preserved.BackupPath is not null)
                {
                    safetyBackups.Add(preserved.BackupPath);
                }

                progress?.Report(new OperationProgress($"Writing {archivePrefix}"));
                var (filesWritten, copyErrors) = CopyDirectoryResilient(sourceDir, targetDir, archivePrefix, progress);
                warnings.AddRange(copyErrors);

                if (preserved.MovedAside && preserved.BackupPath is not null)
                {
                    warnings.AddRange(CarryOverMachineLocal(archivePrefix, preserved.BackupPath, targetDir));
                }

                perTargetReports.Add(new RestoreTargetReport(
                    archivePrefix,
                    targetDir,
                    preserved.MovedAside,
                    preserved.BackupPath,
                    filesWritten,
                    warnings));
            }

            var checklistDest = Path.Combine(
                Environment.ExpandEnvironmentVariables("%LOCALAPPDATA%"),
                "ClaudePortable",
                $"post-restore-checklist-{now.UtcDateTime:yyyy-MM-dd-HHmmss}.md");
            Directory.CreateDirectory(Path.GetDirectoryName(checklistDest)!);

            var checklistContent = PostRestoreChecklistBuilder.Build(
                manifest, gate, safetyBackups, perTargetReports);
            await File.WriteAllTextAsync(checklistDest, checklistContent, cancellationToken).ConfigureAwait(false);

            return new RestoreOutcome(manifest, safetyBackups, checklistDest, gate, perTargetReports);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    /// <summary>
    /// Combine the manifest's ArchiveTargets (new format) with the
    /// legacy fallback map, returning (archivePrefix, effectiveTargetDir)
    /// pairs. Env-var-based targets from the legacy map are expanded
    /// against the effective user profile; literal paths from the
    /// manifest get a username rewrite when TargetUserProfile is set.
    /// </summary>
    private static List<(string ArchivePrefix, string TargetDir)> BuildRestorePlan(
        BackupManifest manifest,
        RestoreRequest request,
        string oldUserProfile,
        string newUserProfile)
    {
        var plan = new List<(string, string)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (archivePrefix, originalPath) in manifest.ArchiveTargets)
        {
            var target = RewriteAbsolutePathForUser(originalPath, oldUserProfile, newUserProfile);
            plan.Add((archivePrefix, target));
            seen.Add(archivePrefix);
        }

        foreach (var (archivePrefix, envRelative) in LegacyRestoreMap)
        {
            if (seen.Contains(archivePrefix))
            {
                continue;
            }
            var target = Environment.ExpandEnvironmentVariables(envRelative);
            if (request.TargetUserProfile is not null)
            {
                target = RewriteEnvPath(envRelative, request.TargetUserProfile);
            }
            plan.Add((archivePrefix, target));
        }

        return plan;
    }

    /// <summary>
    /// Swap occurrences of the backup-time user profile root inside an
    /// absolute path for the restore-time one. Handles the "sascha" ->
    /// "sasch" case used by the cross-machine restore flow.
    /// </summary>
    private static string RewriteAbsolutePathForUser(string original, string oldUserProfile, string newUserProfile)
    {
        if (string.IsNullOrEmpty(original))
        {
            return original;
        }
        var oldNorm = oldUserProfile.TrimEnd('\\', '/');
        var newNorm = newUserProfile.TrimEnd('\\', '/');
        if (string.IsNullOrEmpty(oldNorm) ||
            string.Equals(oldNorm, newNorm, StringComparison.OrdinalIgnoreCase))
        {
            return original;
        }
        if (original.StartsWith(oldNorm, StringComparison.OrdinalIgnoreCase) &&
            (original.Length == oldNorm.Length ||
             original[oldNorm.Length] == Path.DirectorySeparatorChar ||
             original[oldNorm.Length] == '/' ||
             original[oldNorm.Length] == '\\'))
        {
            return newNorm + original[oldNorm.Length..];
        }
        return original;
    }

    /// <summary>
    /// The backup machine's %USERPROFILE%, derived from a dot-folder that
    /// lives directly under it. Codex-only backups have no .claude entry,
    /// so fall back to .codex (only when it is the default location - a
    /// custom %CODEX_HOME% says nothing about the profile root).
    /// </summary>
    private static string? InferSourceUserProfile(BackupManifest manifest)
    {
        if (manifest.SourcePaths.TryGetValue("claudeCodeUserProfile", out var dotClaude))
        {
            return Path.GetDirectoryName(dotClaude.TrimEnd('\\', '/'));
        }
        if (manifest.SourcePaths.TryGetValue("codexUserProfile", out var dotCodex)
            && string.Equals(Path.GetFileName(dotCodex.TrimEnd('\\', '/')), ".codex", StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetDirectoryName(dotCodex.TrimEnd('\\', '/'));
        }
        return null;
    }

    private static bool ArchiveContainsCodex(string zipPath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            var entry = archive.GetEntry("manifest.json");
            if (entry is null)
            {
                return false;
            }
            using var reader = new StreamReader(entry.Open());
            var manifest = ManifestBuilder.Deserialize(reader.ReadToEnd());
            return manifest.ArchiveTargets.Keys.Any(k => k.StartsWith("codex", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            // Unreadable here means the extraction below fails with a
            // clearer error; don't mask it with a process check.
            return false;
        }
    }

    private static List<string> CarryOverMachineLocal(string archivePrefix, string safetyBackupPath, string targetDir)
    {
        var warnings = new List<string>();
        if (!MachineLocalCarryOver.TryGetValue(archivePrefix, out var items))
        {
            return warnings;
        }

        foreach (var item in items)
        {
            var from = Path.Combine(safetyBackupPath, item);
            var to = Path.Combine(targetDir, item);
            try
            {
                if (File.Exists(from) && !File.Exists(to))
                {
                    File.Copy(from, to);
                }
                else if (Directory.Exists(from) && !Directory.Exists(to))
                {
                    foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
                    {
                        var dest = Path.Combine(to, Path.GetRelativePath(from, file));
                        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                        File.Copy(file, dest);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"Could not carry over machine-local '{item}' from the safety backup: {ex.Message}");
            }
        }

        return warnings;
    }

    private static IReadOnlyList<int> GetRunningProcesses(string processName)
    {
        try
        {
            return Process.GetProcessesByName(processName).Select(p => p.Id).ToList();
        }
        catch (InvalidOperationException)
        {
            return Array.Empty<int>();
        }
    }

    private static string RewriteEnvPath(string envRelative, string newUserProfile)
    {
        return envRelative
            .Replace("%USERPROFILE%", newUserProfile, StringComparison.OrdinalIgnoreCase)
            .Replace("%APPDATA%", Path.Combine(newUserProfile, "AppData", "Roaming"), StringComparison.OrdinalIgnoreCase)
            .Replace("%LOCALAPPDATA%", Path.Combine(newUserProfile, "AppData", "Local"), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Copy source -> destination. Collects per-file copy failures as
    /// warnings and returns (filesWritten, errors) - does NOT throw on
    /// a single blocked file (common when a Store-sandbox folder has
    /// some locked or permission-guarded children).
    /// </summary>
    private static (int FilesWritten, IReadOnlyList<string> Errors) CopyDirectoryResilient(
        string source,
        string destination,
        string archivePrefix,
        IProgress<OperationProgress>? progress)
    {
        var errors = new List<string>();
        try
        {
            Directory.CreateDirectory(destination);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            errors.Add($"Could not create '{destination}': {ex.Message}");
            return (0, errors);
        }

        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, dir);
            try
            {
                Directory.CreateDirectory(Path.Combine(destination, relative));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"Could not create subdir '{relative}': {ex.Message}");
            }
        }

        var allFiles = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).ToList();
        var filesWritten = 0;
        for (var i = 0; i < allFiles.Count; i++)
        {
            var file = allFiles[i];
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            if ((i & 0x3F) == 0)
            {
                progress?.Report(new OperationProgress($"Writing {archivePrefix}", i, allFiles.Count));
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: true);
                filesWritten++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"Skipped '{relative}': {ex.Message}");
            }
        }
        progress?.Report(new OperationProgress($"Writing {archivePrefix}", allFiles.Count, allFiles.Count));

        return (filesWritten, errors);
    }

    /// <summary>
    /// Replace the sync <see cref="ZipFile.ExtractToDirectory"/> with a per-
    /// entry loop that yields progress and threadpool-friendly awaits.
    /// </summary>
    private static async Task ExtractWithProgressAsync(
        string zipPath,
        string destination,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var total = archive.Entries.Count;
        for (var i = 0; i < total; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var e = archive.Entries[i];
            if ((i & 0x3F) == 0)
            {
                progress?.Report(new OperationProgress("Extracting archive", i, total));
            }

            if (string.IsNullOrEmpty(e.Name))
            {
                // Directory entry.
                var dirPath = Path.Combine(destination, e.FullName.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(dirPath);
                continue;
            }

            var targetPath = Path.Combine(destination, e.FullName.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            await using var src = e.Open();
            await using var dst = File.Create(targetPath);
            await src.CopyToAsync(dst, cancellationToken).ConfigureAwait(false);
        }
        progress?.Report(new OperationProgress("Extracting archive", total, total));
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
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
