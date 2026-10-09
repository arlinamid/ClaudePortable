using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using ClaudePortable.Core.Abstractions;

namespace ClaudePortable.Core.Archive;

/// <summary>
/// Writes the backup ZIP in a single pass: every source file is opened once,
/// streamed into its ZIP entry and hashed on the way. manifest.json, which
/// carries the content hash, is written as the last entry.
/// <para>
/// A backup must never hang on one file. On a user's machine, opening a file
/// can block indefinitely: a OneDrive / cloud-files placeholder triggers a
/// download (forever, if the sync client is paused or signed out), and a
/// file whose open request is held by another process or filter driver, or
/// a symlink to an unreachable share, can stall CreateFile as well. So:
/// </para>
/// <list type="bullet">
/// <item>cloud-only placeholders are skipped without being opened;</item>
/// <item>each open runs on its own thread and is abandoned after
/// <see cref="OpenTimeout"/>;</item>
/// <item>reads use overlapped I/O and are cancelled when no data arrives
/// for <see cref="ReadStallTimeout"/>.</item>
/// </list>
/// Every skipped file is reported through the warning sink with its path.
/// </summary>
public sealed class ZipArchiveWriter : IArchiveWriter
{
    // Not in the FileAttributes enum: set on cloud-files placeholders whose
    // content is not on disk (OneDrive, Google Drive, Dropbox Files On-Demand).
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;
    private const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;
    private const FileAttributes CloudOnlyMask = FileAttributes.Offline | RecallOnDataAccess | RecallOnOpen;

    private readonly TextWriter _warningSink;
    private readonly Func<string, FileStream> _open;

    public ZipArchiveWriter() : this(Console.Error) { }

    public ZipArchiveWriter(TextWriter warningSink)
        : this(warningSink, OpenReadShared, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60))
    {
    }

    internal ZipArchiveWriter(TextWriter warningSink, Func<string, FileStream> open, TimeSpan openTimeout, TimeSpan readStallTimeout)
    {
        _warningSink = warningSink;
        _open = open;
        OpenTimeout = openTimeout;
        ReadStallTimeout = readStallTimeout;
    }

    public TimeSpan OpenTimeout { get; }

    public TimeSpan ReadStallTimeout { get; }

    public async Task<ArchiveResult> WriteAsync(
        string destinationZipPath,
        IEnumerable<ArchiveEntry> entries,
        string manifestJson,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(new OperationProgress("Preparing"));
        var orderedEntries = entries
            .OrderBy(e => e.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var tempPath = destinationZipPath + ".tmp";
        if (File.Exists(tempPath))
        {
            File.Delete(tempPath);
        }

        long totalBytes = 0;
        var filesWritten = 0;
        var buffer = new byte[81920];
        string digest;

        try
        {
            using var contentHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var fs = File.Create(tempPath))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                for (var i = 0; i < orderedEntries.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var entry = orderedEntries[i];
                    if ((i & 0x3F) == 0)
                    {
                        progress?.Report(new OperationProgress("Writing archive", i, orderedEntries.Count));
                    }

                    if (IsCloudOnly(entry.AbsolutePath))
                    {
                        Warn($"skipping cloud-only file (not downloaded by the sync client) '{entry.AbsolutePath}'");
                        continue;
                    }

                    var source = await OpenWithTimeoutAsync(entry.AbsolutePath, cancellationToken).ConfigureAwait(false);
                    if (source is null)
                    {
                        continue;
                    }

                    await using (source)
                    {
                        var zipEntry = zip.CreateEntry(entry.RelativePath, CompressionLevel.Optimal);
                        await using var dest = zipEntry.Open();
                        using var fileHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                        var copied = await CopyWithStallTimeoutAsync(source, dest, fileHash, buffer, entry.AbsolutePath, cancellationToken)
                            .ConfigureAwait(false);
                        if (copied is null)
                        {
                            // The entry stays in the ZIP truncated; it is
                            // left out of the content hash and file count.
                            continue;
                        }

                        totalBytes += copied.Value;
                        filesWritten++;
                        // Content hash = SHA-256 over "<path>\n<sha256(file)>"
                        // per file, in archive order. Per-file digests let a
                        // file that fails mid-read stay out of the total.
                        contentHash.AppendData(Encoding.UTF8.GetBytes(entry.RelativePath + "\n"));
                        contentHash.AppendData(fileHash.GetHashAndReset());
                    }
                }
                progress?.Report(new OperationProgress("Writing archive", orderedEntries.Count, orderedEntries.Count));

                digest = Convert.ToHexString(contentHash.GetHashAndReset()).ToLowerInvariant();
                var finalManifest = manifestJson.Replace(Manifest.ManifestBuilder.Sha256Placeholder, digest, StringComparison.Ordinal);
                var manifestEntry = zip.CreateEntry("manifest.json", CompressionLevel.Optimal);
                await using var ms = manifestEntry.Open();
                await using var writer = new StreamWriter(ms, new UTF8Encoding(false));
                await writer.WriteAsync(finalManifest).ConfigureAwait(false);
                progress?.Report(new OperationProgress("Finalising archive"));
            }

            File.Move(tempPath, destinationZipPath, overwrite: true);
            return new ArchiveResult(totalBytes, filesWritten, digest);
        }
        catch
        {
            // Cancelled or failed: don't leave a multi-GB .tmp in the
            // user's sync folder for OneDrive to upload.
            TryDelete(tempPath);
            throw;
        }
    }

    private static bool IsCloudOnly(string path)
    {
        try
        {
            return (File.GetAttributes(path) & CloudOnlyMask) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false; // let the open report the real problem
        }
    }

    /// <summary>
    /// Open on a dedicated thread so a CreateFile that never returns can be
    /// abandoned. The stuck thread is left behind (Windows offers no way to
    /// cancel a blocked synchronous open); if it ever completes, the handle
    /// is closed straight away.
    /// </summary>
    private async Task<FileStream?> OpenWithTimeoutAsync(string path, CancellationToken cancellationToken)
    {
        var open = Task.Factory.StartNew(
            () => _open(path),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        try
        {
            return await open.WaitAsync(OpenTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _ = open.ContinueWith(t => t.Result.Dispose(), TaskContinuationOptions.OnlyOnRanToCompletion);
            Warn($"skipping file that did not open within {OpenTimeout.TotalSeconds:0}s (locked by another program, or a cloud / network file that is not available) '{path}'");
            return null;
        }
        catch (OperationCanceledException)
        {
            _ = open.ContinueWith(t => t.Result.Dispose(), TaskContinuationOptions.OnlyOnRanToCompletion);
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Warn($"skipping locked or unreadable file '{path}': {ex.Message}");
            return null;
        }
    }

    /// <returns>Bytes copied, or null when the file failed or stalled mid-read.</returns>
    private async Task<long?> CopyWithStallTimeoutAsync(
        FileStream source,
        Stream dest,
        IncrementalHash fileHash,
        byte[] buffer,
        string path,
        CancellationToken cancellationToken)
    {
        long copied = 0;
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            while (true)
            {
                stall.CancelAfter(ReadStallTimeout);
                var read = await source.ReadAsync(buffer.AsMemory(), stall.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    return copied;
                }
                fileHash.AppendData(buffer, 0, read);
                await dest.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                copied += read;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Warn($"file stopped delivering data for {ReadStallTimeout.TotalSeconds:0}s, kept incomplete and left out of the content hash '{path}'");
            return null;
        }
        catch (IOException ex)
        {
            Warn($"read failed, kept incomplete and left out of the content hash '{path}': {ex.Message}");
            return null;
        }
    }

    internal static FileStream OpenReadShared(string path)
    {
        // Asynchronous = overlapped I/O, so a stalled read can be cancelled
        // (CancelIoEx) instead of pinning a thread forever.
        return new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 1,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    private void Warn(string message)
    {
        lock (_warningSink)
        {
            _warningSink.WriteLine($"warning: {message}");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
