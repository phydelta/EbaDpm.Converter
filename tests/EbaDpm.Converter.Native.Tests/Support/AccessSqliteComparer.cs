using System.Data;
using System.Data.OleDb;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Native.Tests.Support;

/// <summary>
/// Compares rows of an Access table with the rows of the same table in the native SQLite output,
/// by primary key (the business key of a row in this output: IDs are copied, so they are the
/// same on both sides), cell by cell. The normalisation of each Access value to its SQLite form
/// is written here from the documented type mapping, independently of the production mapping
/// code (<c>NativeTypeMapping</c>), so that an error in the production mapping is not mirrored.
/// </summary>
internal static class AccessSqliteComparer
{
    /// <summary>The column kinds seen while comparing; a data test asserts each one was exercised.</summary>
    public enum Kind { Integer, Boolean, Text, Guid, Decimal, DateTime }

    public sealed record Result(long RowsCompared, long CellsCompared, IReadOnlyList<string> Differences, IReadOnlyDictionary<Kind, long> CellsByKind);

    public static SqliteConnection OpenSqlite(string path, bool readOnly = true)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        connection.Open();
        if (!readOnly)
        {
            // Tampering must not be blocked by the declared foreign keys (Microsoft.Data.Sqlite enables them by default).
            using var pragma = connection.CreateCommand();
            pragma.CommandText = "PRAGMA foreign_keys = OFF";
            pragma.ExecuteNonQuery();
        }

        return connection;
    }

    /// <summary>Fails with every difference spelled out (Assert.Empty truncates long collections).</summary>
    public static void AssertNone(IReadOnlyList<string> differences)
        => Assert.True(differences.Count == 0, $"{differences.Count} difference(s):{Environment.NewLine}" + string.Join(Environment.NewLine, differences.Take(40)));

    public static string Q(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    public static string A(string identifier) => "[" + identifier + "]";

    /// <summary>The expected SQLite value of an Access value, or <c>null</c> for NULL.</summary>
    public static (object? Value, Kind Kind) Normalise(object value, Type fieldType)
    {
        if (value is DBNull)
        {
            return (null, KindOf(fieldType));
        }

        return value switch
        {
            bool b => (b ? 1L : 0L, Kind.Boolean),
            Guid g => (g.ToString("D").ToLowerInvariant(), Kind.Guid),
            DateTime d => (d.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture), Kind.DateTime),
            decimal m => (NormaliseDecimal(m), Kind.Decimal),
            string s => (s, Kind.Text),
            byte or short or int or long => (Convert.ToInt64(value, CultureInfo.InvariantCulture), Kind.Integer),
            _ => throw new NotSupportedException($"The comparer does not know the Access value type {value.GetType().Name}."),
        };
    }

    private static Kind KindOf(Type t)
        => t == typeof(bool) ? Kind.Boolean
            : t == typeof(Guid) ? Kind.Guid
            : t == typeof(DateTime) ? Kind.DateTime
            : t == typeof(decimal) ? Kind.Decimal
            : t == typeof(string) ? Kind.Text
            : Kind.Integer;

    /// <summary>Exact decimal text, no trailing zeros, invariant: a different algorithm from the product's.</summary>
    private static string NormaliseDecimal(decimal value)
    {
        var text = value.ToString(CultureInfo.InvariantCulture);
        if (text.Contains('.', StringComparison.Ordinal))
        {
            text = text.TrimEnd('0').TrimEnd('.');
        }

        return text == "-0" ? "0" : text;
    }

    /// <summary>
    /// Compares the rows of <paramref name="table"/> selected by <paramref name="accessWhereOrderLimit"/>
    /// (for example <c>"ORDER BY [ID]"</c> or <c>"TOP 200"</c> via <paramref name="top"/>), looking each up in
    /// SQLite by <paramref name="primaryKey"/>.
    /// </summary>
    public static Result Compare(
        OleDbConnection access, SqliteConnection sqlite, string table, IReadOnlyList<string> columns,
        IReadOnlyList<string> primaryKey, int? top = null, bool descending = false)
    {
        var differences = new List<string>();
        var byKind = Enum.GetValues<Kind>().ToDictionary(k => k, _ => 0L);
        long rows = 0;
        long cells = 0;

        var order = string.Join(", ", primaryKey.Select(c => A(c) + (descending ? " DESC" : string.Empty)));
        var select = $"SELECT {(top is null ? string.Empty : $"TOP {top} ")}{string.Join(", ", columns.Select(A))} FROM {A(table)} ORDER BY {order}";

        using var lookup = sqlite.CreateCommand();
        lookup.CommandText = $"SELECT {string.Join(", ", columns.Select(Q))} FROM {Q(table)} WHERE "
            + string.Join(" AND ", primaryKey.Select((c, i) => $"{Q(c)} = @k{i}"));

        using var command = new OleDbCommand(select, access) { CommandTimeout = 0 };
        using var reader = command.ExecuteReader();
        var keyIndexes = primaryKey.Select(k => columns.ToList().IndexOf(k)).ToArray();

        while (reader.Read())
        {
            rows++;
            var expected = new (object? Value, Kind Kind)[columns.Count];
            for (var i = 0; i < columns.Count; i++)
            {
                expected[i] = Normalise(reader.GetValue(i), reader.GetFieldType(i));
            }

            lookup.Parameters.Clear();
            for (var k = 0; k < keyIndexes.Length; k++)
            {
                lookup.Parameters.AddWithValue($"@k{k}", expected[keyIndexes[k]].Value ?? DBNull.Value);
            }

            using var found = lookup.ExecuteReader();
            var key = string.Join(",", keyIndexes.Select(i => expected[i].Value));
            if (!found.Read())
            {
                differences.Add($"{table}[{key}]: row missing in SQLite");
                continue;
            }

            for (var i = 0; i < columns.Count; i++)
            {
                cells++;
                byKind[expected[i].Kind]++;
                var actual = found.IsDBNull(i) ? null : found.GetValue(i);
                if (!Equals(expected[i].Value, actual) || (expected[i].Value is not null && expected[i].Value!.GetType() != actual!.GetType()))
                {
                    differences.Add(
                        $"{table}[{key}].{columns[i]}: Access '{Show(expected[i].Value)}' vs SQLite '{Show(actual)}'");
                }
            }
        }

        return new Result(rows, cells, differences, byKind);
    }

    private static string Show(object? value) => value is null ? "NULL" : $"{value.GetType().Name}:{value}";

    public static long ScalarLong(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public static string? ScalarText(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar() as string;
    }

    public static List<object?[]> Rows(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = new List<object?[]>();
        while (reader.Read())
        {
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < row.Length; i++)
            {
                row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }

            rows.Add(row);
        }

        return rows;
    }

    /// <summary>The user tables of an Access database, read from its catalogue (not by the product code).</summary>
    public static List<string> AccessUserTables(OleDbConnection access)
    {
        using var schema = access.GetOleDbSchemaTable(OleDbSchemaGuid.Tables, null)!;
        return schema.Rows.Cast<DataRow>()
            .Where(r => (string)r["TABLE_TYPE"] == "TABLE")
            .Select(r => (string)r["TABLE_NAME"])
            .Where(n => !n.StartsWith('~') && !n.StartsWith("MSys", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    public static long AccessCount(OleDbConnection access, string table)
    {
        using var command = new OleDbCommand($"SELECT COUNT(1) FROM {A(table)}", access) { CommandTimeout = 0 };
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public static List<string> SqliteUserTables(SqliteConnection sqlite)
        => Rows(sqlite, "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite!_%' ESCAPE '!' ORDER BY name")
            .Select(r => (string)r[0]!).ToList();

    public static List<(string Name, int Pk)> SqliteColumns(SqliteConnection sqlite, string table)
        => Rows(sqlite, $"PRAGMA table_info({Q(table)})").Select(r => ((string)r[1]!, Convert.ToInt32(r[5], CultureInfo.InvariantCulture))).ToList();
}
