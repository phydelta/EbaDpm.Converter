using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Core.Validation;

/// <summary>
/// Thin wrapper over <see cref="SqliteConnection"/> for the ad-hoc queries of the validation
/// harness: counts, scalars and loose rows, without repeating the <c>SqliteCommand</c>/
/// <c>SqliteDataReader</c> boilerplate in every check.
/// </summary>
public static class SqlHelpers
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
    /// The schema is NOT stable across releases: 3.2 has no <c>mAxis.AxisCode</c> nor
    /// <c>mConcept.ReleaseID</c>, for example. Before a plane B check uses a column that may be
    /// missing from an old reference, it is checked here so it can degrade to <c>"skipped"</c>
    /// instead of blowing up with <c>SqliteException</c>.
    /// </summary>
    public static bool ColumnExists(SqliteConnection connection, string table, string column)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = @column";
        command.Parameters.AddWithValue("@column", column);
        var result = command.ExecuteScalar();
        return result is not null && Convert.ToInt64(result) > 0;
    }

    public static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Runs <paramref name="violationSql"/> (it must return a single column with the business key
    /// of each row that violates the invariant) and builds a <see cref="CheckResult"/>. The total
    /// examined comes from <paramref name="totalSql"/> separately: two simple queries are more
    /// readable and almost as fast as a single one with a subquery, and there is no performance
    /// pressure here (the whole of plane A takes seconds, not minutes, WITH the 8 auxiliary
    /// indexes).
    /// </summary>
    public static CheckResult ViolationCheck(
        SqliteConnection connection,
        string id,
        ValidationLayer layer,
        string table,
        string statement,
        string totalSql,
        string violationKeySql)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var total = Scalar(connection, totalSql);
        var violations = Rows(connection, violationKeySql, 1);
        sw.Stop();

        var samples = violations.Select(r => new CheckSample(r[0] ?? "(NULL)")).ToList();
        return CheckResult.FromViolationCount(id, ValidationPlane.A, layer, table, statement, total, violations.Count, sw.ElapsedMilliseconds, samples);
    }

    /// <summary>Variant that takes the violation count directly via <c>COUNT(*)</c>, without samples (cheaper).</summary>
    public static CheckResult CountCheck(
        SqliteConnection connection,
        string id,
        ValidationLayer layer,
        string table,
        string statement,
        string totalSql,
        string violationCountSql)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var total = Scalar(connection, totalSql);
        var violations = Scalar(connection, violationCountSql);
        sw.Stop();

        return CheckResult.FromViolationCount(id, ValidationPlane.A, layer, table, statement, total, violations, sw.ElapsedMilliseconds, []);
    }

    /// <summary>Checks that <paramref name="countSql"/> returns exactly <paramref name="expected"/>.</summary>
    public static CheckResult ExactMatch(
        SqliteConnection connection, string id, ValidationLayer layer, string table, string statement, string countSql, long expected)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var actual = Scalar(connection, countSql);
        sw.Stop();
        var failed = actual == expected ? 0 : 1;
        var samples = failed == 0 ? [] : new List<CheckSample> { new($"observed={actual} expected={expected}") };
        return CheckResult.FromViolationCount(id, ValidationPlane.A, layer, table, statement, 1, failed, sw.ElapsedMilliseconds, samples);
    }

    /// <summary>Checks that two scalar queries return the same value.</summary>
    public static CheckResult CountsEqual(
        SqliteConnection connection, string id, ValidationLayer layer, string table, string statement, string leftSql, string rightSql)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var left = Scalar(connection, leftSql);
        var right = Scalar(connection, rightSql);
        sw.Stop();
        var failed = left == right ? 0 : 1;
        var samples = failed == 0 ? [] : new List<CheckSample> { new($"left={left} right={right}") };
        return CheckResult.FromViolationCount(id, ValidationPlane.A, layer, table, statement, 1, failed, sw.ElapsedMilliseconds, samples);
    }
}
