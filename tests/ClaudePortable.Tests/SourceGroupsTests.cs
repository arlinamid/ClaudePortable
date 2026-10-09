using System.IO.Compression;
using ClaudePortable.Core.Abstractions;
using ClaudePortable.Core.Archive;
using ClaudePortable.Core.Backup;
using ClaudePortable.Core.Manifest;
using ClaudePortable.Core.Restore;

namespace ClaudePortable.Tests;

public class SourceGroupsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"cp-groups-{Guid.NewGuid():N}");

    [Fact]
    public void Resolve_NoOptions_MeansEverything()
    {
        Assert.Null(SourceGroups.Resolve(null, null));
        Assert.Null(SourceGroups.Resolve(["all"], null));
    }

    [Fact]
    public void Resolve_ClaudeAliasExpandsToAllClaudeGroups()
    {
        var selection = SourceGroups.Resolve(["claude"], null);

        Assert.NotNull(selection);
        Assert.Equal(
            new[] { "claude-code", "claude-desktop", "cowork" },
            selection!.OrderBy(g => g, StringComparer.Ordinal));
    }

    [Fact]
    public void Resolve_AcceptsCommaListsAndSkip()
    {
        Assert.Equal(new[] { "codex" }, SourceGroups.Resolve(["claude-code,codex"], ["claude-code"]));
        Assert.Equal(
            new[] { "claude-code", "claude-desktop", "codex" },
            SourceGroups.Resolve(null, ["cowork"])!.OrderBy(g => g, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("claud")]
    [InlineData("gemini")]
    public void Resolve_RejectsUnknownNames(string name)
    {
        Assert.Throws<ArgumentException>(() => SourceGroups.Resolve([name], null));
    }

    [Fact]
    public void Resolve_RejectsEmptySelection()
    {
        Assert.Throws<ArgumentException>(() => SourceGroups.Resolve(["codex"], ["codex"]));
    }

    [Fact]
    public void SharedAgentsStoreBelongsToBothAgents()
    {
        Assert.Equal(new[] { "claude-code", "codex" }, SourceGroups.ForSourceKey("agentsUserProfile"));
        Assert.True(SourceGroups.IsSelected(SourceGroups.ForSourceKey("agentsUserProfile"), new HashSet<string> { "codex" }));
        Assert.False(SourceGroups.IsSelected(SourceGroups.ForSourceKey("agentsUserProfile"), new HashSet<string> { "claude-desktop" }));
    }

    [Fact]
    public void ContentsOf_IgnoresSharedStoreWhenAnAgentIsPresent()
    {
        var manifest = new BackupManifest
        {
            ArchiveTargets = new()
            {
                ["codex/dotcodex"] = @"C:\Users\a\.codex",
                ["agents/dotagents"] = @"C:\Users\a\.agents",
            },
        };

        Assert.Equal(new[] { "codex" }, SourceGroups.ContentsOf(manifest));
    }

    [Fact]
    public void ContentsOf_LegacyManifestUsesSourcePaths()
    {
        var manifest = new BackupManifest
        {
            SourcePaths = new()
            {
                ["claudeDesktopAppData"] = "x",
                ["claudeCodeUserProfile"] = "y",
                ["coworkProject:abc"] = "z",
            },
        };

        Assert.Equal(new[] { "claude-desktop", "cowork", "claude-code" }, SourceGroups.ContentsOf(manifest));
    }

    [Fact]
    public void GroupsToRestore_OnlyWhatIsBothInBackupAndSelected()
    {
        var manifest = new BackupManifest
        {
            ArchiveTargets = new()
            {
                ["claude-code/dotclaude"] = "x",
                ["codex/dotcodex"] = "y",
            },
        };

        Assert.Equal(
            new[] { "codex" },
            RestoreEngine.GroupsToRestore(manifest, new HashSet<string> { "codex", "claude-desktop" }));
        Assert.Equal(
            new[] { "claude-code", "codex" },
            RestoreEngine.GroupsToRestore(manifest, null).OrderBy(g => g, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Backup_CodexOnly_LeavesClaudeOutAndTagsFilename()
    {
        var dotClaude = Dir("profile", ".claude");
        File.WriteAllText(Path.Combine(dotClaude, "settings.json"), "{}");
        var dotCodex = Dir("profile", ".codex");
        File.WriteAllText(Path.Combine(dotCodex, "config.toml"), "x");
        var dotAgents = Dir("profile", ".agents", "skills", "s");
        File.WriteAllText(Path.Combine(dotAgents, "SKILL.md"), "x");

        var discovery = new FakeDiscovery(
            new("claudeCodeUserProfile", dotClaude, true, "test"),
            new("codexUserProfile", dotCodex, true, "test"),
            new("agentsUserProfile", Path.Combine(_root, "profile", ".agents"), true, "test"),
            new("claudeDesktopAppData", Path.Combine(_root, "missing"), false, "test"));
        var engine = new BackupEngine(discovery, new ZipArchiveWriter(), NullCoworkProjectDiscovery.Instance);

        var outcome = await engine.CreateBackupAsync(new(Dir("out"), RetentionTier.Daily, Groups: new HashSet<string> { "codex" }));

        Assert.EndsWith("_codex_daily.zip", outcome.ZipPath, StringComparison.Ordinal);
        Assert.Equal(new[] { "codex" }, outcome.Manifest.Groups);
        Assert.Empty(outcome.SkippedPaths); // unselected Claude Desktop is not "missing"
        using var zip = ZipFile.OpenRead(outcome.ZipPath);
        Assert.NotNull(zip.GetEntry("codex/dotcodex/config.toml"));
        Assert.NotNull(zip.GetEntry("agents/dotagents/skills/s/SKILL.md"));
        Assert.DoesNotContain(zip.Entries, e => e.FullName.StartsWith("claude-code/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Backup_FullSelectionKeepsLegacyFilenameAndNoGroupsField()
    {
        var dotClaude = Dir("p2", ".claude");
        File.WriteAllText(Path.Combine(dotClaude, "settings.json"), "{}");
        var engine = new BackupEngine(
            new FakeDiscovery(new DiscoveredClaudePath("claudeCodeUserProfile", dotClaude, true, "test")),
            new ZipArchiveWriter(),
            NullCoworkProjectDiscovery.Instance);

        var outcome = await engine.CreateBackupAsync(new(Dir("out2"), RetentionTier.Daily));

        Assert.Matches(@"claude-backup_[^_]+_[^_]+_daily\.zip$", Path.GetFileName(outcome.ZipPath));
        Assert.Null(outcome.Manifest.Groups);
    }

    [Fact]
    public async Task Backup_SelectionWithNothingOnThisMachineFails()
    {
        var engine = new BackupEngine(
            new FakeDiscovery(new DiscoveredClaudePath("codexUserProfile", Path.Combine(_root, "nope"), false, "test")),
            new ZipArchiveWriter(),
            NullCoworkProjectDiscovery.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            engine.CreateBackupAsync(new(Dir("out3"), RetentionTier.Daily, Groups: new HashSet<string> { "codex" })));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        GC.SuppressFinalize(this);
    }

    private string Dir(params string[] parts)
    {
        var path = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class FakeDiscovery(params DiscoveredClaudePath[] paths) : IPathDiscovery
    {
        public IReadOnlyList<DiscoveredClaudePath> Discover() => paths;
    }
}
