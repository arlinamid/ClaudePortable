using ClaudePortable.Core.Archive;
using ClaudePortable.Core.Discovery;
using ClaudePortable.Core.Restore;

namespace ClaudePortable.Tests;

/// <summary>
/// Path handling that must hold on any machine, not just a default
/// C:\Users\&lt;name&gt; profile: other drives, profiles outside \Users,
/// user names that prefix each other or contain spaces, Store package
/// folders with any publisher id, and untrusted ZIP entry names.
/// </summary>
public class PathPortabilityTests
{
    [Theory]
    [InlineData(@"C:\Users\alice", @"D:\Users\alice")]
    [InlineData(@"C:\Users\alice", @"D:\Profiles\alice.CORP")]
    [InlineData(@"E:\Home\alice", @"C:\Users\bob")]
    public void ReplaceProfileIn_RewritesWholeProfileRootInAllEncodings(string oldProfile, string newProfile)
    {
        var escaped = oldProfile.Replace(@"\", @"\\", StringComparison.Ordinal);
        var forward = oldProfile.Replace('\\', '/');
        var input = $$"""{"a":"{{escaped}}\\.claude","b":"{{forward}}/proj"}""" + "\n" + $"command = '{oldProfile}\\.local\\bin\\tool.exe'";

        var (count, result) = PathRewriter.ReplaceProfileIn(input, oldProfile, newProfile);

        Assert.Equal(3, count);
        Assert.Contains(newProfile.Replace(@"\", @"\\", StringComparison.Ordinal) + @"\\.claude", result, StringComparison.Ordinal);
        Assert.Contains(newProfile.Replace('\\', '/') + "/proj", result, StringComparison.Ordinal);
        Assert.Contains(newProfile + @"\.local", result, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"C:\Users\sam", @"C:\Users\samantha\notes")]
    [InlineData(@"C:\Users\John", @"C:\Users\John Smith\notes")]
    public void ReplaceProfileIn_RespectsPathBoundary(string oldProfile, string otherPath)
    {
        var (count, result) = PathRewriter.ReplaceProfileIn(otherPath, oldProfile, @"D:\Users\x");

        Assert.Equal(0, count);
        Assert.Equal(otherPath, result);
    }

    [Fact]
    public void ReplaceProfileIn_ProfileRootAtEndOfQuotedValue()
    {
        var (count, result) = PathRewriter.ReplaceProfileIn(@"home = 'C:\Users\alice'", @"C:\Users\alice", @"C:\Users\bob");

        Assert.Equal(1, count);
        Assert.Equal(@"home = 'C:\Users\bob'", result);
    }

    [Fact]
    public void ExpandCandidate_ResolvesPackageWildcardForAnyPublisherId()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cp-pkg-{Guid.NewGuid():N}");
        var resolved = Path.Combine(root, "Packages", "OpenAI.Codex_abc123xyz", "LocalCache", "Roaming", "Codex");
        Directory.CreateDirectory(resolved);
        try
        {
            var candidate = Path.Combine(root, "Packages", "OpenAI.Codex_*", "LocalCache", "Roaming", "Codex");

            Assert.Equal(resolved, WindowsPathDiscovery.ExpandCandidate(candidate));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ExpandCandidate_SkipsUnsetEnvironmentVariable()
    {
        Assert.Null(WindowsPathDiscovery.ExpandCandidate($"%CP_TEST_UNSET_{Guid.NewGuid():N}%"));
    }

    [Theory]
    [InlineData("claudeDesktopAppData", "claude-desktop/appdata")]
    [InlineData("claudeCodeUserProfile", "claude-code/dotclaude")]
    [InlineData("codexUserProfile", "codex/dotcodex")]
    [InlineData("codexDesktopAppData", "codex-desktop/appdata")]
    [InlineData("agentsUserProfile", "agents/dotagents")]
    public void SourceLayout_MapsBothWays(string key, string prefix)
    {
        Assert.Equal(prefix, SourceLayout.PrefixFor(key));
        Assert.Equal(key, SourceLayout.KeyFor(prefix));
    }

    [Fact]
    public void SourceLayout_UnknownPrefixHasNoKey()
    {
        Assert.Null(SourceLayout.KeyFor("cowork-projects/abc123"));
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("claude-code/../../evil.txt")]
    [InlineData(@"C:\Windows\evil.txt")]
    public void SafeEntryPath_RejectsEntriesEscapingTheRoot(string entryName)
    {
        var root = Path.Combine(Path.GetTempPath(), "cp-extract-root");

        Assert.Throws<InvalidDataException>(() => RestoreEngine.SafeEntryPath(root, entryName));
    }

    [Fact]
    public void SafeEntryPath_AllowsNormalEntries()
    {
        var root = Path.Combine(Path.GetTempPath(), "cp-extract-root");

        Assert.Equal(
            Path.Combine(root, "codex", "dotcodex", "config.toml"),
            RestoreEngine.SafeEntryPath(root, "codex/dotcodex/config.toml"));
    }
}
