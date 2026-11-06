using ClaudePortable.Core.Restore;

namespace ClaudePortable.Tests;

public class VersionGatingTests
{
    [Fact]
    public void SameVersion_ReturnsOk()
    {
        var r = VersionGating.Evaluate("1.3883.0.0", "1.3883.0.0");
        Assert.Equal(VersionGateLevel.Ok, r.Level);
    }

    [Theory]
    [InlineData("1.3883.0.0", "1.3900.0.0")]
    // The claude.ai build went from 1.x straight to 4.x; its data migrates.
    [InlineData("1.22209.0.0", "4.29.0.0")]
    public void NewerInstalledApp_ReturnsInfo(string backup, string installed)
    {
        var r = VersionGating.Evaluate(backup, installed);
        Assert.Equal(VersionGateLevel.Info, r.Level);
        Assert.Contains("newer", r.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OlderInstalledApp_SameMajor_ReturnsWarn()
    {
        var r = VersionGating.Evaluate("1.3900.0.0", "1.3883.0.0");
        Assert.Equal(VersionGateLevel.Warn, r.Level);
        Assert.Contains("Minor-version mismatch", r.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OlderInstalledApp_OtherMajor_ReturnsBlock()
    {
        var r = VersionGating.Evaluate("4.29.0.0", "1.3883.0.0");
        Assert.Equal(VersionGateLevel.Block, r.Level);
        Assert.Contains("Major-version mismatch", r.Message, StringComparison.Ordinal);
    }

    [Theory]
    // claude.ai build in the backup, Microsoft Store build here (and back):
    // independent numbering, so even "older" is not a block.
    [InlineData("4.29.0.0", "direct", "3.6556.2.0", "store")]
    [InlineData("3.6556.2.0", "store", "4.29.0.0", "direct")]
    [InlineData("0.9.3", "squirrel", "4.29.0.0", "direct")]
    public void DifferentEditions_AreNotCompared(string backup, string backupEdition, string installed, string installedEdition)
    {
        var r = VersionGating.Evaluate(backup, backupEdition, installed, installedEdition);
        Assert.Equal(VersionGateLevel.Info, r.Level);
        Assert.Contains("not compared", r.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SameEdition_IsCompared()
    {
        var r = VersionGating.Evaluate("4.29.0.0", "direct", "1.3883.0.0", "direct");
        Assert.Equal(VersionGateLevel.Block, r.Level);
    }

    [Fact]
    public void InstalledMissing_ReturnsWarn()
    {
        var r = VersionGating.Evaluate("1.3883.0.0", null);
        Assert.Equal(VersionGateLevel.Warn, r.Level);
        Assert.Contains("not installed", r.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BackupMissing_ReturnsInfo()
    {
        var r = VersionGating.Evaluate(null, "1.3883.0.0");
        Assert.Equal(VersionGateLevel.Info, r.Level);
    }

    [Fact]
    public void BothMissing_ReturnsInfo()
    {
        var r = VersionGating.Evaluate(null, null);
        Assert.Equal(VersionGateLevel.Info, r.Level);
    }

    [Fact]
    public void UnparseableVersion_ReturnsWarn()
    {
        var r = VersionGating.Evaluate("garbage", "also-garbage");
        Assert.Equal(VersionGateLevel.Warn, r.Level);
    }

    [Theory]
    [InlineData("1.3883", "1.3883")]
    [InlineData("1.3883.0", "1.3883.0")]
    public void ShortVersionStrings_MatchWhenEqual(string b, string i)
    {
        var r = VersionGating.Evaluate(b, i);
        Assert.Equal(VersionGateLevel.Ok, r.Level);
    }
}
