using ClaudePortable.Core.Abstractions;
using Microsoft.Data.Sqlite;

namespace ClaudePortable.Core.Backup;

/// <summary>
/// Replaces SQLite databases in a backup with consistent snapshots.
/// <para>
/// Codex (state_5.sqlite, thread_history_1.sqlite, codex-dev.db, ...) and
/// Claude Desktop keep their databases in WAL mode and stay open while the
/// app, its app-server or a CLI session runs. Copying the main file alone
/// loses every transaction still in the -wal file (often minutes to days of
/// threads), and copying both while they are written can tear them. The
/// SQLite online-backup API reads a single committed state, WAL included,
/// without blocking the writer, so a backup is consistent even when the
/// agents keep running.
/// </para>
/// </summary>
internal static class SqliteSnapshot
{
    private static readonly string[] Extensions = [".sqlite", ".sqlite3", ".db"];
    private static readonly byte[] Header = "SQLite format 3\0"u8.ToArray();

    /// <summary>
    /// Returns the entries with every SQLite database pointing at a snapshot
    /// in <paramref name="snapshotDir"/>. A database that cannot be
    /// snapshotted is archived as it is, together with its -wal file so no
    /// committed data is lost.
    /// </summary>
    public static List<ArchiveEntry> Apply(
        IReadOnlyList<ArchiveEntry> entries,
        string snapshotDir,
        IOperationLog log,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var databases = entries.Where(e => IsCandidate(e.AbsolutePath)).ToList();
        if (databases.Count == 0)
        {
            log.Debug("No SQLite databases to snapshot");
            return entries.ToList();
        }
        log.Info($"Snapshotting {databases.Count} SQLite database(s) so changes still in their write-ahead logs are included");

        Directory.CreateDirectory(snapshotDir);
        var replaced = new Dictionary<ArchiveEntry, ArchiveEntry>();
        var extra = new List<ArchiveEntry>();
        for (var i = 0; i < databases.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new OperationProgress("Snapshotting databases", i, databases.Count));
            var db = databases[i];
            var target = Path.Combine(snapshotDir, $"{i:D5}-{Path.GetFileName(db.AbsolutePath)}");
            var error = TrySnapshot(db.AbsolutePath, target);
            if (error is null)
            {
                replaced[db] = db with { AbsolutePath = target };
                log.Debug($"  snapshot {db.RelativePath}: {OperationLogExtensions.Bytes(Length(db.AbsolutePath))} + wal {OperationLogExtensions.Bytes(Length(db.AbsolutePath + "-wal"))} -> {OperationLogExtensions.Bytes(Length(target))}");
                continue;
            }

            log.Warn($"Could not snapshot SQLite database '{db.AbsolutePath}' ({error}); archived as is, with its write-ahead log");
            var wal = db.AbsolutePath + "-wal";
            if (File.Exists(wal))
            {
                extra.Add(new ArchiveEntry(db.RelativePath + "-wal", wal));
            }
        }
        progress?.Report(new OperationProgress("Snapshotting databases", databases.Count, databases.Count));

        return entries.Select(e => replaced.TryGetValue(e, out var r) ? r : e).Concat(extra).ToList();
    }

    private static long Length(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    internal static bool IsCandidate(string path)
    {
        if (!Extensions.Any(x => path.EndsWith(x, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> head = stackalloc byte[16];
            return fs.Read(head) == 16 && head.SequenceEqual(Header);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <returns>Null on success, otherwise the reason.</returns>
    internal static string? TrySnapshot(string sourcePath, string targetPath)
    {
        // Read-only first so the live database is never touched; a WAL
        // database whose -shm is missing can only be opened read-write.
        string? lastError = null;
        foreach (var mode in new[] { SqliteOpenMode.ReadOnly, SqliteOpenMode.ReadWrite })
        {
            try
            {
                if (File.Exists(targetPath))
                {
                    File.Delete(targetPath);
                }
                using (var source = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = sourcePath,
                    Mode = mode,
                    Pooling = false,
                    DefaultTimeout = 30,
                }.ToString()))
                using (var dest = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = targetPath,
                    Mode = SqliteOpenMode.ReadWriteCreate,
                    Pooling = false,
                }.ToString()))
                {
                    source.Open();
                    dest.Open();
                    source.BackupDatabase(dest);
                }
                return null;
            }
            catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                lastError = ex.Message;
            }
        }
        return lastError;
    }
}
