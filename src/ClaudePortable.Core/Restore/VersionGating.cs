namespace ClaudePortable.Core.Restore;

public enum VersionGateLevel
{
    Ok,
    Info,
    Warn,
    Block,
}

public sealed record VersionGateResult(VersionGateLevel Level, string Message);

/// <summary>
/// Decides whether Claude Desktop data from a backup can be opened by the
/// Claude Desktop installed here.
/// <para>
/// Electron apps migrate their stores forward on first start, so restoring
/// onto a NEWER app is the normal update path and only informational (the
/// claude.ai build jumped from 1.x to 4.x without breaking its data). Opening
/// data written by a newer app with an OLDER one is what goes wrong: within
/// one major version that is a warning, across major versions it blocks
/// until the app is updated (or the user overrides).
/// </para>
/// <para>
/// Version numbers are only compared within one edition: the Microsoft
/// Store build ("store") numbers itself independently of the claude.ai
/// build ("direct"). Backups that did not record an edition were read from
/// the "Claude" package, i.e. the direct build.
/// </para>
/// </summary>
public static class VersionGating
{
    public static VersionGateResult Evaluate(string? backupVersion, string? installedVersion)
        => Evaluate(backupVersion, backupEdition: null, installedVersion, installedEdition: null);

    public static VersionGateResult Evaluate(string? backupVersion, string? backupEdition, string? installedVersion, string? installedEdition)
    {
        if (string.IsNullOrEmpty(backupVersion) && string.IsNullOrEmpty(installedVersion))
        {
            return new VersionGateResult(VersionGateLevel.Info, "Claude Desktop version unknown on both sides; no gating applied.");
        }
        if (string.IsNullOrEmpty(backupVersion))
        {
            return new VersionGateResult(VersionGateLevel.Info, $"Backup has no claudeDesktopVersion field; installed is {installedVersion}.");
        }
        if (string.IsNullOrEmpty(installedVersion))
        {
            return new VersionGateResult(VersionGateLevel.Warn, $"Claude Desktop is not installed on this machine (or not detected); backup was taken with version {backupVersion}. Install Claude Desktop before opening the restored data.");
        }

        if (backupEdition is not null
            && installedEdition is not null
            && !string.Equals(backupEdition, installedEdition, StringComparison.OrdinalIgnoreCase))
        {
            return new VersionGateResult(
                VersionGateLevel.Info,
                $"Backup was taken with the {EditionLabel(backupEdition)} of Claude Desktop ({backupVersion}); this machine has the {EditionLabel(installedEdition)} ({installedVersion}). Both use the same data folder; the versions are numbered independently and are not compared. Keep the app up to date before opening the restored data.");
        }

        if (string.Equals(backupVersion, installedVersion, StringComparison.OrdinalIgnoreCase))
        {
            return new VersionGateResult(VersionGateLevel.Ok, $"Claude Desktop version matches: {installedVersion}.");
        }

        if (!Version.TryParse(Normalize(backupVersion), out var b) || !Version.TryParse(Normalize(installedVersion), out var i))
        {
            return new VersionGateResult(VersionGateLevel.Warn, $"Version strings are not parseable; treating as mismatch. Backup: {backupVersion}, installed: {installedVersion}.");
        }

        if (i > b)
        {
            return new VersionGateResult(VersionGateLevel.Info, $"Installed Claude Desktop {installedVersion} is newer than the backup's {backupVersion}; it migrates the restored data on first start.");
        }

        if (b.Major != i.Major)
        {
            return new VersionGateResult(VersionGateLevel.Block, $"Major-version mismatch: the backup was taken with Claude Desktop {backupVersion}, but the installed {installedVersion} is older. Update Claude Desktop first - an older app can leave newer chat history unreadable. Pass --ignore-version-mismatch to override.");
        }

        return new VersionGateResult(VersionGateLevel.Warn, $"Minor-version mismatch: installed Claude Desktop {installedVersion} is older than the backup's {backupVersion}. Update Claude Desktop before opening the restored data; LevelDB schema changes can corrupt chat history.");
    }

    private static string EditionLabel(string edition) => edition.ToLowerInvariant() switch
    {
        "direct" => "claude.ai download",
        "store" => "Microsoft Store build",
        "squirrel" => "legacy installer build",
        _ => edition + " build",
    };

    private static string Normalize(string v)
    {
        var parts = v.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 4)
        {
            return string.Join('.', parts[..4]);
        }
        while (parts.Length < 4)
        {
            parts = parts.Append("0").ToArray();
        }
        return string.Join('.', parts);
    }
}
