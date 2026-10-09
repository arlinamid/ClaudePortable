using System.Text.RegularExpressions;

namespace ClaudePortable.Core.Restore;

/// <summary>
/// Claude Code keeps each project's history in ~\.claude\projects\&lt;name&gt;,
/// where &lt;name&gt; is the project's absolute path with every character
/// other than A-Z, a-z, 0-9 replaced by '-'. For example
/// C:\Users\János\proj becomes C--Users-J-nos-proj. When the restore profile
/// differs from the backup one, those folder names must follow, or Claude
/// Code on the new machine does not see the restored history at all.
/// </summary>
public static partial class ClaudeCodeProjectFolders
{
    [GeneratedRegex("[^a-zA-Z0-9]")]
    private static partial Regex NonAlphanumeric();

    public static string Encode(string absolutePath) => NonAlphanumeric().Replace(absolutePath, "-");

    /// <returns>Warnings for folders that could not be renamed.</returns>
    public static IReadOnlyList<string> RenameForProfile(string projectsDir, string oldUserProfile, string newUserProfile)
    {
        var warnings = new List<string>();
        var oldPrefix = Encode(oldUserProfile.TrimEnd('\\', '/'));
        var newPrefix = Encode(newUserProfile.TrimEnd('\\', '/'));
        if (!Directory.Exists(projectsDir) || string.Equals(oldPrefix, newPrefix, StringComparison.Ordinal))
        {
            return warnings;
        }

        foreach (var dir in Directory.GetDirectories(projectsDir))
        {
            var name = Path.GetFileName(dir);
            var matches = name.Equals(oldPrefix, StringComparison.OrdinalIgnoreCase)
                || name.StartsWith(oldPrefix + "-", StringComparison.OrdinalIgnoreCase);
            if (!matches)
            {
                continue;
            }

            var target = Path.Combine(projectsDir, newPrefix + name[oldPrefix.Length..]);
            try
            {
                if (!Directory.Exists(target))
                {
                    Directory.Move(dir, target);
                }
                else
                {
                    MergeInto(dir, target);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"Could not rename Claude Code project folder '{name}' for the new user profile: {ex.Message}");
            }
        }
        return warnings;
    }

    private static void MergeInto(string source, string target)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(target, Path.GetRelativePath(source, file));
            if (File.Exists(dest))
            {
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Move(file, dest);
        }
        Directory.Delete(source, recursive: true);
    }
}
