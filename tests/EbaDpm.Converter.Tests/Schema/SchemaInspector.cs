using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Tests.Schema;

/// <summary>A column as described by <c>PRAGMA table_info</c>.</summary>
/// <param name="Ordinal">Declared position (0-based), used to compare column order.</param>
/// <param name="Name">Column name.</param>
/// <param name="DeclaredType">Type exactly as it appears in the DDL (e.g. "INTEGER", "TEXT", "BOOLEAN").</param>
/// <param name="NotNull">Whether the column has <c>NOT NULL</c>.</param>
/// <param name="PrimaryKeyPosition">
/// 0 if the column is not part of the PK; otherwise its 1-based position within the PK
/// (relevant for composite PKs, e.g. mCellPosition(CellID, OrdinateID)).
/// </param>
internal sealed record ColumnInfo(int Ordinal, string Name, string DeclaredType, bool NotNull, int PrimaryKeyPosition)
{
    public override string ToString() =>
        $"{Ordinal}:{Name} {DeclaredType} {(NotNull ? "NOT NULL" : "NULL")} " +
        (PrimaryKeyPosition > 0 ? $"PK#{PrimaryKeyPosition}" : "(no PK)");
}

/// <summary>A foreign key as described by <c>PRAGMA foreign_key_list</c>.</summary>
/// <param name="FromColumn">Source column, in the table that declares the FK.</param>
/// <param name="ToTable">Target table.</param>
/// <param name="ToColumn">
/// Target column. It can be <c>null</c> when the DDL uses <c>REFERENCES table</c> without an
/// explicit column (implicit reference to the PK of the target table). This is a real case of the
/// contract (mResourceFile, mRewriteURI) and it is compared as is, without resolving it, because
/// both sides (generated and reference) come from the same literal DDL.
/// </param>
internal sealed record ForeignKeyInfo(string FromColumn, string ToTable, string? ToColumn)
{
    public override string ToString() => $"{FromColumn} -> {ToTable}({ToColumn ?? "<implicit PK>"})";
}

/// <summary>An explicit index (not auto-generated for PK/UNIQUE) as it appears in <c>sqlite_master</c>.</summary>
internal sealed record IndexInfo(string TableName, string IndexName, bool IsUnique, IReadOnlyList<string> Columns)
{
    public override string ToString() =>
        $"{TableName}.{IndexName} ({(IsUnique ? "UNIQUE " : "")}{string.Join(", ", Columns)})";
}

/// <summary>
/// Thin wrapper over the SQLite schema introspection PRAGMAs, used both on the generated
/// database and on the reference database.
/// </summary>
internal static class SchemaInspector
{
    /// <summary>User table names (excludes the internal <c>sqlite_*</c> ones), sorted.</summary>
    public static List<string> GetTableNames(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite\\_%' ESCAPE '\\' ORDER BY name;";
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    public static List<ColumnInfo> GetColumns(SqliteConnection connection, string tableName)
    {
        using var command = connection.CreateCommand();
        // The table name cannot be parameterised in a PRAGMA; it always comes from
        // sqlite_master (not from external input), so the interpolation is safe.
        command.CommandText = $"PRAGMA table_info('{EscapeForPragma(tableName)}');";
        using var reader = command.ExecuteReader();

        var columns = new List<ColumnInfo>();
        while (reader.Read())
        {
            // columns: cid, name, type, notnull, dflt_value, pk
            var ordinal = reader.GetInt32(0);
            var name = reader.GetString(1);
            var type = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
            var notNull = reader.GetInt32(3) != 0;
            var pk = reader.GetInt32(5);
            columns.Add(new ColumnInfo(ordinal, name, type, notNull, pk));
        }

        return columns;
    }

    public static List<ForeignKeyInfo> GetForeignKeys(SqliteConnection connection, string tableName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA foreign_key_list('{EscapeForPragma(tableName)}');";
        using var reader = command.ExecuteReader();

        var foreignKeys = new List<ForeignKeyInfo>();
        while (reader.Read())
        {
            // columns: id, seq, table, from, to, on_update, on_delete, match
            var toTable = reader.GetString(2);
            var from = reader.GetString(3);
            var to = reader.IsDBNull(4) ? null : reader.GetString(4);
            foreignKeys.Add(new ForeignKeyInfo(from, toTable, to));
        }

        return foreignKeys;
    }

    public static long GetRowCount(SqliteConnection connection, string tableName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM \"{tableName.Replace("\"", "\"\"")}\";";
        var result = command.ExecuteScalar();
        return Convert.ToInt64(result);
    }

    /// <summary>
    /// Explicit indexes of the whole database (excludes the <c>sqlite_autoindex_*</c> that SQLite
    /// creates automatically for PK/UNIQUE, which are already covered by the PK comparison).
    /// </summary>
    public static List<IndexInfo> GetExplicitIndexes(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT tbl_name, name FROM sqlite_master " +
            "WHERE type = 'index' AND name NOT LIKE 'sqlite_autoindex_%' ORDER BY tbl_name, name;";
        using var reader = command.ExecuteReader();

        var indexes = new List<(string Table, string Name)>();
        while (reader.Read())
        {
            indexes.Add((reader.GetString(0), reader.GetString(1)));
        }

        reader.Close();

        var result = new List<IndexInfo>();
        foreach (var (table, indexName) in indexes)
        {
            using var infoCommand = connection.CreateCommand();
            infoCommand.CommandText = $"PRAGMA index_info('{EscapeForPragma(indexName)}');";
            using var infoReader = infoCommand.ExecuteReader();
            var columns = new List<string>();
            while (infoReader.Read())
            {
                columns.Add(infoReader.GetString(2));
            }

            infoReader.Close();

            using var listCommand = connection.CreateCommand();
            listCommand.CommandText = $"PRAGMA index_list('{EscapeForPragma(table)}');";
            using var listReader = listCommand.ExecuteReader();
            var isUnique = false;
            while (listReader.Read())
            {
                if (listReader.GetString(1) == indexName)
                {
                    isUnique = listReader.GetInt32(2) != 0;
                    break;
                }
            }

            result.Add(new IndexInfo(table, indexName, isUnique, columns));
        }

        return result;
    }

    private static string EscapeForPragma(string identifier) => identifier.Replace("'", "''");
}
