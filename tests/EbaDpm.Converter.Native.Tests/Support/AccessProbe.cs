using System.Data;
using System.Data.OleDb;
using System.Globalization;

namespace EbaDpm.Converter.Native.Tests.Support;

/// <summary>
/// Reads facts about an Access database directly from the OLE DB schema rowsets, written
/// independently of the production catalogue reader (<c>NativeCatalogReader</c>).
/// </summary>
internal sealed class AccessProbe
{
    public sealed record Column(string Table, string Name, int DataType, long Ordinal);

    public sealed record Index(string Table, string Name, IReadOnlyList<string> Columns, bool Unique);

    public required IReadOnlyList<string> Tables { get; init; }

    public required IReadOnlyList<Column> Columns { get; init; }

    public required IReadOnlyDictionary<string, IReadOnlyList<string>> PrimaryKeys { get; init; }

    /// <summary>Relationships as (child table, name, parent table); one entry per relationship.</summary>
    public required IReadOnlyList<(string Child, string Name, string Parent)> ForeignKeys { get; init; }

    public required IReadOnlyList<Index> Indexes { get; init; }

    public static AccessProbe Read(OleDbConnection connection)
    {
        var tables = AccessSqliteComparer.AccessUserTables(connection);
        var set = tables.ToHashSet(StringComparer.Ordinal);

        using var cols = connection.GetOleDbSchemaTable(OleDbSchemaGuid.Columns, null)!;
        var columns = cols.Rows.Cast<DataRow>()
            .Where(r => set.Contains((string)r["TABLE_NAME"]))
            .Select(r => new Column(
                (string)r["TABLE_NAME"], (string)r["COLUMN_NAME"],
                Convert.ToInt32(r["DATA_TYPE"], CultureInfo.InvariantCulture),
                Convert.ToInt64(r["ORDINAL_POSITION"], CultureInfo.InvariantCulture)))
            .OrderBy(c => c.Table, StringComparer.Ordinal).ThenBy(c => c.Ordinal)
            .ToList();

        using var pks = connection.GetOleDbSchemaTable(OleDbSchemaGuid.Primary_Keys, null)!;
        var primaryKeys = pks.Rows.Cast<DataRow>()
            .Where(r => set.Contains((string)r["TABLE_NAME"]))
            .GroupBy(r => (string)r["TABLE_NAME"], StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<string>)g.OrderBy(r => Convert.ToInt64(r["ORDINAL"], CultureInfo.InvariantCulture))
                    .Select(r => (string)r["COLUMN_NAME"]).ToList(),
                StringComparer.Ordinal);

        using var fks = connection.GetOleDbSchemaTable(OleDbSchemaGuid.Foreign_Keys, null)!;
        var foreignKeys = fks.Rows.Cast<DataRow>()
            .Where(r => set.Contains((string)r["FK_TABLE_NAME"]) && set.Contains((string)r["PK_TABLE_NAME"]))
            .Select(r => (Child: (string)r["FK_TABLE_NAME"], Name: (string)r["FK_NAME"], Parent: (string)r["PK_TABLE_NAME"]))
            .Distinct()
            .ToList();

        using var idx = connection.GetOleDbSchemaTable(OleDbSchemaGuid.Indexes, null)!;
        var indexes = idx.Rows.Cast<DataRow>()
            .Where(r => set.Contains((string)r["TABLE_NAME"]) && r["INDEX_NAME"] is string && !Convert.ToBoolean(r["PRIMARY_KEY"], CultureInfo.InvariantCulture))
            .GroupBy(r => ((string)r["TABLE_NAME"], (string)r["INDEX_NAME"]))
            .Select(g => new Index(
                g.Key.Item1, g.Key.Item2,
                g.OrderBy(r => Convert.ToInt64(r["ORDINAL_POSITION"], CultureInfo.InvariantCulture)).Select(r => (string)r["COLUMN_NAME"]).ToList(),
                Convert.ToBoolean(g.First()["UNIQUE"], CultureInfo.InvariantCulture)))
            .ToList();

        return new AccessProbe { Tables = tables, Columns = columns, PrimaryKeys = primaryKeys, ForeignKeys = foreignKeys, Indexes = indexes };
    }

    public IReadOnlyList<Column> ColumnsOf(string table) => Columns.Where(c => c.Table == table).ToList();
}
