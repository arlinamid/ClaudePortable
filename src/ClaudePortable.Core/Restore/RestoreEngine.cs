using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.Versioning;
using ClaudePortable.Core.Abstractions;
using ClaudePortable.Core.Archive;
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
        ["codex/dotcodex"] =
        [
            "auth.json", "installation_id", "cap_sid", ".sandbox", ".sandbox-secrets",
            "chrome-native-hosts-v2.json", "project-chatgpt-provisioning", "project-chatgpt-pairing",
        ],
        ["claude-code/dotclaude"] = [".credentials.json"],
        ["claude-desktop/appdata"] =
        [
            "config.json", "ant-did", "ant-device-registry.json", "remote-control-state.json",
            "ssh-remote-server-state.json", "bridge-state.json",
        ],
        ["claude-desktop/appdata-3p"] = ["config.json"],
    };

    /// <summary>Archive prefix of Claude Code's global config file, which is merged instead of copied.</summary>
    private const string ClaudeGlobalConfigPrefix = "claude-code/globalconfig";

    private readonly IPathRewriter _pathRewriter;
    private readonly IPathDiscovery _pathDiscovery;
    private readonly TimeProvider _clock;
    private readonly IOperationLog _log;

    public RestoreEngine(IPathRewriter pathRewriter, TimeProvider? clock = null, IPathDiscovery? pathDiscovery = null, IOperationLog? log = null)
    {
        _pathRewriter = pathRewriter;
        _pathDiscovery = pathDiscovery ?? new WindowsPathDiscovery();
        _clock = clock ?? TimeProvider.System;
        _log = log ?? NullOperationLog.Instance;
    }

    public async Task<RestoreOutcome> RestoreAsync(
        RestoreRequest request,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var total = System.Diagnostics.Stopwatch.StartNew();
        var detailsLog = _log.In(LogSections.BackupDetails);
        var targetsLog = _log.In(LogSections.Targets);
        var linksLog = _log.In(LogSections.Links);
        _log.Info($"Restore started: {request.SourceZipPath}; groups {(request.Groups is null ? "all" : string.Join(", ", request.Groups))}; target profile {request.TargetUserProfile ?? "current user"}; ignore version mismatch: {request.IgnoreVersionMismatch}");
        if (!File.Exists(request.SourceZipPath))
        {
            _log.Error($"Backup ZIP not found: {request.SourceZipPath}");
            throw new FileNotFoundException("Backup ZIP not found.", request.SourceZipPath);
        }
        detailsLog.Info($"ZIP: {OperationLogExtensions.Bytes(new FileInfo(request.SourceZipPath).Length)}, modified {File.GetLastWriteTime(request.SourceZipPath):yyyy-MM-dd HH:mm}");

        if (!request.Confirmed)
        {
            _log.Error("Restore was not confirmed");
            throw new InvalidOperationException(
                "Restore requires explicit confirmation. Pass --yes on the CLI or set Confirmed=true.");
        }

        // Only demand that an app is closed when its data is actually about
        // to be written: a Codex-only restore must not require quitting
        // Claude, and vice versa. Peek at the manifest before the
        // (potentially multi-GB) extraction so the user fails fast.
        var restoring = GroupsToRestore(TryPeekManifest(request.SourceZipPath), request.Groups);
        _log.Info($"Groups to restore (selection and backup contents): {(restoring.Count == 0 ? "none" : string.Join(", ", SourceGroups.All.Where(restoring.Contains)))}");
        var running = FindRunning(restoring);
        if (running.Count > 0)
        {
            foreach (var p in running)
            {
                _log.In(LogSections.RunningApps).Info($"{AgentProcesses.Describe(p.Kind)} PID {p.Process.Pid} {p.Process.Path}");
            }
            var message = $"Close these before restoring: {AgentProcesses.Summarize(running)}. Running agents keep the restored folders open ('Access denied') and write their old state back on exit. Background helpers count too: Claude Desktop's Claude Code sessions, the Codex app-server, codex / claude CLI sessions.";
            _log.Error(message);
            throw new InvalidOperationException(message);
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), "ClaudePortable", $"restore-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var phase = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            progress?.Report(new OperationProgress("Extracting archive"));
            await ExtractWithProgressAsync(request.SourceZipPath, tempRoot, progress, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            detailsLog.Info($"Extracted to {tempRoot} in {OperationLogExtensions.Duration(phase.Elapsed)}");
            phase.Restart();

            var manifestPath = Path.Combine(tempRoot, "manifest.json");
            if (!File.Exists(manifestPath))
            {
                _log.Error("Backup is missing manifest.json");
                throw new InvalidDataException("Backup is missing manifest.json.");
            }

            var manifest = ManifestBuilder.Deserialize(await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false));
            detailsLog.Info($"Made {manifest.CreatedAt:yyyy-MM-dd HH:mm} UTC on {manifest.Hostname} by {manifest.WindowsUser} with AgentPortable {manifest.ToolVersion} (schema {manifest.SchemaVersion}): {manifest.FileCount:N0} files, {OperationLogExtensions.Bytes(manifest.SizeBytes)}, groups {(manifest.Groups is null ? "all" : string.Join(", ", manifest.Groups))}");
            foreach (var app in manifest.Apps ?? [])
            {
                detailsLog.Info($"App on the backup machine: {app.App} {app.Edition} {app.Version}");
            }

            var installed = ClaudeDesktopVersionReader.TryReadInstall();
            var backupEdition = manifest.ClaudeDesktopEdition
                ?? (manifest.ClaudeDesktopVersion is null ? null : AgentInstalls.LegacyClaudeEdition);
            var gate = VersionGating.Evaluate(manifest.ClaudeDesktopVersion, backupEdition, installed?.Version, installed?.Edition);
            _log.Write(
                gate.Level is VersionGateLevel.Warn or VersionGateLevel.Block ? OperationLogLevel.Warning : OperationLogLevel.Info,
                LogSections.VersionCheck,
                $"Claude Desktop: backup {manifest.ClaudeDesktopVersion ?? "?"} ({backupEdition ?? "?"}), installed {installed?.Version ?? "none"} ({installed?.Edition ?? "-"}): {gate.Level} - {gate.Message}");
            if (gate.Level == VersionGateLevel.Block
                && !request.IgnoreVersionMismatch
                && restoring.Contains(SourceGroups.ClaudeDesktop))
            {
                _log.Error("Restore stopped by the version check (override: --ignore-version-mismatch)");
                throw new InvalidOperationException(gate.Message);
            }

            var newUserProfile = request.TargetUserProfile
                ?? Environment.ExpandEnvironmentVariables("%USERPROFILE%");
            var oldUserProfile = manifest.UserProfile ?? InferSourceUserProfile(manifest) ?? newUserProfile;
            _log.In(LogSections.Paths).Info($"User profile: {oldUserProfile} (backup) -> {newUserProfile} (here){(manifest.UserProfile is null ? " [backup profile inferred]" : string.Empty)}");

            // Paths inside configs, session files and SQLite state (Codex's
            // threads.rollout_path) move from the backup profile to this one;
            // so do Claude Code's path-derived project folder names.
            progress?.Report(new OperationProgress("Rewriting paths"));
            var rewrite = _pathRewriter.Rewrite(tempRoot, oldUserProfile, newUserProfile);
            _log.In(LogSections.Paths).Info($"Path rewrite: {rewrite.FilesScanned:N0} files scanned, {rewrite.FilesChanged:N0} changed, {rewrite.ReplacementsMade:N0} paths replaced ({OperationLogExtensions.Duration(phase.Elapsed)})");
            var projectFolderWarnings = ClaudeCodeProjectFolders.RenameForProfile(
                Path.Combine(tempRoot, "claude-code", "dotclaude", "projects"),
                oldUserProfile,
                newUserProfile);
            foreach (var w in projectFolderWarnings)
            {
                _log.In(LogSections.Paths).Warn(w);
            }
            phase.Restart();

            var now = _clock.GetUtcNow();
            var safetyBackups = new List<string>();
            var perTargetReports = new List<RestoreTargetReport>();

            var liveSources = request.TargetUserProfile is null
                ? _pathDiscovery.Discover()
                : Array.Empty<DiscoveredClaudePath>();
            var restorePlan = BuildRestorePlan(manifest, request, oldUserProfile, newUserProfile, liveSources);
            targetsLog.Info($"Plan: {restorePlan.Count} target(s)");
            foreach (var (archivePrefix, targetDir) in restorePlan)
            {
                var from = manifest.ArchiveTargets.TryGetValue(archivePrefix, out var original) ? original : "(legacy layout)";
                targetsLog.Debug($"{archivePrefix} -> {targetDir} (backed up from {from})");
            }

            foreach (var (archivePrefix, targetDir) in restorePlan)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourceDir = Path.Combine(tempRoot, archivePrefix.Replace('/', Path.DirectorySeparatorChar));

                if (!Directory.Exists(sourceDir))
                {
                    targetsLog.Info($"{archivePrefix}: not in this backup, skipped");
                    perTargetReports.Add(new RestoreTargetReport(
                        archivePrefix,
                        targetDir,
                        SafetyBackedUp: false,
                        SafetyBackupPath: null,
                        FilesWritten: 0,
                        Warnings: NotInBackupWarning));
                    continue;
                }

                if (archivePrefix.Equals(ClaudeGlobalConfigPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    var merged = RestoreClaudeGlobalConfig(sourceDir, targetDir, now);
                    targetsLog.Write(
                        merged.Warnings.Count > 0 ? OperationLogLevel.Warning : OperationLogLevel.Info,
                        merged.FilesWritten > 0
                            ? $"{archivePrefix}: merged into {targetDir} (machine ids kept{(merged.SafetyBackupPath is null ? ", no previous file" : $", previous file saved as {merged.SafetyBackupPath}")})"
                            : $"{archivePrefix}: not merged - {string.Join("; ", merged.Warnings)}");
                    perTargetReports.Add(merged);
                    continue;
                }

                // External folders (MCP servers, hook scripts, marketplaces,
                // link targets) are put back only where nothing exists yet:
                // a checkout already on this PC is the user's, newer or not.
                if (archivePrefix.StartsWith(SourceGroups.ExternalPrefix, StringComparison.OrdinalIgnoreCase)
                    && Directory.Exists(targetDir))
                {
                    targetsLog.Info($"{archivePrefix}: external folder {targetDir} already exists here, left as it is");
                    perTargetReports.Add(new RestoreTargetReport(
                        archivePrefix,
                        targetDir,
                        SafetyBackedUp: false,
                        SafetyBackupPath: null,
                        FilesWritten: 0,
                        Warnings: [$"External folder already exists on this PC; left as it is: {targetDir}"]));
                    continue;
                }

                var warnings = new List<string>();
                var targetWatch = System.Diagnostics.Stopwatch.StartNew();

                progress?.Report(new OperationProgress($"Preserving current {archivePrefix}"));
                var preserved = SafetyBackup.TryPreserveExisting(targetDir, now);
                if (preserved.Error is not null)
                {
                    warnings.Add(preserved.Error);
                    targetsLog.Warn($"{archivePrefix}: {preserved.Error}");
                }
                if (preserved.MovedAside && preserved.BackupPath is not null)
                {
                    safetyBackups.Add(preserved.BackupPath);
                    targetsLog.Info($"{archivePrefix}: current folder moved aside to {preserved.BackupPath}");
                }
                else if (preserved.Error is null)
                {
                    targetsLog.Info($"{archivePrefix}: {targetDir} did not exist yet");
                }

                progress?.Report(new OperationProgress($"Writing {archivePrefix}"));
                var (filesWritten, copyErrors) = CopyDirectoryResilient(sourceDir, targetDir, archivePrefix, progress);
                warnings.AddRange(copyErrors);
                foreach (var error in copyErrors)
                {
                    targetsLog.Warn($"{archivePrefix}: {error}");
                }

                if (preserved.MovedAside && preserved.BackupPath is not null)
                {
                    warnings.AddRange(CarryOverMachineLocal(archivePrefix, preserved.BackupPath, targetDir, targetsLog));
                }

                targetsLog.Write(
                    copyErrors.Count > 0 ? OperationLogLevel.Warning : OperationLogLevel.Info,
                    $"{archivePrefix}: {filesWritten:N0} files written to {targetDir}{(copyErrors.Count > 0 ? $", {copyErrors.Count:N0} failed" : string.Empty)} ({OperationLogExtensions.Duration(targetWatch.Elapsed)})");
                perTargetReports.Add(new RestoreTargetReport(
                    archivePrefix,
                    targetDir,
                    preserved.MovedAside,
                    preserved.BackupPath,
                    filesWritten,
                    warnings));
            }

            // Links last: a link in one source often points into another
            // (~\.claude\skills\x -> ~\.agents\skills\x), whose files must
            // already be in place for the target-exists check.
            progress?.Report(new OperationProgress("Recreating links"));
            for (var i = 0; i < perTargetReports.Count; i++)
            {
                var report = perTargetReports[i];
                var prefix = report.ArchivePrefix.TrimEnd('/') + "/";
                var recorded = manifest.Links.Count(l => l.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
                var linkWarnings = RecreateLinks(manifest.Links, report.ArchivePrefix, report.TargetFolder, oldUserProfile, newUserProfile);
                if (recorded > 0)
                {
                    linksLog.Write(
                        linkWarnings.Count > 0 ? OperationLogLevel.Warning : OperationLogLevel.Info,
                        $"{report.ArchivePrefix}: {recorded} link(s) in the backup, {recorded - linkWarnings.Count} in place");
                    foreach (var w in linkWarnings)
                    {
                        linksLog.Warn(w);
                    }
                }
                if (report.ArchivePrefix.Equals("claude-code/dotclaude", StringComparison.OrdinalIgnoreCase))
                {
                    linkWarnings.AddRange(projectFolderWarnings);
                }
                if (linkWarnings.Count > 0)
                {
                    perTargetReports[i] = report with { Warnings = [.. report.Warnings, .. linkWarnings] };
                }
            }

            foreach (var e in manifest.ExternalPaths.Where(e => e.ArchivePrefix is null))
            {
                _log.In(LogSections.ProvideHere).Info($"{e.Path} - {e.SkipReason} (used by {string.Join("; ", e.ReferencedBy)})");
            }

            var checklistDest = Path.Combine(
                Environment.ExpandEnvironmentVariables("%LOCALAPPDATA%"),
                "ClaudePortable",
                $"post-restore-checklist-{now.UtcDateTime:yyyy-MM-dd-HHmmss}.md");
            Directory.CreateDirectory(Path.GetDirectoryName(checklistDest)!);

            var checklistContent = PostRestoreChecklistBuilder.Build(
                manifest, gate, safetyBackups, perTargetReports);
            await File.WriteAllTextAsync(checklistDest, checklistContent, cancellationToken).ConfigureAwait(false);

            var totalWarnings = perTargetReports.Sum(r => r.Warnings.Count(w => !w.StartsWith("Not present", StringComparison.Ordinal)));
            _log.Info($"Restore finished in {OperationLogExtensions.Duration(total.Elapsed)}: {perTargetReports.Sum(r => r.FilesWritten):N0} files, {safetyBackups.Count} safety backup(s), {totalWarnings} warning(s); checklist {checklistDest}");
            return new RestoreOutcome(manifest, safetyBackups, checklistDest, gate, perTargetReports);
        }
        catch (OperationCanceledException)
        {
            _log.Warn($"Restore cancelled after {OperationLogExtensions.Duration(total.Elapsed)}");
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _log.Error($"Restore failed after {OperationLogExtensions.Duration(total.Elapsed)}: {ex.GetType().Name}: {ex.Message}");
            throw;
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
        string newUserProfile,
        IReadOnlyList<DiscoveredClaudePath> liveSources)
    {
        var plan = new List<(string, string)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (archivePrefix, originalPath) in manifest.ArchiveTargets)
        {
            if (!SourceGroups.IsSelected(SourceGroups.ForArchivePrefix(archivePrefix, manifest), request.Groups))
            {
                continue;
            }

            // Prefer where the app actually keeps its data on THIS machine:
            // Store vs. non-Store install, a different %CODEX_HOME%, or
            // redirected AppData all change it. Fall back to the backup
            // machine's path (re-rooted to this profile) when the app has
            // no data folder here yet.
            var key = SourceLayout.KeyFor(archivePrefix);
            var live = key is null
                ? null
                : liveSources.FirstOrDefault(s => s.Exists && string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase));
            var target = live?.Path ?? RewriteAbsolutePathForUser(originalPath, oldUserProfile, newUserProfile);
            plan.Add((archivePrefix, target));
            seen.Add(archivePrefix);
        }

        foreach (var (archivePrefix, envRelative) in LegacyRestoreMap)
        {
            if (seen.Contains(archivePrefix)
                || !SourceGroups.IsSelected(SourceGroups.ForArchivePrefix(archivePrefix), request.Groups))
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

    /// <summary>
    /// Recreate the junctions / directory symlinks recorded at backup time
    /// under <paramref name="archivePrefix"/>. Absolute targets are re-rooted
    /// to the restore profile. A link is only created when its target exists
    /// here; otherwise the user gets a warning naming both ends.
    /// </summary>
    internal static List<string> RecreateLinks(
        IReadOnlyList<BackupLink> links,
        string archivePrefix,
        string targetDir,
        string oldUserProfile,
        string newUserProfile)
    {
        var warnings = new List<string>();
        var prefix = archivePrefix.TrimEnd('/') + "/";
        foreach (var link in links)
        {
            if (!link.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var relative = link.Path[prefix.Length..].Replace('/', Path.DirectorySeparatorChar);
            var linkPath = Path.Combine(targetDir, relative);
            var target = Path.IsPathFullyQualified(link.Target)
                ? RewriteAbsolutePathForUser(link.Target, oldUserProfile, newUserProfile)
                : link.Target;
            var resolvedTarget = Path.IsPathFullyQualified(target)
                ? target
                : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(linkPath)!, target));

            if (Directory.Exists(linkPath) || File.Exists(linkPath))
            {
                continue;
            }
            if (!Directory.Exists(resolvedTarget))
            {
                warnings.Add($"Link '{linkPath}' -> '{resolvedTarget}' not recreated: the target folder does not exist on this machine. Restore or clone it, then run: mklink /J \"{linkPath}\" \"{resolvedTarget}\"");
                continue;
            }

            var error = TryCreateDirectoryLink(linkPath, target, resolvedTarget);
            if (error is not null)
            {
                warnings.Add($"Link '{linkPath}' -> '{resolvedTarget}' could not be recreated: {error}");
            }
        }
        return warnings;
    }

    /// <summary>
    /// Junctions need no privileges, so an absolute local target becomes a
    /// junction (.NET has no junction API, hence mklink). Directory symlinks
    /// need Developer Mode or admin rights; they are used first only for a
    /// relative target (to keep it relative), and as the fallback for UNC
    /// targets, which junctions cannot point at. A relative symlink that
    /// cannot be created falls back to a junction to the resolved path.
    /// </summary>
    private static string? TryCreateDirectoryLink(string linkPath, string rawTarget, string resolvedTarget)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }

        var junctionPossible = !resolvedTarget.StartsWith(@"\\", StringComparison.Ordinal);
        if (Path.IsPathFullyQualified(rawTarget))
        {
            if (junctionPossible && TryCreateJunction(linkPath, resolvedTarget))
            {
                return null;
            }
            return TryCreateSymlink(linkPath, rawTarget);
        }

        var symlinkError = TryCreateSymlink(linkPath, rawTarget);
        if (symlinkError is null || (junctionPossible && TryCreateJunction(linkPath, resolvedTarget)))
        {
            return null;
        }
        return symlinkError;
    }

    private static bool TryCreateJunction(string linkPath, string target)
    {
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", $"/d /c mklink /J \"{linkPath}\" \"{target}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var proc = Process.Start(psi);
            return proc is not null && proc.WaitForExit(10_000) && proc.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    private static string? TryCreateSymlink(string linkPath, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, target);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"{ex.Message} (directory symlinks need Developer Mode or admin rights)";
        }
    }

    /// <summary>
    /// Groups that a restore of this backup with this selection will write.
    /// An unreadable manifest is treated as "everything", so the running-app
    /// checks stay on the safe side; extraction then reports the real error.
    /// </summary>
    public static IReadOnlySet<string> GroupsToRestore(string zipPath, IReadOnlySet<string>? selection)
        => GroupsToRestore(TryPeekManifest(zipPath), selection);

    internal static IReadOnlySet<string> GroupsToRestore(BackupManifest? manifest, IReadOnlySet<string>? selection)
    {
        var contents = manifest is null ? SourceGroups.All : SourceGroups.ContentsOf(manifest);
        return contents
            .Where(g => selection is null || selection.Contains(g))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static BackupManifest? TryPeekManifest(string zipPath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            var entry = archive.GetEntry("manifest.json");
            if (entry is null)
            {
                return null;
            }
            using var reader = new StreamReader(entry.Open());
            return ManifestBuilder.Deserialize(reader.ReadToEnd());
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static List<string> CarryOverMachineLocal(string archivePrefix, string safetyBackupPath, string targetDir, IOperationLog log)
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
                    log.Info($"{archivePrefix}: kept this PC's {item} (credentials / machine identity, not in backups)");
                }
                else if (Directory.Exists(from) && !Directory.Exists(to))
                {
                    foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
                    {
                        var dest = Path.Combine(to, Path.GetRelativePath(from, file));
                        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                        File.Copy(file, dest);
                    }
                    log.Info($"{archivePrefix}: kept this PC's {item} folder (credentials / machine identity, not in backups)");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"Could not carry over machine-local '{item}' from the safety backup: {ex.Message}");
            }
        }

        return warnings;
    }

    private static IReadOnlyList<AgentProcess> FindRunning(IReadOnlySet<string> restoring)
    {
        try
        {
            return AgentProcesses.Find(AgentProcesses.KindsToClose(restoring, AgentOperation.Restore));
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return Array.Empty<AgentProcess>();
        }
    }

    /// <summary>
    /// Merge the backed-up .claude.json into this machine's (see
    /// <see cref="ClaudeGlobalConfigMerger"/>); the current file is kept as
    /// ".claude.json.backup_&lt;timestamp&gt;" next to it.
    /// </summary>
    private static RestoreTargetReport RestoreClaudeGlobalConfig(string sourceDir, string targetFile, DateTimeOffset now)
    {
        var warnings = new List<string>();
        var source = Directory.EnumerateFiles(sourceDir, "*.json").FirstOrDefault();
        if (source is null)
        {
            return new RestoreTargetReport(ClaudeGlobalConfigPrefix, targetFile, false, null, 0, NotInBackupWarning);
        }

        string? safetyCopy = null;
        try
        {
            string? existing = null;
            if (File.Exists(targetFile))
            {
                existing = File.ReadAllText(targetFile);
                safetyCopy = $"{targetFile}.backup_{now.UtcDateTime.ToString("yyyy-MM-dd-HHmmss", System.Globalization.CultureInfo.InvariantCulture)}";
                File.Copy(targetFile, safetyCopy, overwrite: true);
            }
            var merged = ClaudeGlobalConfigMerger.Merge(File.ReadAllText(source), existing);
            Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
            File.WriteAllText(targetFile, merged);
            return new RestoreTargetReport(ClaudeGlobalConfigPrefix, targetFile, safetyCopy is not null, safetyCopy, 1, warnings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            warnings.Add($"Could not merge Claude Code's global config into '{targetFile}': {ex.Message}");
            return new RestoreTargetReport(ClaudeGlobalConfigPrefix, targetFile, safetyCopy is not null, safetyCopy, 0, warnings);
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
        var destinationRoot = Path.GetFullPath(destination);
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
                Directory.CreateDirectory(SafeEntryPath(destinationRoot, e.FullName));
                continue;
            }

            var targetPath = SafeEntryPath(destinationRoot, e.FullName);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            await using var src = e.Open();
            await using var dst = File.Create(targetPath);
            await src.CopyToAsync(dst, cancellationToken).ConfigureAwait(false);
        }
        progress?.Report(new OperationProgress("Extracting archive", total, total));
    }

    /// <summary>
    /// Resolve a ZIP entry name under the extraction root, refusing entries
    /// that escape it ("../", absolute or drive-qualified names). Backups can
    /// come from anywhere (a shared folder, a download), so a crafted archive
    /// must not be able to write outside the temp folder.
    /// </summary>
    internal static string SafeEntryPath(string destinationRoot, string entryName)
    {
        var root = destinationRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, entryName.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Backup contains an entry outside the archive root: '{entryName}'.");
        }
        return full;
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
