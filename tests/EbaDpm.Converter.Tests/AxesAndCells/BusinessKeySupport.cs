using EbaDpm.Converter.Tests.Dictionary;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Tests.AxesAndCells;

/// <summary>
/// Matching by BUSINESS KEY between the generated database and any of the reference databases:
/// taxonomy lower-cased with <c>_</c> in place of spaces, table by <c>mTable.TableCode</c> (which
/// already comes from <c>TableVersion.XbrlTableCode</c>, with <c>_</c> in place of spaces), and
/// ordinate by <c>(AxisOrientation, path of TRIM(OrdinateCode) from the root)</c> over CLOSED axes.
///
/// IDs are never compared across databases: everything produced here is a text key.
/// </summary>
internal static class BusinessKeySupport
{
    /// <summary>Taxonomy code lower-cased with <c>_</c> in place of spaces, applied symmetrically on both sides.</summary>
    public static string NormalizeTaxonomyCode(string code) => code.Replace(' ', '_').ToLowerInvariant();

    /// <summary>
    /// Local part of a qualified XBRL code (what comes AFTER the colon), e.g.
    /// <c>eba_dim_3.4:CIF</c> -> <c>CIF</c>. Needed to match dimensions/domains/members between the
    /// generated output (verbatim Access code, WITHOUT a version suffix) and the 4.x references,
    /// which version the namespace prefix per release. Without it, matching on the full
    /// <c>DimensionXBRLCode</c> fails wholesale against 4.0/4.2.
    /// If there is no <c>':'</c>, returns the whole string (it already is the local part).
    /// </summary>
    public static string LocalXbrlPart(string qualifiedCode)
    {
        var colonIndex = qualifiedCode.LastIndexOf(':');
        return colonIndex >= 0 ? qualifiedCode[(colonIndex + 1)..] : qualifiedCode;
    }

    public readonly record struct TableKey(string Taxonomy, string TableCode);

    public readonly record struct OrdinateKey(string Taxonomy, string TableCode, string Axis, string PathKey);

    /// <summary>
    /// <c>(normalised taxonomy, TableCode) -> TableID</c>, restricted to the taxonomies in
    /// <paramref name="normalizedTaxonomies"/>. If a physical table is reused across taxonomies
    /// (N:M via <c>mTaxonomyTable</c>), it appears once per taxonomy that uses it. It is never
    /// grouped by <c>TableCode</c> alone (two taxonomies may share a code for different tables).
    /// </summary>
    public static Dictionary<TableKey, int> BuildTableIdsByBusinessKey(
        SqliteConnection connection, HashSet<string> normalizedTaxonomies)
    {
        var rows = QueryHelpers.Rows(
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
            var taxonomy = NormalizeTaxonomyCode(row[0]!);
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
    /// <c>(TableID, AxisOrientation, PathKey) -> OrdinateID</c> over CLOSED axes only
    /// (<c>IsOpenAxis = 0</c>), where <c>PathKey</c> is the path of <c>OrdinateCode</c> from the
    /// root, separated by <c>'/'</c>. Assumes <c>OrdinateCode</c> carries no padding (no codes with
    /// surrounding spaces in the references or in the generated output).
    /// </summary>
    public static Dictionary<(int TableId, string Axis, string PathKey), int> BuildClosedOrdinatePathIndex(
        SqliteConnection connection, IReadOnlySet<int> tableIds)
    {
        if (tableIds.Count == 0)
        {
            return [];
        }

        var tableIdList = string.Join(",", tableIds);
        var rows = QueryHelpers.Rows(
            connection,
            $"""
            SELECT ta."TableID", a."AxisOrientation", o."OrdinateID", o."OrdinateCode", o."ParentOrdinateID"
            FROM "mTableAxis" ta
            JOIN "mAxis" a          ON a."AxisID" = ta."AxisID"
            JOIN "mAxisOrdinate" o  ON o."AxisID"  = a."AxisID"
            WHERE a."IsOpenAxis" = 0 AND ta."TableID" IN ({tableIdList})
            """,
            5);

        // (TableID, Axis) -> ordinates of that axis, to rebuild the path without SQL recursion.
        var byAxis = new Dictionary<(int TableId, string Axis), List<OrdinateRow>>();
        var ordinateById = new Dictionary<int, (int TableId, string Axis, OrdinateRow Row)>();

        foreach (var row in rows)
        {
            var tableId = int.Parse(row[0]!);
            var axis = row[1]!;
            var ordinateId = int.Parse(row[2]!);
            var code = row[3]!.Trim();
            var parentId = row[4] is null ? (int?)null : int.Parse(row[4]!);

            var record = new OrdinateRow(ordinateId, tableId, code, parentId);
            var groupKey = (tableId, axis);
            if (!byAxis.TryGetValue(groupKey, out var list))
            {
                list = [];
                byAxis[groupKey] = list;
            }

            list.Add(record);
            ordinateById[ordinateId] = (tableId, axis, record);
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

            // (TableID, Axis, PathKey) must be unique: if it is not, fail explicitly instead of
            // silently overwriting.
            if (!result.TryAdd(key, ordinateId))
            {
                throw new InvalidOperationException(
                    $"Duplicate ordinate key (TableID={tableId}, Axis={axis}, Path={key.Item3}): " +
                    $"OrdinateID {result[key]} and {ordinateId}. Path matching is not unique here.");
            }
        }

        return result;
    }

    /// <summary>
    /// Index <c>(TableID, CLOSED position key) -> CellID</c>: the key is the ORDERED set of
    /// <c>(AxisOrientation, TRIM(OrdinateCode))</c> of the cell's positions over non-open axes. It
    /// is the right key to compare cells against 4.x, where <c>BusinessCode</c> does not work: the
    /// open-axis convention differs and the code text differs accordingly.
    /// </summary>
    public static Dictionary<(int TableId, string PositionKey), int> BuildClosedCellPositionIndex(
        SqliteConnection connection, IReadOnlySet<int> tableIds)
    {
        if (tableIds.Count == 0)
        {
            return [];
        }

        var tableIdList = string.Join(",", tableIds);
        var rows = QueryHelpers.Rows(
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
            var position = row[2]! + ":" + row[3]!.Trim();

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
            result.TryAdd(key, cellId); // duplicates (if any) are reported separately
        }

        return result;
    }
}
