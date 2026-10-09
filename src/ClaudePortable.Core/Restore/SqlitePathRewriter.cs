using Microsoft.Data.Sqlite;

namespace ClaudePortable.Core.Restore;

/// <summary>
/// Applies a text rewrite to every TEXT value of every ordinary table in a
/// SQLite database, in place. Used on the restore staging copy, never on a
/// live database. The rewrite runs as a C# function registered on the
/// connection, so SQL-side replace() (case-sensitive, no path boundaries) is
/// not involved.
/// </summary>
internal static class SqlitePathRewriter
{
    private static readonly byte[] Header = "SQLite format 3\0"u8.ToArray();

    public static bool IsSqliteDatabase(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Span<byte> head = stackalloc byte[16];
            return fs.Read(head) == 16 && head.SequenceEqual(Header);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Read-only: number of TEXT values for which <paramref name="mayNeedRewrite"/> is true.</summary>
    public static int CountCandidates(string databasePath, Func<string, bool> mayNeedRewrite)
    {
        var count = 0;
        Scan(databasePath, mayNeedRewrite, _ => count++, maxValuesPerColumn: int.MaxValue);
        return count;
    }

    /// <summary>
    /// Read-only: calls <paramref name="visit"/> for TEXT values passing
    /// <paramref name="filter"/>, at most <paramref name="maxValuesPerColumn"/> per column.
    /// </summary>
    public static void Scan(string databasePath, Func<string, bool> filter, Action<string> visit, int maxValuesPerColumn)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        };
        try
        {
            using var connection = new SqliteConnection(builder.ToString());
            connection.Open();
            connection.CreateFunction<string?, bool>("ap_hit", v => v is not null && filter(v), isDeterministic: true);
            foreach (var table in OrdinaryTables(connection))
            {
                foreach (var column in Columns(connection, table))
                {
                    using var select = connection.CreateCommand();
                    select.CommandText =
                        $"SELECT {Quote(column)} FROM {Quote(table)} " +
                        $"WHERE typeof({Quote(column)}) = 'text' AND ap_hit({Quote(column)}) LIMIT {maxValuesPerColumn}";
                    try
                    {
                        using var reader = select.ExecuteReader();
                        while (reader.Read())
                        {
                            visit(reader.GetString(0));
                        }
                    }
                    catch (SqliteException)
                    {
                    }
                }
            }
        }
        catch (SqliteException)
        {
        }
    }

    /// <returns>Number of values changed.</returns>
    public static int Rewrite(string databasePath, Func<string, bool> mayNeedRewrite, Func<string, string> rewrite)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        };

        try
        {
            using var connection = new SqliteConnection(builder.ToString());
            connection.Open();
            connection.CreateFunction<string?, bool>("ap_hit", v => v is not null && mayNeedRewrite(v), isDeterministic: true);
            connection.CreateFunction<string?, string?>("ap_rewrite", v => v is null ? null : rewrite(v), isDeterministic: true);

            var tables = OrdinaryTables(connection);
            var changed = 0;
            using var transaction = connection.BeginTransaction();
            foreach (var table in tables)
            {
                foreach (var column in Columns(connection, table))
                {
                    using var update = connection.CreateCommand();
                    update.Transaction = transaction;
                    update.CommandText =
                        $"UPDATE {Quote(table)} SET {Quote(column)} = ap_rewrite({Quote(column)}) " +
                        $"WHERE typeof({Quote(column)}) = 'text' AND ap_hit({Quote(column)})";
                    try
                    {
                        changed += update.ExecuteNonQuery();
                    }
                    catch (SqliteException)
                    {
                        // Generated or otherwise read-only column: leave it.
                    }
                }
            }
            transaction.Commit();
            return changed;
        }
        catch (SqliteException)
        {
            // Corrupt / encrypted / not really SQLite: restore it unchanged.
            return 0;
        }
    }

    /// <summary>
    /// User tables, minus SQLite internals and the shadow tables behind
    /// virtual tables (e.g. FTS indexes), which must only change through
    /// their virtual table.
    /// </summary>
    private static List<string> OrdinaryTables(SqliteConnection connection)
    {
        var all = new List<(string Name, bool IsVirtual)>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT name, sql FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var sql = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                all.Add((reader.GetString(0), sql.StartsWith("CREATE VIRTUAL", StringComparison.OrdinalIgnoreCase)));
            }
        }
        var virtualNames = all.Where(t => t.IsVirtual).Select(t => t.Name + "_").ToList();
        return all
            .Where(t => !t.IsVirtual && !virtualNames.Any(v => t.Name.StartsWith(v, StringComparison.OrdinalIgnoreCase)))
            .Select(t => t.Name)
            .ToList();
    }

    private static List<string> Columns(SqliteConnection connection, string table)
    {
        var columns = new List<string>();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({Quote(table)})";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            columns.Add(reader.GetString(1));
        }
        return columns;
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
