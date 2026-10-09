using ClaudePortable.Core.Restore;
using Microsoft.Data.Sqlite;

namespace ClaudePortable.Tests;

/// <summary>
/// Restoring a backup from a laptop profile "Janos" onto a desktop profile
/// "János". Codex could not resume any conversation afterwards ("failed to
/// resolve rollout path C:\Users\Janos\.codex\sessions\...jsonl") because its
/// SQLite state and session files still pointed into the old profile.
/// </summary>
public class ProfileRenameRestoreTests : IDisposable
{
    private const string OldProfile = @"C:\Users\Janos";
    private const string NewProfile = @"C:\Users\János";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"cp-profile-{Guid.NewGuid():N}");

    public ProfileRenameRestoreTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void CodexStateDatabase_RolloutPathsFollowTheNewProfile()
    {
        var db = Path.Combine(_root, "codex", "dotcodex", "state_5.sqlite");
        Directory.CreateDirectory(Path.GetDirectoryName(db)!);
        Exec(db, """
            CREATE TABLE threads (id TEXT PRIMARY KEY, rollout_path TEXT NOT NULL, cwd TEXT, sandbox_policy TEXT, tokens_used INTEGER);
            INSERT INTO threads VALUES
              ('t1', 'C:\Users\Janos\.codex\sessions\2026\09\10\rollout-1.jsonl', '\\?\C:\Users\Janos\Documents\proj',
               '{"writable_roots":["C:\\Users\\Janos\\Documents\\proj"]}', 42),
              ('t2', 'C:\Users\Janosik\.codex\sessions\x.jsonl', 'C:\Users\Janosik', NULL, 7);
            """);

        new PathRewriter().Rewrite(_root, OldProfile, NewProfile);

        using var con = Open(db);
        Assert.Equal(@"C:\Users\János\.codex\sessions\2026\09\10\rollout-1.jsonl", Scalar(con, "SELECT rollout_path FROM threads WHERE id='t1'"));
        Assert.Equal(@"\\?\C:\Users\János\Documents\proj", Scalar(con, "SELECT cwd FROM threads WHERE id='t1'"));
        Assert.Equal("""{"writable_roots":["C:\\Users\\János\\Documents\\proj"]}""", Scalar(con, "SELECT sandbox_policy FROM threads WHERE id='t1'"));
        Assert.Equal(@"C:\Users\Janosik\.codex\sessions\x.jsonl", Scalar(con, "SELECT rollout_path FROM threads WHERE id='t2'"));
        Assert.Equal(42L, Convert.ToInt64(new SqliteCommand("SELECT tokens_used FROM threads WHERE id='t1'", con).ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void SessionJsonlFiles_AreRewritten()
    {
        var jsonl = Path.Combine(_root, "codex", "dotcodex", "sessions", "rollout-1.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(jsonl)!);
        File.WriteAllText(jsonl, """
            {"type":"session_meta","payload":{"cwd":"C:\\Users\\Janos\\Documents\\proj"}}
            {"type":"message","payload":{"text":"hello"}}
            """);

        new PathRewriter().Rewrite(_root, OldProfile, NewProfile);

        var text = File.ReadAllText(jsonl);
        Assert.Contains(@"C:\\Users\\János\\Documents\\proj", text, StringComparison.Ordinal);
        Assert.DoesNotContain(@"\\Janos\\", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ReverseDirection_MatchesJsonUnicodeEscapedUserName()
    {
        // Some serializers write "á" as \u00e1.
        var json = Path.Combine(_root, "settings.json");
        File.WriteAllText(json, """{"p":"C:\\Users\\J\u00e1nos\\proj","q":"C:\\Users\\J\u00E1nos"}""");

        new PathRewriter().Rewrite(_root, NewProfile, OldProfile);

        Assert.Equal("""{"p":"C:\\Users\\Janos\\proj","q":"C:\\Users\\Janos"}""", File.ReadAllText(json));
    }

    [Fact]
    public void NewProfileInsideOldProfile_IsRewrittenOnlyOnce()
    {
        // --target-user pointing into a folder under the old profile: the
        // rewritten value starts with the old profile again and must not be
        // rewritten a second time by the user-name rule.
        var newProfile = @"C:\Users\Janos\restore-test\János";
        var (count, result) = PathRewriter.RewriteText(
            @"C:\Users\Janos\.codex\sessions\a.jsonl|""C:\\Users\\Janos\\.codex""",
            OldProfile,
            newProfile);

        Assert.Equal(2, count);
        Assert.Equal(@"C:\Users\Janos\restore-test\János\.codex\sessions\a.jsonl|""C:\\Users\\Janos\\restore-test\\János\\.codex""", result);
    }

    [Fact]
    public void UserNameRule_StillCoversOtherDrives()
    {
        var (count, result) = PathRewriter.RewriteText(@"D:\Users\Janos\data", OldProfile, NewProfile);

        Assert.Equal(1, count);
        Assert.Equal(@"D:\Users\János\data", result);
    }

    [Fact]
    public void SameProfile_TouchesNothing()
    {
        var json = Path.Combine(_root, "a.json");
        File.WriteAllText(json, """{"p":"C:\\Users\\Janos\\x"}""");
        var before = File.GetLastWriteTimeUtc(json);

        var result = new PathRewriter().Rewrite(_root, OldProfile, @"c:\users\janos\");

        Assert.Equal(0, result.FilesScanned);
        Assert.Equal(before, File.GetLastWriteTimeUtc(json));
    }

    [Theory]
    [InlineData(@"C:\Users\arlin\Documents\Happy_hour_prezentáció_vibecoding", "C--Users-arlin-Documents-Happy-hour-prezent-ci--vibecoding")]
    [InlineData(@"D:\tool\ClaudePortable", "D--tool-ClaudePortable")]
    [InlineData(@"C:\Users\János", "C--Users-J-nos")]
    public void ClaudeCodeProjectFolderEncoding_MatchesClaudeCode(string path, string expected)
    {
        Assert.Equal(expected, ClaudeCodeProjectFolders.Encode(path));
    }

    [Fact]
    public void ClaudeCodeProjectFolders_AreRenamedForTheNewProfile()
    {
        var projects = Path.Combine(_root, "projects");
        Directory.CreateDirectory(Path.Combine(projects, "C--Users-Janos-Documents-proj"));
        File.WriteAllText(Path.Combine(projects, "C--Users-Janos-Documents-proj", "s1.jsonl"), "{}");
        Directory.CreateDirectory(Path.Combine(projects, "C--Users-Janos"));
        Directory.CreateDirectory(Path.Combine(projects, "D--tool-x"));

        var warnings = ClaudeCodeProjectFolders.RenameForProfile(projects, OldProfile, NewProfile);

        Assert.Empty(warnings);
        Assert.True(File.Exists(Path.Combine(projects, "C--Users-J-nos-Documents-proj", "s1.jsonl")));
        Assert.True(Directory.Exists(Path.Combine(projects, "C--Users-J-nos")));
        Assert.True(Directory.Exists(Path.Combine(projects, "D--tool-x")));
        Assert.False(Directory.Exists(Path.Combine(projects, "C--Users-Janos-Documents-proj")));
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

    private static SqliteConnection Open(string db)
    {
        var con = new SqliteConnection($"Data Source={db};Pooling=False");
        con.Open();
        return con;
    }

    private static void Exec(string db, string sql)
    {
        using var con = Open(db);
        using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static string? Scalar(SqliteConnection con, string sql)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar() as string;
    }
}
