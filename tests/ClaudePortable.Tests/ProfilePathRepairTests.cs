using ClaudePortable.Core.Restore;
using Microsoft.Data.Sqlite;

namespace ClaudePortable.Tests;

/// <summary>
/// A desktop (profile "János") that was restored from a laptop backup
/// (profile "Janos") by a version that did not rewrite SQLite / .jsonl:
/// Codex fails with "failed to resolve rollout path C:\Users\Janos\...".
/// The repair works on the live folders, detecting the foreign profile from
/// the data and the current one from the machine.
/// </summary>
public class ProfilePathRepairTests : IDisposable
{
    private const string Current = @"C:\Users\János";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"cp-repair-{Guid.NewGuid():N}");
    private readonly string _codex;
    private readonly string _claude;

    public ProfilePathRepairTests()
    {
        _codex = Path.Combine(_root, ".codex");
        _claude = Path.Combine(_root, ".claude");
        Directory.CreateDirectory(Path.Combine(_codex, "sessions"));
        Directory.CreateDirectory(Path.Combine(_claude, "projects", "C--Users-Janos-Documents-proj"));
        File.WriteAllText(Path.Combine(_claude, "projects", "C--Users-Janos-Documents-proj", "s.jsonl"), """{"cwd":"C:\\Users\\Janos\\Documents\\proj"}""");

        // Already rewritten by the old restore (json/toml were handled).
        File.WriteAllText(Path.Combine(_codex, "config.toml"), "[projects.'C:\\Users\\János\\Documents\\proj']\ntrust_level = 'trusted'\n");
        File.WriteAllText(Path.Combine(_codex, "sessions", "rollout-1.jsonl"), """{"type":"session_meta","payload":{"cwd":"C:\\Users\\Janos\\Documents\\proj"}}""");

        using var con = new SqliteConnection($"Data Source={Path.Combine(_codex, "state_5.sqlite")};Pooling=False");
        con.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE threads (id TEXT PRIMARY KEY, rollout_path TEXT, cwd TEXT);
            INSERT INTO threads VALUES
              ('a', 'C:\Users\Janos\.codex\sessions\rollout-1.jsonl', '\\?\C:\Users\Janos\Documents\proj'),
              ('b', 'C:\Users\Janos\.codex\archived_sessions\rollout-2.jsonl', 'D:\work');
            """;
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void Detect_FindsTheOtherUsersProfileButNotTheCurrentOne()
    {
        var found = ProfilePathRepair.Detect([_codex, _claude], Current);

        var profile = Assert.Single(found);
        Assert.Equal(@"C:\Users\Janos", profile.ProfileRoot);
        Assert.Equal(2, profile.Occurrences);
    }

    [Theory]
    [InlineData("Janos", true)]
    [InlineData("János", true)]
    [InlineData("john.smith", true)]
    [InlineData("...", false)]
    [InlineData("<myname>", false)]
    [InlineData("Public", false)]
    [InlineData("Default", false)]
    public void IsPlausibleProfileName_RejectsPlaceholdersAndSystemProfiles(string name, bool expected)
    {
        Assert.Equal(expected, ProfilePathRepair.IsPlausibleProfileName(name));
    }

    [Fact]
    public void Detect_IgnoresExamplePathsInLogsAndMemories()
    {
        var logs = Path.Combine(_codex, "logs_2.sqlite");
        using (var con = new SqliteConnection($"Data Source={logs};Pooling=False"))
        {
            con.Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE logs (body TEXT);
                INSERT INTO logs VALUES ('see C:\Users\Someone\.codex\config.toml for an example');
                """;
            cmd.ExecuteNonQuery();
        }

        var found = ProfilePathRepair.Detect([_codex], Current);

        Assert.Equal(@"C:\Users\Janos", Assert.Single(found).ProfileRoot);
    }

    [Fact]
    public void Detect_NothingWhenEverythingAlreadyPointsHere()
    {
        Assert.Empty(ProfilePathRepair.Detect([_codex], @"C:\Users\Janos"));
    }

    [Fact]
    public void Repair_PointsCodexAndClaudeCodeAtTheCurrentProfile_AndKeepsOriginals()
    {
        var backup = Path.Combine(_root, "originals");
        var profiles = ProfilePathRepair.Detect([_codex, _claude], Current);

        var result = ProfilePathRepair.Repair(
            [("codexUserProfile", _codex), ("claudeCodeUserProfile", _claude)], profiles, Current, backup);

        using (var con = new SqliteConnection($"Data Source={Path.Combine(_codex, "state_5.sqlite")};Pooling=False"))
        {
            con.Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT rollout_path || '|' || cwd FROM threads WHERE id = 'a'";
            Assert.Equal(@"C:\Users\János\.codex\sessions\rollout-1.jsonl|\\?\C:\Users\János\Documents\proj", cmd.ExecuteScalar());
        }
        Assert.Contains(@"C:\\Users\\János\\Documents\\proj", File.ReadAllText(Path.Combine(_codex, "sessions", "rollout-1.jsonl")), StringComparison.Ordinal);
        Assert.True(Directory.Exists(Path.Combine(_claude, "projects", "C--Users-J-nos-Documents-proj")));
        Assert.True(result.ValuesChanged >= 5);

        // Originals of every changed file were kept.
        Assert.Equal(backup, result.OriginalsBackupFolder);
        Assert.True(File.Exists(Path.Combine(backup, "codexUserProfile", "state_5.sqlite")));
        Assert.Contains(@"\\Janos\\", File.ReadAllText(Path.Combine(backup, "codexUserProfile", "sessions", "rollout-1.jsonl")), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(backup, "codexUserProfile", "config.toml"))); // unchanged, not copied

        // Running it again finds nothing more to do.
        Assert.Empty(ProfilePathRepair.Detect([_codex, _claude], Current));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        GC.SuppressFinalize(this);
    }
}
