using System.IO.Compression;
using ClaudePortable.Core.Abstractions;
using ClaudePortable.Core.Archive;
using ClaudePortable.Core.Manifest;

namespace ClaudePortable.Tests;

/// <summary>
/// A backup must finish even when single files misbehave: a file whose open
/// never returns (cloud placeholder that cannot download, a held oplock, a
/// dead network share), an exclusively locked file, or a cloud-only
/// placeholder must be skipped with a warning naming the file.
/// </summary>
public class ZipArchiveWriterTests : IDisposable
{
    private const string Manifest = $$"""{"sha256":"{{ManifestBuilder.Sha256Placeholder}}"}""";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"cp-zipwriter-{Guid.NewGuid():N}");

    public ZipArchiveWriterTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task FileWhoseOpenNeverReturns_IsSkippedAndBackupCompletes()
    {
        var good = WriteFile("good.txt", "fine");
        var hanging = WriteFile("hanging.txt", "never opens");
        using var release = new ManualResetEventSlim(false);
        var warnings = new StringWriter();
        var writer = new ZipArchiveWriter(
            warnings,
            path =>
            {
                if (path == hanging)
                {
                    release.Wait(TimeSpan.FromSeconds(30)); // simulates a CreateFile that never returns
                }
                return ZipArchiveWriter.OpenReadShared(path);
            },
            openTimeout: TimeSpan.FromMilliseconds(300),
            readStallTimeout: TimeSpan.FromSeconds(10));

        var zipPath = Path.Combine(_root, "out.zip");
        var result = await writer.WriteAsync(zipPath, [Entry("a/good.txt", good), Entry("a/hanging.txt", hanging)], Manifest)
            .WaitAsync(TimeSpan.FromSeconds(10));
        release.Set();

        Assert.Equal(1, result.FileCount);
        Assert.Contains("hanging.txt", warnings.ToString(), StringComparison.Ordinal);
        Assert.Contains("did not open within", warnings.ToString(), StringComparison.Ordinal);
        using var zip = ZipFile.OpenRead(zipPath);
        Assert.NotNull(zip.GetEntry("a/good.txt"));
        Assert.Null(zip.GetEntry("a/hanging.txt"));
    }

    [Fact]
    public async Task CloudOnlyPlaceholder_IsSkippedWithoutOpening()
    {
        var good = WriteFile("good.txt", "fine");
        var cloud = WriteFile("cloud.txt", "would trigger a download");
        File.SetAttributes(cloud, File.GetAttributes(cloud) | FileAttributes.Offline);
        var opened = new List<string>();
        var warnings = new StringWriter();
        var writer = new ZipArchiveWriter(
            warnings,
            path => { lock (opened) { opened.Add(path); } return ZipArchiveWriter.OpenReadShared(path); },
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(5));

        var zipPath = Path.Combine(_root, "out.zip");
        var result = await writer.WriteAsync(zipPath, [Entry("good.txt", good), Entry("cloud.txt", cloud)], Manifest);

        Assert.Equal(1, result.FileCount);
        Assert.DoesNotContain(cloud, opened);
        Assert.Contains("cloud-only", warnings.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExclusivelyLockedFile_IsSkipped()
    {
        var good = WriteFile("good.txt", "fine");
        var locked = WriteFile("locked.txt", "busy");
        await using var holder = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var warnings = new StringWriter();

        var result = await new ZipArchiveWriter(warnings)
            .WriteAsync(Path.Combine(_root, "out.zip"), [Entry("good.txt", good), Entry("locked.txt", locked)], Manifest);

        Assert.Equal(1, result.FileCount);
        Assert.Contains("locked.txt", warnings.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ManifestCarriesTheContentHashAndHashIsDeterministic()
    {
        var a = WriteFile("a.txt", "alpha");
        var b = WriteFile("b.txt", "beta");
        IReadOnlyList<ArchiveEntry> entries = [Entry("x/b.txt", b), Entry("x/a.txt", a)];

        var first = await new ZipArchiveWriter(TextWriter.Null).WriteAsync(Path.Combine(_root, "1.zip"), entries, Manifest);
        var second = await new ZipArchiveWriter(TextWriter.Null).WriteAsync(Path.Combine(_root, "2.zip"), entries.Reverse(), Manifest);

        Assert.Equal(first.Sha256, second.Sha256);
        Assert.Equal(9, first.SizeBytes); // "alpha" + "beta"
        using var zip = ZipFile.OpenRead(Path.Combine(_root, "1.zip"));
        using var reader = new StreamReader(zip.GetEntry("manifest.json")!.Open());
        Assert.Contains(first.Sha256, await reader.ReadToEndAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancellation_LeavesNoTempFile()
    {
        var a = WriteFile("a.txt", "alpha");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var zipPath = Path.Combine(_root, "out.zip");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ZipArchiveWriter(TextWriter.Null).WriteAsync(zipPath, [Entry("a.txt", a)], Manifest, cancellationToken: cts.Token));

        Assert.False(File.Exists(zipPath + ".tmp"));
        Assert.False(File.Exists(zipPath));
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(f, FileAttributes.Normal);
            }
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        GC.SuppressFinalize(this);
    }

    private string WriteFile(string name, string content)
    {
        var path = Path.Combine(_root, "src", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static ArchiveEntry Entry(string relative, string absolute) => new(relative, absolute);
}
