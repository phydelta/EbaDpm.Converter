using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Core.Validation;

/// <summary>
/// Pairing by BUSINESS KEY between the generated database and any of the reference databases:
/// taxonomy in lower case with <c>_</c> for space, table by <c>mTable.TableCode</c>, ordinate by
/// <c>(AxisOrientation, path of TRIM(OrdinateCode) from the root)</c> over CLOSED axes.
///
/// It never compares IDs between databases: everything that comes out of here is text keys.
/// </summary>
public static class BusinessKeys
{
    public readonly record struct TableKey(string Taxonomy, string TableCode);

    public readonly record struct OrdinateKey(string Taxonomy, string TableCode, string Axis, string PathKey);

    /// <summary>
    /// <c>(normalized taxonomy, TableCode) -> TableID</c>, restricted to the taxonomies in
    /// <paramref name="normalizedTaxonomies"/>. If a physical table is reused across taxonomies
    /// (N:M via <c>mTaxonomyTable</c>), it appears once per taxonomy that uses it; it is never
    /// grouped by <c>TableCode</c> alone (two taxonomies can share a code for different tables,
    /// A-UNQ-12).
    /// </summary>
    public static Dictionary<TableKey, int> BuildTableIdsByBusinessKey(
        SqliteConnection connection, IReadOnlySet<string> normalizedTaxonomies)
    {
        var rows = SqlHelpers.Rows(
            connection,
            """
            SELECT DISTINCT t."TaxonomyCode", tab."TableCode", tab."TableID"
            FROM "mTaxonomyTable" tt
            JOIN "mTaxonomy" t ON t."TaxonomyID" = tt."TaxonomyID"
            JOIN "mTable" tab  ON tab."TableID"  = tt."TableID"
            """,
            3);

        var result = new Dictionary<TableKey, int>();
        foreach (var row in rows)
        {
            var taxonomy = Normalization.TaxonomyCode(row[0]!);
            if (!normalizedTaxonomies.Contains(taxonomy))
            {
                continue;
            }

            var key = new TableKey(taxonomy, row[1]!);
            result.TryAdd(key, int.Parse(row[2]!));
        }

        return result;
    }

    private sealed record OrdinateRow(int OrdinateId, int AxisId, string OrdinateCode, int? ParentOrdinateId);

    /// <summary>
    /// Builds, for the given <paramref name="tableIds"/>, the index
    /// <c>(TableID, AxisOrientation, PathKey) -> OrdinateID</c> over CLOSED axes
    /// (<c>IsOpenAxis = 0</c>) only, with <c>PathKey</c> the path of <c>OrdinateCode</c> from the
    /// root, separated by <c>'/'</c>.
    /// </summary>
    public static Dictionary<(int TableId, string Axis, string PathKey), int> BuildClosedOrdinatePathIndex(
        SqliteConnection connection, IReadOnlySet<int> tableIds)
    {
        if (tableIds.Count == 0)
        {
            return [];
        }

        var tableIdList = string.Join(",", tableIds);
        var rows = SqlHelpers.Rows(
            connection,
            $"""
            SELECT ta."TableID", a."AxisOrientation", o."OrdinateID", o."OrdinateCode", o."ParentOrdinateID"
            FROM "mTableAxis" ta
            JOIN "mAxis" a          ON a."AxisID" = ta."AxisID"
            JOIN "mAxisOrdinate" o  ON o."AxisID"  = a."AxisID"
            WHERE a."IsOpenAxis" = 0 AND ta."TableID" IN ({tableIdList})
            """,
            5);

        var ordinateById = new Dictionary<int, (int TableId, string Axis, OrdinateRow Row)>();
        foreach (var row in rows)
        {
            var tableId = int.Parse(row[0]!);
            var axis = row[1]!;
            var ordinateId = int.Parse(row[2]!);
            var code = Normalization.OrdinateCode(row[3]!);
            var parentId = row[4] is null ? (int?)null : int.Parse(row[4]!);

            ordinateById[ordinateId] = (tableId, axis, new OrdinateRow(ordinateId, tableId, code, parentId));
        }

        var pathCache = new Dictionary<int, string>();

        string ResolvePath(int ordinateId)
        {
            if (pathCache.TryGetValue(ordinateId, out var cached))
            {
                return cached;
            }

            var (_, _, row) = ordinateById[ordinateId];
            var path = row.ParentOrdinateId is { } parentId && ordinateById.ContainsKey(parentId)
                ? ResolvePath(parentId) + "/" + row.OrdinateCode
                : row.OrdinateCode;

            pathCache[ordinateId] = path;
            return path;
        }

        var result = new Dictionary<(int, string, string), int>();
        foreach (var (ordinateId, (tableId, axis, _)) in ordinateById)
        {
            var key = (tableId, axis, ResolvePath(ordinateId));
            result.TryAdd(key, ordinateId);
        }

        return result;
    }

    /// <summary>
    /// Index <c>(TableID, CLOSED position key) -> CellID</c>: the SORTED set of
    /// <c>(AxisOrientation, TRIM(OrdinateCode))</c> of the cell positions over non-open axes. It is
    /// the right key to compare cells against 4.x, where <c>BusinessCode</c> is NOT usable.
    /// </summary>
    public static Dictionary<(int TableId, string PositionKey), int> BuildClosedCellPositionIndex(
        SqliteConnection connection, IReadOnlySet<int> tableIds)
    {
        if (tableIds.Count == 0)
        {
            return [];
        }

        var tableIdList = string.Join(",", tableIds);
        var rows = SqlHelpers.Rows(
            connection,
            $"""
            SELECT c."TableID", c."CellID", a."AxisOrientation", o."OrdinateCode"
            FROM "mTableCell" c
            JOIN "mCellPosition" cp ON cp."CellID" = c."CellID"
            JOIN "mAxisOrdinate" o  ON o."OrdinateID" = cp."OrdinateID"
            JOIN "mAxis" a          ON a."AxisID" = o."AxisID"
            WHERE a."IsOpenAxis" = 0 AND c."TableID" IN ({tableIdList})
            ORDER BY c."CellID"
            """,
            4);

        var positionsByCell = new Dictionary<int, (int TableId, List<string> Positions)>();
        foreach (var row in rows)
        {
            var tableId = int.Parse(row[0]!);
            var cellId = int.Parse(row[1]!);
            var position = row[2]! + ":" + Normalization.OrdinateCode(row[3]!);

            if (!positionsByCell.TryGetValue(cellId, out var entry))
            {
                entry = (tableId, []);
                positionsByCell[cellId] = entry;
            }

            entry.Positions.Add(position);
        }

        var result = new Dictionary<(int, string), int>();
        foreach (var (cellId, (tableId, positions)) in positionsByCell)
        {
            positions.Sort(StringComparer.Ordinal);
            var key = (tableId, string.Join("|", positions));
            result.TryAdd(key, cellId);
        }

        return result;
    }
}
