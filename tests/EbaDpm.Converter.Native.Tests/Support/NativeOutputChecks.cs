using System.Data.OleDb;
using System.Globalization;
using EbaDpm.Converter.Native.Schema;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Native.Tests.Support;

/// <summary>
/// Independent whole-table checks of a native output against its Access source: per-column
/// aggregate fingerprints (counts, sums of integers and booleans, total text length), foreign keys
/// and indexes. Each returns the list of differences, empty when the output agrees.
/// </summary>
internal static class NativeOutputChecks
{
    private static readonly int[] IntegerTypes = [2, 3, 17, 20];

    public static IReadOnlyList<string> RowCountDifferences(OleDbConnection access, SqliteConnection sqlite, IEnumerable<string> tables)
    {
        var differences = new List<string>();
        foreach (var table in tables)
        {
            var expected = AccessSqliteComparer.AccessCount(access, table);
            var actual = AccessSqliteComparer.ScalarLong(sqlite, $"SELECT COUNT(1) FROM {AccessSqliteComparer.Q(table)}");
            if (expected != actual)
            {
                differences.Add($"{table}: Access {expected} rows, SQLite {actual} rows");
            }
        }

        return differences;
    }

    public static IReadOnlyList<string> FingerprintDifferences(
        OleDbConnection access, SqliteConnection sqlite, AccessProbe probe, IEnumerable<string> tables)
    {
        var differences = new List<string>();

        // Access LEN counts UTF-16 code units; SQLite LENGTH counts code points (they differ for characters outside
        // the BMP). A function registered on the connection counts UTF-16 units so both sides measure the same thing.
        sqlite.CreateFunction<string?, long?>("utf16len", text => text?.Length);

        foreach (var table in tables)
        {
            var aggregates = new List<(string Column, string Label, string Access, string Sqlite)>();
            foreach (var column in probe.ColumnsOf(table))
            {
                var a = AccessSqliteComparer.A(column.Name);
                var q = AccessSqliteComparer.Q(column.Name);
                aggregates.Add((column.Name, "count", $"COUNT({a})", $"COUNT({q})"));
                if (IntegerTypes.Contains(column.DataType))
                {
                    aggregates.Add((column.Name, "sum", $"SUM({a})", $"SUM({q})"));
                }
                else if (column.DataType == 11)
                {
                    aggregates.Add((column.Name, "true-count", $"SUM(IIF({a} = True, 1, 0))", $"SUM({q})"));
                }
                else if (column.DataType == 130)
                {
                    aggregates.Add((column.Name, "text-length", $"SUM(LEN({a}))", $"SUM(utf16len({q}))"));
                }
            }

            var expected = Scalars(access, $"SELECT {string.Join(", ", aggregates.Select(x => x.Access))} FROM {AccessSqliteComparer.A(table)}");
            var actual = Scalars(sqlite, $"SELECT {string.Join(", ", aggregates.Select(x => x.Sqlite))} FROM {AccessSqliteComparer.Q(table)}");
            for (var i = 0; i < aggregates.Count; i++)
            {
                if (expected[i] != actual[i])
                {
                    differences.Add($"{table}.{aggregates[i].Column} {aggregates[i].Label}: Access {expected[i]}, SQLite {actual[i]}");
                }
            }
        }

        return differences;
    }

    public static IReadOnlyList<string> GuidFormatDifferences(SqliteConnection sqlite, AccessProbe probe, IEnumerable<string> tables)
    {
        var differences = new List<string>();
        foreach (var table in tables)
        {
            foreach (var column in probe.ColumnsOf(table).Where(c => c.DataType == 72))
            {
                var q = AccessSqliteComparer.Q(column.Name);
                var bad = AccessSqliteComparer.ScalarLong(
                    sqlite,
                    $"SELECT COUNT(1) FROM {AccessSqliteComparer.Q(table)} WHERE {q} IS NOT NULL AND "
                    + $"(typeof({q}) <> 'text' OR length({q}) <> 36 OR {q} <> lower({q}) OR {q} GLOB '*[^0-9a-f-]*' "
                    + $"OR substr({q},9,1) <> '-' OR substr({q},14,1) <> '-' OR substr({q},19,1) <> '-' OR substr({q},24,1) <> '-')");
                if (bad > 0)
                {
                    differences.Add($"{table}.{column.Name}: {bad} values are not lower-case 36-character GUIDs");
                }
            }
        }

        return differences;
    }

    public static IReadOnlyList<string> BooleanFormatDifferences(SqliteConnection sqlite, AccessProbe probe, IEnumerable<string> tables)
    {
        var differences = new List<string>();
        foreach (var table in tables)
        {
            foreach (var column in probe.ColumnsOf(table).Where(c => c.DataType == 11))
            {
                var q = AccessSqliteComparer.Q(column.Name);
                var bad = AccessSqliteComparer.ScalarLong(
                    sqlite,
                    $"SELECT COUNT(1) FROM {AccessSqliteComparer.Q(table)} WHERE {q} IS NOT NULL AND (typeof({q}) <> 'integer' OR {q} NOT IN (0, 1))");
                if (bad > 0)
                {
                    differences.Add($"{table}.{column.Name}: {bad} values are not 0 or 1");
                }
            }
        }

        return differences;
    }

    public static IReadOnlyList<string> ForeignKeyDifferences(SqliteConnection sqlite, AccessProbe probe, IEnumerable<string> exportedTables)
    {
        var exported = exportedTables.ToHashSet(StringComparer.Ordinal);
        var differences = new List<string>();
        foreach (var table in exported)
        {
            var expected = probe.ForeignKeys.Count(f => f.Child == table && exported.Contains(f.Parent));
            var actual = AccessSqliteComparer.Rows(sqlite, $"PRAGMA foreign_key_list({AccessSqliteComparer.Q(table)})")
                .Select(r => Convert.ToInt64(r[0], CultureInfo.InvariantCulture)).Distinct().Count();
            if (expected != actual)
            {
                differences.Add($"{table}: {expected} relationships in Access, {actual} foreign keys in SQLite");
            }
        }

        return differences;
    }

    public static IReadOnlyList<string> IndexDifferences(SqliteConnection sqlite, AccessProbe probe, IEnumerable<string> exportedTables)
    {
        var exported = exportedTables.ToHashSet(StringComparer.Ordinal);
        var differences = new List<string>();
        foreach (var index in probe.Indexes.Where(i => exported.Contains(i.Table)))
        {
            if (probe.PrimaryKeys.TryGetValue(index.Table, out var key) && key.SequenceEqual(index.Columns))
            {
                continue;
            }

            var found = false;
            var uniqueWanted = probe.Indexes.Any(i => i.Table == index.Table && i.Columns.SequenceEqual(index.Columns) && i.Unique);
            foreach (var sqliteIndex in AccessSqliteComparer.Rows(sqlite, $"PRAGMA index_list({AccessSqliteComparer.Q(index.Table)})"))
            {
                var name = (string)sqliteIndex[1]!;
                var columns = AccessSqliteComparer.Rows(sqlite, $"PRAGMA index_info({AccessSqliteComparer.Q(name)})")
                    .OrderBy(r => Convert.ToInt32(r[0], CultureInfo.InvariantCulture)).Select(r => (string)r[2]!).ToList();
                if (columns.SequenceEqual(index.Columns) && (Convert.ToInt32(sqliteIndex[2], CultureInfo.InvariantCulture) == 1) == uniqueWanted)
                {
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                differences.Add($"{index.Table}.{index.Name} ({string.Join(",", index.Columns)}{(uniqueWanted ? ", unique" : string.Empty)}): no such index in SQLite");
            }
        }

        return differences;
    }

    public static string[] Exported(AccessProbe probe)
        => [.. probe.Tables.Where(t => !NativeExclusions.IsExcluded(t))];

    private static List<decimal> Scalars(OleDbConnection connection, string sql)
    {
        using var command = new OleDbCommand(sql, connection) { CommandTimeout = 0 };
        using var reader = command.ExecuteReader();
        reader.Read();
        return [.. Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? 0m : Convert.ToDecimal(reader.GetValue(i), CultureInfo.InvariantCulture))];
    }

    private static List<decimal> Scalars(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        reader.Read();
        return [.. Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? 0m : Convert.ToDecimal(reader.GetValue(i), CultureInfo.InvariantCulture))];
    }
}
