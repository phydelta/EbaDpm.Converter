using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Tests.Dictionary;

/// <summary>
/// Thin wrapper over <see cref="SqliteConnection"/> for the ad-hoc queries of the dictionary
/// tests: counts, scalars and loose rows, without repeating the <c>SqliteCommand</c>/
/// <c>SqliteDataReader</c> boilerplate in every test.
/// </summary>
internal static class QueryHelpers
{
    public static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var result = command.ExecuteScalar();
        return result is null or DBNull ? 0 : Convert.ToInt64(result);
    }

    public static List<string?[]> Rows(SqliteConnection connection, string sql, int columnCount)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();

        var rows = new List<string?[]>();
        while (reader.Read())
        {
            var row = new string?[columnCount];
            for (var i = 0; i < columnCount; i++)
            {
                row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i).ToString();
            }

            rows.Add(row);
        }

        return rows;
    }

    /// <summary>
    /// Counts the rows of <paramref name="childTable"/> whose (non-null) <paramref name="childKeyColumn"/>
    /// has no matching row in <paramref name="parentTable"/>.<paramref name="parentKeyColumn"/>.
    /// Returns up to <paramref name="sampleLimit"/> sample values for the failure message.
    /// </summary>
    public static (long OrphanCount, List<string?> Samples) FindOrphans(
        SqliteConnection connection,
        string childTable,
        string childKeyColumn,
        string parentTable,
        string parentKeyColumn,
        int sampleLimit = 10)
    {
        var countSql =
            $"""
            SELECT COUNT(*) FROM "{childTable}" c
            WHERE c."{childKeyColumn}" IS NOT NULL
              AND NOT EXISTS (SELECT 1 FROM "{parentTable}" p WHERE p."{parentKeyColumn}" = c."{childKeyColumn}")
            """;
        var count = Scalar(connection, countSql);

        var samples = new List<string?>();
        if (count > 0)
        {
            var sampleSql =
                $"""
                SELECT DISTINCT c."{childKeyColumn}" FROM "{childTable}" c
                WHERE c."{childKeyColumn}" IS NOT NULL
                  AND NOT EXISTS (SELECT 1 FROM "{parentTable}" p WHERE p."{parentKeyColumn}" = c."{childKeyColumn}")
                LIMIT {sampleLimit}
                """;
            samples = Rows(connection, sampleSql, 1).Select(r => r[0]).ToList();
        }

        return (count, samples);
    }

    public static void AssertNoOrphans(
        SqliteConnection connection,
        string childTable,
        string childKeyColumn,
        string parentTable,
        string parentKeyColumn)
    {
        var (count, samples) = FindOrphans(connection, childTable, childKeyColumn, parentTable, parentKeyColumn);
        Assert.True(
            count == 0,
            $"{childTable}.{childKeyColumn} -> {parentTable}.{parentKeyColumn}: {count} orphan rows. "
            + $"Examples: {string.Join(", ", samples)}");
    }
}
