using System.Diagnostics;
using ClaudePortable.Core.Archive;
using ClaudePortable.Core.Manifest;
using ClaudePortable.Core.Restore;

namespace ClaudePortable.Tests;

/// <summary>
/// Junctions inside a backup source (e.g. a skill folder linked to a git
/// checkout elsewhere) must be recorded, not followed, and recreated on
/// restore. Junctions are used because they need no admin rights or
/// Developer Mode, so these tests run on any Windows account.
/// </summary>
public class LinkHandlingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"cp-links-{Guid.NewGuid():N}");

    public LinkHandlingTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void Enumerate_RecordsJunctionButDoesNotFollowIt()
    {
        var external = Dir("external-repo");
        File.WriteAllText(Path.Combine(external, "SKILL.md"), "big external content");
        var source = Dir("source");
        File.WriteAllText(Path.Combine(source, "config.toml"), "x");
        Directory.CreateDirectory(Path.Combine(source, "skills"));
        var junction = Path.Combine(source, "skills", "linked-skill");
        CreateJunction(junction, external);

        var links = new List<BackupLink>();
        var entries = FileEnumerator.Enumerate(source, "codex/dotcodex", new ExclusionGlob(DefaultExclusions.Globs), links).ToList();

        Assert.Contains(entries, e => e.RelativePath == "codex/dotcodex/config.toml");
        Assert.DoesNotContain(entries, e => e.RelativePath.Contains("linked-skill", StringComparison.Ordinal));
        var link = Assert.Single(links);
        Assert.Equal("codex/dotcodex/skills/linked-skill", link.Path);
        Assert.Equal(external, link.Target.TrimEnd('\\'), ignoreCase: true);
    }

    [Fact]
    public void Enumerate_FollowsSourceRootThatIsItselfAJunction()
    {
        var real = Dir("real-dotclaude");
        File.WriteAllText(Path.Combine(real, "settings.json"), "{}");
        var rootLink = Path.Combine(_root, "dotclaude-link");
        CreateJunction(rootLink, real);

        var entries = FileEnumerator.Enumerate(rootLink, "claude-code/dotclaude", new ExclusionGlob(DefaultExclusions.Globs)).ToList();

        Assert.Contains(entries, e => e.RelativePath == "claude-code/dotclaude/settings.json");
    }

    [Fact]
    public void Enumerate_DoesNotRecordLinksInsideExcludedFolders()
    {
        var external = Dir("pnpm-store-pkg");
        var source = Dir("project");
        Directory.CreateDirectory(Path.Combine(source, "node_modules"));
        CreateJunction(Path.Combine(source, "node_modules", "left-pad"), external);

        var links = new List<BackupLink>();
        _ = FileEnumerator.Enumerate(source, "cowork-projects/abc", new ExclusionGlob(DefaultExclusions.Globs), links).ToList();

        Assert.Empty(links);
    }

    [Fact]
    public void Enumerate_PrunesExcludedDirectories()
    {
        var source = Dir("pruned");
        File.WriteAllText(Path.Combine(source, "keep.txt"), "x");
        Directory.CreateDirectory(Path.Combine(source, "node_modules", "pkg"));
        File.WriteAllText(Path.Combine(source, "node_modules", "pkg", "index.js"), "x");

        var entries = FileEnumerator.Enumerate(source, "p", new ExclusionGlob(DefaultExclusions.Globs)).ToList();

        Assert.Equal("p/keep.txt", Assert.Single(entries).RelativePath);
    }

    [Fact]
    public void RecreateLinks_CreatesJunctionWhenTargetExists()
    {
        var external = Dir("restore-target");
        var restored = Dir("restored-dotcodex");
        var links = new[] { new BackupLink("codex/dotcodex/skills/linked-skill", external) };

        var warnings = RestoreEngine.RecreateLinks(links, "codex/dotcodex", restored, @"C:\Users\nobody", @"C:\Users\nobody");

        Assert.Empty(warnings);
        var linkDir = new DirectoryInfo(Path.Combine(restored, "skills", "linked-skill"));
        Assert.True(linkDir.Exists);
        Assert.NotNull(linkDir.LinkTarget);
    }

    [Fact]
    public void RecreateLinks_RerootsTargetUnderNewProfile()
    {
        var oldProfile = Path.Combine(_root, "old-profile");
        var newProfile = Dir("new-profile");
        var newTarget = Path.Combine(newProfile, "src", "my-skill");
        Directory.CreateDirectory(newTarget);
        var restored = Dir("restored-reroot");
        var links = new[] { new BackupLink("claude-code/dotclaude/skills/my-skill", Path.Combine(oldProfile, "src", "my-skill")) };

        var warnings = RestoreEngine.RecreateLinks(links, "claude-code/dotclaude", restored, oldProfile, newProfile);

        Assert.Empty(warnings);
        var linkTarget = new DirectoryInfo(Path.Combine(restored, "skills", "my-skill")).LinkTarget;
        Assert.Equal(newTarget, linkTarget?.TrimEnd('\\'), ignoreCase: true);
    }

    [Fact]
    public void RecreateLinks_RelativeTargetResolvesAgainstRestoredLocation()
    {
        // ~\.claude\skills\hf-cli -> ..\..\.agents\skills\hf-cli, as written
        // by `npx skills add`. Works with or without symlink privileges:
        // without them the engine falls back to a junction.
        var profile = Dir("profile-rel");
        var agentsSkill = Path.Combine(profile, ".agents", "skills", "hf-cli");
        Directory.CreateDirectory(agentsSkill);
        var dotClaude = Path.Combine(profile, ".claude");
        Directory.CreateDirectory(dotClaude);
        var links = new[] { new BackupLink("claude-code/dotclaude/skills/hf-cli", @"..\..\.agents\skills\hf-cli") };

        var warnings = RestoreEngine.RecreateLinks(links, "claude-code/dotclaude", dotClaude, profile, profile);

        Assert.Empty(warnings);
        var linkDir = new DirectoryInfo(Path.Combine(dotClaude, "skills", "hf-cli"));
        Assert.NotNull(linkDir.LinkTarget);
        Assert.Equal(
            agentsSkill,
            Path.GetFullPath(Path.Combine(linkDir.Parent!.FullName, linkDir.LinkTarget!)).TrimEnd('\\'),
            ignoreCase: true);
    }

    [Fact]
    public void RecreateLinks_WarnsWhenTargetMissing()
    {
        var restored = Dir("restored-missing");
        var links = new[] { new BackupLink("codex/dotcodex/skills/gone", Path.Combine(_root, "does-not-exist")) };

        var warnings = RestoreEngine.RecreateLinks(links, "codex/dotcodex", restored, @"C:\Users\a", @"C:\Users\a");

        Assert.Contains("does not exist", Assert.Single(warnings), StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(restored, "skills", "gone")));
    }

    [Fact]
    public void RecreateLinks_IgnoresLinksOfOtherPrefixes()
    {
        var restored = Dir("restored-other");
        var links = new[] { new BackupLink("claude-code/dotclaude/skills/x", Path.Combine(_root, "does-not-exist")) };

        Assert.Empty(RestoreEngine.RecreateLinks(links, "codex/dotcodex", restored, @"C:\Users\a", @"C:\Users\a"));
    }

    public void Dispose()
    {
        try
        {
            // Delete junctions first so Directory.Delete never walks into
            // their targets.
            foreach (var dir in Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories)
                         .Select(d => new DirectoryInfo(d))
                         .Where(d => d.LinkTarget is not null)
                         .ToList())
            {
                dir.Delete();
            }
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        GC.SuppressFinalize(this);
    }

    private string Dir(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void CreateJunction(string link, string target)
    {
        var psi = new ProcessStartInfo("cmd.exe", $"/d /c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var proc = Process.Start(psi)!;
        proc.WaitForExit();
        Assert.Equal(0, proc.ExitCode);
    }
}
