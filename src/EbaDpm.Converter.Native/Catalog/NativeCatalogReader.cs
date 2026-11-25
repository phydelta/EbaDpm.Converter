using System.Data;
using System.Data.OleDb;
using EbaDpm.Converter.Core.Access;

namespace EbaDpm.Converter.Native.Catalog;

/// <summary>
/// Reads the catalogue of an Access database with <see cref="OleDbConnection.GetOleDbSchemaTable"/>
/// (Tables, Columns, Primary_Keys, Foreign_Keys, Indexes): never a data row. Only user tables
/// (<c>TABLE_TYPE = 'TABLE'</c>) are read: <c>MSys*</c> come as another table type. Temporary
/// tables (names starting with <c>~</c>) are skipped too. The native exclusions
/// (<see cref="Schema.NativeExclusions"/>) are NOT applied here: the catalogue is complete, so that
/// the baseline can be compared with another one table for table.
/// </summary>
public static class NativeCatalogReader
{
    public static NativeCatalog Read(string accdbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accdbPath);

        using var connection = new OleDbConnection(AceProvider.ConnectionString(accdbPath));
        connection.Open();
        return Read(connection);
    }

    public static NativeCatalog Read(OleDbConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var tableNames = ReadTableNames(connection);
        var tableSet = new HashSet<string>(tableNames, StringComparer.Ordinal);

        var columns = ReadColumns(connection, tableSet);
        var primaryKeys = ReadPrimaryKeys(connection, tableSet);

        var tables = tableNames
            .Select(name => new NativeTable(
                name,
                columns.GetValueOrDefault(name) ?? [],
                primaryKeys.GetValueOrDefault(name) ?? []))
            .ToList();

        return new NativeCatalog(tables, ReadForeignKeys(connection, tableSet), ReadIndexes(connection, tableSet));
    }

    private static List<string> ReadTableNames(OleDbConnection connection)
    {
        using var schema = connection.GetOleDbSchemaTable(OleDbSchemaGuid.Tables, null)!;
        var names = new List<string>();
        foreach (DataRow row in schema.Rows)
        {
            if (!string.Equals(row["TABLE_TYPE"] as string, "TABLE", StringComparison.Ordinal))
            {
                continue;
            }

            if (row["TABLE_NAME"] is string name && name.Length > 0 && !name.StartsWith('~'))
            {
                names.Add(name);
            }
        }

        names.Sort(StringComparer.Ordinal);
        return names;
    }

    private static Dictionary<string, IReadOnlyList<NativeColumn>> ReadColumns(OleDbConnection connection, HashSet<string> tables)
    {
        using var schema = connection.GetOleDbSchemaTable(OleDbSchemaGuid.Columns, null)!;
        return schema.Rows.Cast<DataRow>()
            .Where(row => tables.Contains((string)row["TABLE_NAME"]))
            .GroupBy(row => (string)row["TABLE_NAME"], StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<NativeColumn>)group
                    .OrderBy(row => Convert.ToInt64(row["ORDINAL_POSITION"]))
                    .Select(row => new NativeColumn(
                        (string)row["COLUMN_NAME"],
                        Convert.ToInt32(row["DATA_TYPE"]),
                        Convert.ToBoolean(row["IS_NULLABLE"])))
                    .ToList(),
                StringComparer.Ordinal);
    }

    private static Dictionary<string, IReadOnlyList<string>> ReadPrimaryKeys(OleDbConnection connection, HashSet<string> tables)
    {
        using var schema = connection.GetOleDbSchemaTable(OleDbSchemaGuid.Primary_Keys, null)!;
        return schema.Rows.Cast<DataRow>()
            .Where(row => tables.Contains((string)row["TABLE_NAME"]))
            .GroupBy(row => (string)row["TABLE_NAME"], StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<string>)group
                    .OrderBy(row => Convert.ToInt64(row["ORDINAL"]))
                    .Select(row => (string)row["COLUMN_NAME"])
                    .ToList(),
                StringComparer.Ordinal);
    }

    private static List<NativeForeignKey> ReadForeignKeys(OleDbConnection connection, HashSet<string> tables)
    {
        using var schema = connection.GetOleDbSchemaTable(OleDbSchemaGuid.Foreign_Keys, null)!;
        return schema.Rows.Cast<DataRow>()
            .Where(row => tables.Contains((string)row["FK_TABLE_NAME"]) && tables.Contains((string)row["PK_TABLE_NAME"]))
            .GroupBy(row => ((string)row["FK_TABLE_NAME"], (string)row["FK_NAME"]))
            .Select(group =>
            {
                var ordered = group.OrderBy(row => Convert.ToInt64(row["ORDINAL"])).ToList();
                var first = ordered[0];
                return new NativeForeignKey(
                    group.Key.Item2,
                    group.Key.Item1,
                    ordered.Select(row => (string)row["FK_COLUMN_NAME"]).ToList(),
                    (string)first["PK_TABLE_NAME"],
                    ordered.Select(row => (string)row["PK_COLUMN_NAME"]).ToList(),
                    (string)first["UPDATE_RULE"],
                    (string)first["DELETE_RULE"]);
            })
            .OrderBy(fk => fk.ChildTable, StringComparer.Ordinal)
            .ThenBy(fk => fk.Name, StringComparer.Ordinal)
            .ToList();
    }

    private static List<NativeIndex> ReadIndexes(OleDbConnection connection, HashSet<string> tables)
    {
        using var schema = connection.GetOleDbSchemaTable(OleDbSchemaGuid.Indexes, null)!;
        return schema.Rows.Cast<DataRow>()
            .Where(row => tables.Contains((string)row["TABLE_NAME"])
                && row["INDEX_NAME"] is string
                && !Convert.ToBoolean(row["PRIMARY_KEY"]))
            .GroupBy(row => ((string)row["TABLE_NAME"], (string)row["INDEX_NAME"]))
            .Select(group => new NativeIndex(
                group.Key.Item1,
                group.Key.Item2,
                group.OrderBy(row => Convert.ToInt64(row["ORDINAL_POSITION"])).Select(row => (string)row["COLUMN_NAME"]).ToList(),
                Convert.ToBoolean(group.First()["UNIQUE"])))
            .OrderBy(index => index.Table, StringComparer.Ordinal)
            .ThenBy(index => index.Name, StringComparer.Ordinal)
            .ToList();
    }
}
