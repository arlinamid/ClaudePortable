using ClaudePortable.Core.Abstractions;
using ClaudePortable.Core.Manifest;

namespace ClaudePortable.Core.Archive;

public static class FileEnumerator
{
    // Walks one directory level at a time instead of using a recursive
    // EnumerateFiles, for two reasons:
    //
    // 1. Links. A recursive enumeration follows NTFS junctions and directory
    //    symlinks, so a skill folder junctioned to a git checkout elsewhere
    //    on disk would be copied into the ZIP wholesale (and restored as a
    //    real folder), and a link cycle would recurse until the path got too
    //    long. Link directories BELOW the source root are therefore recorded
    //    as BackupLink entries and not descended into; restore recreates
    //    them. The source root itself may be a link and is always walked.
    //    Only real links count: OneDrive / cloud-files folders are also
    //    reparse points but have no LinkTarget and are walked normally.
    //
    // 2. Pruning. A directory matched by an exclusion glob ending in "/**"
    //    (node_modules, Cache, packages, ...) is skipped without listing its
    //    contents, which is the same result as filtering every file in it.
    //
    // IgnoreInaccessible=true is the key knob for per-level listing: without
    // it, the FIRST UnauthorizedAccessException on %APPDATA%\Claude (a
    // reparse point into a Store-app sandbox where some children deny access)
    // would end the listing early. AttributesToSkip=0 makes sure we don't
    // silently drop System/Hidden files, which Store apps use liberally.
    private static readonly EnumerationOptions Options = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false,
    };

    public static IEnumerable<ArchiveEntry> Enumerate(
        string absoluteRoot,
        string archivePrefix,
        ExclusionGlob exclusions,
        ICollection<BackupLink>? links = null)
    {
        if (!Directory.Exists(absoluteRoot))
        {
            yield break;
        }

        var rootFull = Path.GetFullPath(absoluteRoot).TrimEnd('\\', '/');
        var pending = new Stack<string>();
        pending.Push(rootFull);

        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            List<FileSystemInfo> children;
            try
            {
                children = new DirectoryInfo(dir).EnumerateFileSystemInfos("*", Options).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var child in children)
            {
                var relativeToSource = Path.GetRelativePath(rootFull, child.FullName).Replace('\\', '/');
                var archivePath = string.IsNullOrEmpty(archivePrefix)
                    ? relativeToSource
                    : $"{archivePrefix.TrimEnd('/')}/{relativeToSource}";

                if (child is DirectoryInfo subdir)
                {
                    // Probe with a trailing slash: only globs that exclude a
                    // whole subtree ("**/Cache/**") match a bare directory.
                    var dirExcluded = exclusions.IsExcluded(archivePath + "/")
                        || exclusions.IsExcluded(relativeToSource + "/");

                    if (TryGetLinkTarget(subdir) is { } target)
                    {
                        if (!dirExcluded)
                        {
                            links?.Add(new BackupLink(archivePath, target));
                        }
                        continue;
                    }

                    if (!dirExcluded)
                    {
                        pending.Push(subdir.FullName);
                    }
                    continue;
                }

                if (exclusions.IsExcluded(archivePath) || exclusions.IsExcluded(relativeToSource))
                {
                    continue;
                }

                yield return new ArchiveEntry(archivePath, child.FullName);
            }
        }
    }

    /// <summary>
    /// The raw target of a junction or directory symlink, or null for a
    /// plain directory or a non-link reparse point (cloud-files placeholder,
    /// dedup, ...). The target may be relative for symlinks.
    /// </summary>
    internal static string? TryGetLinkTarget(DirectoryInfo dir)
    {
        if ((dir.Attributes & FileAttributes.ReparsePoint) == 0)
        {
            return null;
        }
        try
        {
            return dir.LinkTarget;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
