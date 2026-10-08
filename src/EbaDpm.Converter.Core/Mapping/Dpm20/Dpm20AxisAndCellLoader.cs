using EbaDpm.Converter.Core.Access.Dpm20;
using EbaDpm.Converter.Core.Sqlite;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Core.Mapping.Dpm20;

/// <summary>
/// Loads the DPM 2.0 axes, ordinates and cells: <c>mAxis</c>, <c>mTableAxis</c>,
/// <c>mAxisOrdinate</c>, <c>mTableCell</c>, <c>mCellPosition</c> and
/// <c>mOrdinateCategorisation</c>. It runs after <see cref="Dpm20StructureLoader.Load"/>, on the
/// same connection: it reuses <c>TableVIdByTableId</c> (the source of each of the 847 pairs that
/// <c>mTable</c> already wrote) and reads <c>mDomain</c>/<c>mMember</c>/<c>mDimension</c> from the
/// destination, which the dictionary loader has already closed.
///
/// This is a NEW pipeline, PARALLEL to the DPM 1.0
/// <see cref="EbaDpm.Converter.Core.Mapping.AxisAndCellLoader"/>: the source is structurally
/// different (<c>Header</c> merges <c>Axis</c> and <c>AxisOrdinate</c>), so the reading and the
/// construction of the tree are redone entirely. What DOES survive from DPM 1.0, literally (the
/// signature algorithm is fed, not re-derived):
/// <list type="bullet">
/// <item><description>
/// <b>The T2 transposition</b>: a key header of direction <c>X</c> becomes an open <c>Y</c> axis
/// with a single ordinate; one of direction <c>Z</c> becomes an open <c>Z</c> axis. There are no
/// key headers of direction <c>Y</c> in the source (0/23,537, measured) — if one appeared, it is
/// an unforeseen case and the conversion fails (it is reported, not forced), just as DPM 1.0
/// failed on an open X axis.
/// </description></item>
/// <item><description>
/// <b>Pruning of abstract headers AFTER the transposition</b>: an abstract header that, after
/// excluding its key children, is left without any surviving non-key child, is NOT emitted. It is
/// a fixed point: it is applied bottom-up until nothing changes (the measured case is
/// single-level, but the algorithm does not assume it).
/// </description></item>
/// <item><description>
/// <b>The cell semantics</b>: <c>mTableCell</c> is the cartesian product AXIS BY AXIS of the
/// ordinates of each CLOSED axis (abstract ones included; open axes do not multiply, they
/// contribute a fixed position to each cell). A cell is shaded if the source does not carry it, or
/// if it carries it with <c>TableVersionCell.IsExcluded</c>.
/// </description></item>
/// </list>
///
/// <b>The source↔destination coordinate mapping</b> (verified empirically):
/// <c>Cell.ColumnID</c>/<c>RowID</c>/<c>SheetID</c> are literally the <c>Header.HeaderID</c> of
/// the X/Y/Z ordinate of that cell IN THE SOURCE — and they ALWAYS reference a NON-KEY header (key
/// headers never have a position of their own in <c>Cell</c>: they are folded into open axes). That
/// is why the lookup "does the source carry this cell?" only needs the combination of ordinates of
/// the CLOSED axes — open ones do not take part in the coordinate.
///
/// <b>IDs.</b> Unlike DPM 1.0, there is no <c>Axis</c>/<c>Ordinate</c>/<c>Cell</c> entity in the
/// source to pair 1:1: <c>AxisID</c>, <c>OrdinateID</c> and <c>CellID</c> are always MINTED, with
/// global counters starting at 1 (no other table writes these three PKs, so no collision is
/// possible). <c>ConceptID</c> stays <see langword="null"/> in everything this class writes (the
/// concept loader mints it later, same pattern as the rest of the DPM 2.0 pipeline).
///
/// <b>The same <c>TableVID</c> can repeat in several of the 847 pairs</b> (58 of 847): the
/// axis/ordinate/cell structure of a <c>TableVID</c> is computed ONCE
/// (<see cref="BuildTablePlan"/>) and INSTANTIATED with fresh IDs for each pair that references it
/// (<see cref="EmitTablePlan"/>) — it avoids recomputing the pruning and the categorisation 58
/// extra times, and guarantees that two pairs of the same <c>TableVID</c> emit the SAME structure.
///
/// <b>The metric dimension (<c>MET</c>, <c>DimensionID = 9999</c>)</b> is written by
/// <see cref="Dpm20DictionaryLoader.Load"/>, not by this class: it is a dictionary row, and it had
/// to exist in <c>mDimension</c> BEFORE <see cref="EmitCategorisation"/> referenced it — leaving it
/// "dangling" without a row of its own, even if the value 9999 is correct, is exactly the
/// inconsistency that must be avoided (every referenced <c>DimensionID</c> must have a row in
/// <c>mDimension</c>). <c>DimensionID = 9999</c> is not a minted counter: it is the value measured,
/// literal and identical, in TWO independent reference databases (4.2 and 4.0). This class only
/// READS it (<see cref="DimensionMemberResolver"/>) and uses it through its local constant
/// <see cref="MetDimensionId"/> when composing <c>DPS</c>.
///
/// <b><c>mTableCell.BusinessCode</c>/<c>DatapointSignature</c>/<c>DPS</c></b>. The algorithm is
/// the same as DPM 1.0 (it is not re-derived): <see cref="ComposeBusinessCode"/> composes
/// <c>{table,Y,X,Z}</c> by ORIENTATION (not by <c>mTableAxis.Order</c> — 100% against 96.54%) and
/// <see cref="ComposeCellSignature"/> composes the closure by <c>ParentOrdinateID</c> with
/// nearest-ancestor inheritance, discarding default members except the metric.
/// <c>BusinessCode</c> is emitted for ALL cells; <c>DPS</c>/<c>DatapointSignature</c> (identical)
/// only for the NON-shaded ones — a shaded one carries <see langword="null"/>, not an empty string.
///
/// <b><c>mOpenAxisValueRestriction</c>, and with it the bracket of the signature.</b>
/// One row per open axis whose key header carries a non-null <c>HeaderVersion.SubCategoryVID</c>:
/// <c>SubCategoryVID → SubCategoryVersion.SubCategoryID → mHierarchy</c> is resolved (this last
/// correspondence, 1:1, comes ALREADY COMPUTED in
/// <see cref="Dpm20DictionaryLoader.Result.HierarchyBySubCategoryId"/> — it is not re-derived by
/// matching on <c>HierarchyCode</c>, which is not a unique key in <c>mHierarchy</c>). With the
/// restriction resolved, <c>AxisID</c> = the emitted open axis, <c>HierarchyID</c> = the hierarchy
/// found, <c>HierarchyStartingMemberID</c> = NULL and <c>IsStartingMemberIncluded</c> = 0 (the
/// DPM 2.0 source does not model a starting member). The SAME restriction feeds the bracket of
/// <see cref="ComposeDps"/>/<see cref="ComposeDms"/> — <c>DIM(*[HierarchyCode])</c> in
/// <c>DPS</c>, <c>DIM(*[HierarchyID])</c> in <c>DimensionMemberSignature</c>/
/// <c>DatapointSignature</c> — exactly as the DPM 1.0 <c>AxisAndCellLoader</c> does with
/// <c>OpenMemberRestriction</c>: the signature algorithm does NOT change, it is only fed.
/// </summary>
public static class Dpm20AxisAndCellLoader
{
    /// <summary>As in DPM 1.0: the sentinel for the "open value" in <c>MemberID</c>.</summary>
    private const int OpenAxisMemberSentinel = 9999;

    /// <summary>
    /// The <c>DimensionID</c> synthesized for the metric dimension (see the XML doc of the class):
    /// a measured value, not minted by a counter.
    /// </summary>
    private const int MetDimensionId = 9999;

    private const int ChunkSize = 2_000;

    /// <summary>Count of rows written, for the CLI report.</summary>
    public sealed record Result(
        int AxisRows,
        int TableAxisRows,
        int AxisOrdinateRows,
        int TableCellRows,
        int CellPositionRows,
        int OrdinateCategorisationRows,
        int OpenAxisValueRestrictionRows,
        int UnresolvedContextPairs,
        int UnresolvedMetricPairs,
        int UnresolvedOpenAxisDimensions,
        int UnresolvedOpenAxisRestrictions,
        IReadOnlyList<string> StructuralAnomalies);

    // ==================================================================
    // Internal working structures
    // ==================================================================

    private sealed record HeaderNode(
        int HeaderId, int? ParentHeaderId, int OriginOrder, bool IsAbstract, string Direction, bool IsKey,
        string? Code, string? Label, int? PropertyId, int? ContextId, int? SubCategoryVId);

    private sealed class SurvivorNode
    {
        public required int HeaderId;
        public required string Code;
        public required string Label;
        public required bool IsAbstract;
        public int Level;
        public int Order;
        public int? ParentHeaderId; // null => root of the SURVIVING tree (may differ from the source if the parent does not survive)
    }

    private sealed class ClosedAxisPlan
    {
        public required string Orientation; // "X" | "Y" | "Z", same as the source (the transposition does not touch closed axes)
        public required List<SurvivorNode> Survivors; // preorder
    }

    private sealed class OpenAxisPlan
    {
        public required string DestOrientation; // "Y" (from a key X) | "Z" (from a key Z)
        public required int HeaderId;
        public required string Code;
        public required string Label;
        public required bool IsAbstract;
        public (int DimensionId, string DimensionXbrlCode, int MemberId, string? MemberXbrlCode)? Categorisation;

        /// <summary>
        /// The hierarchy restriction of THIS open axis, resolved via
        /// <c>HeaderVersion.SubCategoryVID</c> — independent of <see cref="Categorisation"/> (that
        /// one comes from <c>PropertyID</c>): the two chains can, in principle, resolve separately.
        /// <see langword="null"/> if the key header carries no <c>SubCategoryVID</c>, or if it
        /// carries one but the chain does not resolve (counted in
        /// <c>UnresolvedOpenAxisRestrictions</c>, reported and not forced).
        /// </summary>
        public (int HierarchyId, string HierarchyCode)? Restriction;

        /// <summary>
        /// Additional FIXED pairs that the key header itself declares via
        /// <c>HeaderVersion.ContextID</c> — the DPM 2.0 equivalent of the DPM 1.0 <c>'999 '</c>
        /// sentinel which, besides the open pair, carries fixed categorisations. Before this was
        /// handled, the key header's <c>ContextID</c> was read (<see cref="HeaderNode.ContextId"/>)
        /// and never consulted: a dead read — measured in 1/406 current key headers in the 4.2
        /// Access database (0/429 in 4.3; the source itself nulled that <c>ContextID</c> between
        /// releases). Empty in the general case.
        /// </summary>
        public List<(int DimensionId, string DimensionXbrlCode, int MemberId, string? MemberXbrlCode, bool IsDefaultMember)> FixedPairs = [];
    }

    private sealed class TablePlan
    {
        public required int TableVId;
        public required List<ClosedAxisPlan> ClosedAxes;
        public required List<OpenAxisPlan> OpenAxes;

        /// <summary>Own + inherited (closure) categorisation of each CLOSED survivor, by source <c>HeaderID</c>.</summary>
        public required Dictionary<int, List<(int DimensionId, string DimensionXbrlCode, int MemberId, string? MemberXbrlCode)>> CategorisationByHeaderId;

        /// <summary>
        /// OWN categorisation (WITHOUT closure) of each CLOSED survivor, by source <c>HeaderID</c>
        /// — it is step 1 of the signature algorithm and feeds <see cref="ComposeCellSignature"/>,
        /// which does its OWN closure by <c>ParentOrdinateID</c> at CELL level (not at ordinate
        /// level: two ordinates of the same cell, from different axes, can compete for the SAME
        /// dimension, and there the GLOBALLY nearest one wins, not the nearest within each axis
        /// separately — literal algorithm). With <c>IsDefaultMember</c> already resolved (step 4):
        /// the metric carries the real value but is exempt anyway; the open axis is NOT built here
        /// (it has no "own" per header — it is added separately, in
        /// <see cref="EmitTablePlan"/>, with <c>IsDefaultMember = false</c> hardcoded, see the XML
        /// doc of <see cref="DimensionMemberResolver.IsDefaultMemberByMemberId"/>).
        /// </summary>
        public required Dictionary<int, Dictionary<int, (int MemberId, string DimensionXbrlCode, string? MemberXbrlCode, bool IsDefaultMember)>> OwnCategorisationByHeaderId;

        /// <summary>Source coordinate (ColumnID, RowID, SheetID) -> CellID of <c>Cell</c> (<c>TableID</c> entity).</summary>
        public required Dictionary<(int? Column, int? Row, int? Sheet), int> OriginCellIdByCoordinate;

        /// <summary><c>TableVersionCell</c> of THIS table version, by <c>CellID</c>.</summary>
        public required Dictionary<int, bool> IsExcludedByCellId;
    }

    // ==================================================================
    // Entry point
    // ==================================================================

    public static Result Load(
        Dpm20AccessReader reader,
        SqliteConnection destination,
        IReadOnlyDictionary<int, int> tableVIdByTableId,
        IReadOnlyList<Dpm20TableVersionRow> currentTableVersions,
        IReadOnlyDictionary<int, (int HierarchyId, string HierarchyCode, int DomainId)> hierarchyBySubCategoryId)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(tableVIdByTableId);
        ArgumentNullException.ThrowIfNull(currentTableVersions);
        ArgumentNullException.ThrowIfNull(hierarchyBySubCategoryId);

        var structuralAnomalies = new List<string>();

        // The mDimension row for MET (DimensionID=9999) is written by Dpm20DictionaryLoader,
        // together with the rest of the dictionary (it is dictionary, not categorisation, and it
        // must exist BEFORE mOrdinateCategorisation references it). Here it is only resolved by
        // reading.
        var resolver = DimensionMemberResolver.Build(reader, destination, structuralAnomalies);

        // SubCategoryVID -> SubCategoryID (1,246 rows, release cutoff applied in the reader itself).
        // The same SubCategoryVID that already travels in HeaderNode.SubCategoryVId (below, from
        // HeaderVersion) — the missing link up to hierarchyBySubCategoryId.
        var subCategoryIdBySubCategoryVId = reader.ReadSubCategoryVersions()
            .ToDictionary(scv => scv.SubCategoryVId, scv => scv.SubCategoryId);

        // Fallback WITHOUT the release-cutoff filter, same pattern as in
        // Dpm20HierarchyNodeLoader. Measured in 4.3: the source re-versions a subcategory
        // (SubCategoryID=19674) EXACTLY at the cutoff of a new release and does NOT propagate the
        // new SubCategoryVID to all the HeaderVersion rows that used it — of ~90 headers, only 5
        // ended up with the new version. The literal SubCategoryVID of the header stops being
        // current and the strict lookup fails (48 cases in 4.3, 0 in 4.2). It is SAFE by
        // construction: hierarchyBySubCategoryId is indexed by SubCategoryID, which is stable
        // between versions of the SAME subcategory — the old and the new window resolve to the SAME
        // Hierarchy, so the fallback can never change a case that ALREADY resolved through the
        // strict path (4.2 must not move).
        var allSubCategoryIdBySubCategoryVId = reader.ReadAllSubCategoryVersions()
            .ToDictionary(scv => scv.SubCategoryVId, scv => scv.SubCategoryId);

        // mTable.TableCode is already written by Dpm20StructureLoader (same connection, runs
        // earlier — see the XML doc of the class). It is read here, not recomposed: it feeds the
        // first segment of BusinessCode.
        var tableCodeByTableId = new Dictionary<int, string>();
        using (var command = destination.CreateCommand())
        {
            command.CommandText = "SELECT \"TableID\", \"TableCode\" FROM \"mTable\"";
            using var tableCodeReader = command.ExecuteReader();
            while (tableCodeReader.Read())
            {
                tableCodeByTableId[tableCodeReader.GetInt32(0)] = tableCodeReader.GetString(1);
            }
        }

        // DimensionID -> (DomainID, DimensionXBRLCode) and DomainID -> the default member
        // (mMember.IsDefaultMember=1) — already written by the dictionary loader, read here for the
        // same reason as the rest of the resolver: by already persisted CODE/value, not re-derived.
        // They feed the projection RESET (which comes from the ABSENCE of a pair).
        var dimensionInfoById = new Dictionary<int, (int DomainId, string? DimensionXbrlCode)>();
        using (var command = destination.CreateCommand())
        {
            command.CommandText = "SELECT \"DimensionID\", \"DomainID\", \"DimensionXBRLCode\" FROM \"mDimension\"";
            using var dimensionReader = command.ExecuteReader();
            while (dimensionReader.Read())
            {
                dimensionInfoById[dimensionReader.GetInt32(0)] =
                    (dimensionReader.GetInt32(1), dimensionReader.IsDBNull(2) ? null : dimensionReader.GetString(2));
            }
        }

        var defaultMemberByDomainId = new Dictionary<int, (int MemberId, string? MemberXbrlCode)>();
        using (var command = destination.CreateCommand())
        {
            command.CommandText = "SELECT \"DomainID\", \"MemberID\", \"MemberXBRLCode\" FROM \"mMember\" WHERE \"IsDefaultMember\" = 1";
            using var defaultMemberReader = command.ExecuteReader();
            while (defaultMemberReader.Read())
            {
                defaultMemberByDomainId.TryAdd(
                    defaultMemberReader.GetInt32(0),
                    (defaultMemberReader.GetInt32(1), defaultMemberReader.IsDBNull(2) ? null : defaultMemberReader.GetString(2)));
            }
        }

        if (tableVIdByTableId.Count == 0)
        {
            return new Result(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, structuralAnomalies);
        }

        var tableVersionByTableVId = currentTableVersions.ToDictionary(tv => tv.TableVId);
        var distinctTableVIds = tableVIdByTableId.Values.Distinct().OrderBy(id => id).ToList();
        var distinctEntityTableIds = distinctTableVIds
            .Select(vid => ResolveEntityTableId(vid, tableVersionByTableVId))
            .Distinct()
            .ToList();

        // ---- Bulk read, bounded to the selection (not the 59,356/23,537/48,481 total rows) ----
        var tableVersionHeadersByTableVId = reader.ReadTableVersionHeaders(distinctTableVIds)
            .GroupBy(h => h.TableVId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var headersById = reader.ReadHeadersByTableIds(distinctEntityTableIds).ToDictionary(h => h.HeaderId);

        var neededHeaderVIds = tableVersionHeadersByTableVId.Values
            .SelectMany(list => list)
            .Select(h => h.HeaderVId)
            .Distinct();
        var headerVersionsById = reader.ReadHeaderVersionsByIds(neededHeaderVIds).ToDictionary(hv => hv.HeaderVId);

        var cellsByEntityTableId = reader.ReadCellsByTableIds(distinctEntityTableIds)
            .GroupBy(c => c.TableId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var tableVersionCellsByTableVId = reader.ReadTableVersionCellsByTableVIds(distinctTableVIds)
            .GroupBy(c => c.TableVId)
            .ToDictionary(g => g.Key, g => g.ToList());

        // TableVersionCell.VariableVID -> VariableVersion.ContextID — the ALREADY RESOLVED context
        // of each cell's data point, the single source (together with the metric, which is
        // unchanged) of the own categorisation of each CLOSED ordinate. It replaces the former
        // header/table ContextID sources, which are NO LONGER read for this.
        var neededVariableVIds = tableVersionCellsByTableVId.Values
            .SelectMany(list => list)
            .Select(vc => vc.VariableVId)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct();
        var variableVersionByVariableVId = reader.ReadVariableVersionsByVariableVIds(neededVariableVIds)
            .ToDictionary(vv => vv.VariableVId);

        // The header/table ContextID NO LONGER feed the categorisation (it is fully replaced by
        // the variable context), but they are kept in the union in case some other point of the
        // reading needs them — the cost of including them is marginal compared with the VARIABLE
        // ContextID, the new and majority source.
        var neededContextIds = headerVersionsById.Values
            .Select(hv => hv.ContextId)
            .Concat(distinctTableVIds.Select(vid => tableVersionByTableVId[vid].ContextId))
            .Concat(variableVersionByVariableVId.Values.Select(vv => vv.ContextId))
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct();
        var contextCompositionsByContextId = reader.ReadContextCompositionsByContextIds(neededContextIds)
            .GroupBy(c => c.ContextId)
            .ToDictionary(g => g.Key, g => g.ToList());

        // ---- One plan per distinct TableVID (788 with --all): structure + own categorisation,
        // computed ONCE and reused for the pairs (up to 847) that share a TableVID. ----
        var plansByTableVId = new Dictionary<int, TablePlan>(distinctTableVIds.Count);
        foreach (var tableVId in distinctTableVIds)
        {
            var entityTableId = ResolveEntityTableId(tableVId, tableVersionByTableVId);
            var tvhRows = tableVersionHeadersByTableVId.GetValueOrDefault(tableVId, []);

            var nodes = BuildHeaderNodes(tableVId, tvhRows, headersById, headerVersionsById, structuralAnomalies);

            var plan = BuildTablePlan(
                tableVId, entityTableId, nodes, contextCompositionsByContextId, resolver,
                cellsByEntityTableId.GetValueOrDefault(entityTableId, []),
                tableVersionCellsByTableVId.GetValueOrDefault(tableVId, []),
                subCategoryIdBySubCategoryVId, allSubCategoryIdBySubCategoryVId, hierarchyBySubCategoryId,
                variableVersionByVariableVId, defaultMemberByDomainId, dimensionInfoById,
                tableVersionByTableVId[tableVId].ContextId, // level-2 source: the table's ContextID, reused for arbitration.
                structuralAnomalies);

            plansByTableVId[tableVId] = plan;
        }

        // ---- Instantiation per pair (847), with fresh IDs. In memory first (bounded census:
        // 1,991 + 20,705 + 163,206 + 415,562 + ~71,000 rows, hundreds of thousands, not millions —
        // it fits comfortably) and written AFTERWARDS, table by table: a SqliteBatchWriter opens
        // its own transaction and SQLite does not allow two active ones at once on the same
        // connection (unlike DPM 1.0, here a single cell needs rows in three different tables at
        // once — mTableCell, mCellPosition, mOrdinateCategorisation — so writing "on the fly" with
        // several open writers is not feasible). ----
        var counters = new MintingCounters();
        var axisRows = new List<AxisRowData>();
        var tableAxisRows = new List<TableAxisRowData>();
        var ordinateRows = new List<OrdinateRowData>();
        var cellRows = new List<CellRowData>();
        var cellPositionRows = new List<CellPositionRowData>();
        var categorisationRows = new List<CategorisationRowData>();
        var openAxisRestrictionRows = new List<OpenAxisRestrictionRowData>();

        foreach (var (tableId, tableVId) in tableVIdByTableId.OrderBy(kv => kv.Key))
        {
            var plan = plansByTableVId[tableVId];
            var tableCode = tableCodeByTableId.TryGetValue(tableId, out var code)
                ? code
                : throw new InvalidOperationException($"TableID={tableId} has no row in mTable (TableCode) — BusinessCode cannot be composed.");

            EmitTablePlan(
                plan, tableId, tableCode, counters, axisRows, tableAxisRows, ordinateRows, cellRows,
                cellPositionRows, categorisationRows, openAxisRestrictionRows);
        }

        WriteAxisRows(destination, axisRows);
        WriteTableAxisRows(destination, tableAxisRows);
        WriteOrdinateRows(destination, ordinateRows);
        WriteCellRows(destination, cellRows);
        WriteCellPositionRows(destination, cellPositionRows);
        WriteCategorisationRows(destination, categorisationRows);
        WriteOpenAxisValueRestrictionRows(destination, openAxisRestrictionRows);

        return new Result(
            AxisRows: axisRows.Count,
            TableAxisRows: tableAxisRows.Count,
            AxisOrdinateRows: ordinateRows.Count,
            TableCellRows: cellRows.Count,
            CellPositionRows: cellPositionRows.Count,
            OrdinateCategorisationRows: categorisationRows.Count,
            OpenAxisValueRestrictionRows: openAxisRestrictionRows.Count,
            UnresolvedContextPairs: resolver.UnresolvedContextPairs,
            UnresolvedMetricPairs: resolver.UnresolvedMetricPairs,
            UnresolvedOpenAxisDimensions: resolver.UnresolvedOpenAxisDimensions,
            UnresolvedOpenAxisRestrictions: resolver.UnresolvedOpenAxisRestrictions,
            StructuralAnomalies: structuralAnomalies);
    }

    // ------------------------------------------------------------------
    // In-memory rows (see the comment above) and their sequential writing, one
    // SqliteBatchWriter at a time.
    // ------------------------------------------------------------------

    private sealed record AxisRowData(int AxisId, string Orientation, string? Label, bool IsOpenAxis, bool OptionalKey, string? AxisCode);
    private sealed record TableAxisRowData(int AxisId, int TableId, int Order);

    private sealed record OrdinateRowData(
        int AxisId, int OrdinateId, string Label, string Code, bool IsAbstract, bool IsRowKey, int Level, int Order, int? ParentOrdinateId);

    private sealed record CellRowData(int CellId, int TableId, bool IsShaded, string BusinessCode, string? Dps, string? Dms);
    private sealed record CellPositionRowData(int CellId, int OrdinateId);
    private sealed record CategorisationRowData(int OrdinateId, int DimensionId, int MemberId, string Dps, string Dms);

    /// <summary>One row per restricted open axis; <c>HierarchyStartingMemberID</c>/<c>IsStartingMemberIncluded</c> are always NULL/0.</summary>
    private sealed record OpenAxisRestrictionRowData(int AxisId, int HierarchyId);

    private static void WriteAxisRows(SqliteConnection destination, List<AxisRowData> rows)
    {
        using var writer = new SqliteBatchWriter(
            destination, "mAxis", ["AxisID", "AxisOrientation", "AxisLabel", "IsOpenAxis", "OptionalKey", "ConceptID", "AxisCode"]);
        foreach (var row in rows)
        {
            writer.AddRow(row.AxisId, row.Orientation, row.Label, row.IsOpenAxis, row.OptionalKey, null, row.AxisCode);
        }
    }

    private static void WriteTableAxisRows(SqliteConnection destination, List<TableAxisRowData> rows)
    {
        using var writer = new SqliteBatchWriter(destination, "mTableAxis", ["AxisID", "TableID", "Order"]);
        foreach (var row in rows)
        {
            writer.AddRow(row.AxisId, row.TableId, row.Order);
        }
    }

    private static void WriteOrdinateRows(SqliteConnection destination, List<OrdinateRowData> rows)
    {
        using var writer = new SqliteBatchWriter(
            destination,
            "mAxisOrdinate",
            [
                "AxisID", "OrdinateID", "OrdinateLabel", "OrdinateCode", "IsDisplayBeforeChildren",
                "IsAbstractHeader", "IsRowKey", "Level", "Order", "ParentOrdinateID", "ConceptID",
                "TypeOfKey", "RelatedDimensionTableId",
            ]);
        foreach (var row in rows)
        {
            writer.AddRow(
                row.AxisId, row.OrdinateId, row.Label, row.Code,
                false, // IsDisplayBeforeChildren: constant 0
                row.IsAbstract, row.IsRowKey, row.Level, row.Order, row.ParentOrdinateId,
                null, // ConceptID: NULL until the concept loader runs
                null, // TypeOfKey: NULL
                null); // RelatedDimensionTableId: NULL
        }
    }

    private static void WriteCellRows(SqliteConnection destination, List<CellRowData> rows)
    {
        using var writer = new SqliteBatchWriter(
            destination, "mTableCell", ["CellID", "TableID", "IsRowKey", "IsShaded", "BusinessCode", "DatapointSignature", "DPS"]);
        foreach (var row in rows)
        {
            writer.AddRow(
                row.CellId, row.TableId,
                false, // mTableCell.IsRowKey: constant 0
                row.IsShaded,
                row.BusinessCode, // ALL cells, shaded ones included.
                row.Dms, // DatapointSignature = DMS (IDs; differs from DPS only in the bracket of
                         // an open axis WITH a restriction) — NULL in shaded cells.
                row.Dps); // DPS = XBRL codes.
        }
    }

    private static void WriteCellPositionRows(SqliteConnection destination, List<CellPositionRowData> rows)
    {
        using var writer = new SqliteBatchWriter(destination, "mCellPosition", ["CellID", "OrdinateID"]);
        foreach (var row in rows)
        {
            writer.AddRow(row.CellId, row.OrdinateId);
        }
    }

    private static void WriteCategorisationRows(SqliteConnection destination, List<CategorisationRowData> rows)
    {
        using var writer = new SqliteBatchWriter(
            destination,
            "mOrdinateCategorisation",
            ["OrdinateID", "DimensionID", "MemberID", "DimensionMemberSignature", "Source", "DPS"]);
        foreach (var row in rows)
        {
            writer.AddRow(
                row.OrdinateId, row.DimensionId, row.MemberId,
                row.Dms, // DimensionMemberSignature = DMS (differs from DPS only in the bracket of
                         // open-axis pairs WITH a restriction).
                null, // Source: NULL
                row.Dps);
        }
    }

    private static void WriteOpenAxisValueRestrictionRows(SqliteConnection destination, List<OpenAxisRestrictionRowData> rows)
    {
        using var writer = new SqliteBatchWriter(
            destination, "mOpenAxisValueRestriction",
            ["AxisID", "HierarchyID", "HierarchyStartingMemberID", "IsStartingMemberIncluded"]);
        foreach (var row in rows)
        {
            writer.AddRow(
                row.AxisId, row.HierarchyId,
                null, // HierarchyStartingMemberID: always NULL (the source does not model a starting member)
                false); // IsStartingMemberIncluded: 0 (false, NOT null — measured 207/207)
        }
    }

    private static int ResolveEntityTableId(int tableVId, IReadOnlyDictionary<int, Dpm20TableVersionRow> tableVersionByTableVId)
        => tableVersionByTableVId.TryGetValue(tableVId, out var tv)
            ? tv.TableId
            : throw new InvalidOperationException(
                $"TableVID={tableVId} is not in the received list of current TableVersion rows: its " +
                "Table.TableID (entity) cannot be resolved to read Header/Cell.");

    private sealed class MintingCounters
    {
        public int NextAxisId = 1;
        public int NextOrdinateId = 1;
        public int NextCellId = 1;
    }

    // ==================================================================
    // Building the header tree of ONE TableVID
    // ==================================================================

    private static List<HeaderNode> BuildHeaderNodes(
        int tableVId,
        List<Dpm20TableVersionHeaderRow> tvhRows,
        IReadOnlyDictionary<int, Dpm20HeaderRow> headersById,
        IReadOnlyDictionary<int, Dpm20HeaderVersionRow> headerVersionsById,
        List<string> structuralAnomalies)
    {
        var nodes = new List<HeaderNode>(tvhRows.Count);

        foreach (var tvh in tvhRows)
        {
            if (!headersById.TryGetValue(tvh.HeaderId, out var header))
            {
                structuralAnomalies.Add($"TableVID={tableVId}: HeaderID={tvh.HeaderId} has no row in Header.");
                continue;
            }

            if (!headerVersionsById.TryGetValue(tvh.HeaderVId, out var hv))
            {
                structuralAnomalies.Add($"TableVID={tableVId}: HeaderVID={tvh.HeaderVId} (HeaderID={tvh.HeaderId}) has no row in HeaderVersion.");
                continue;
            }

            nodes.Add(new HeaderNode(
                tvh.HeaderId, tvh.ParentHeaderId, tvh.Order, tvh.IsAbstract, header.Direction, header.IsKey,
                hv.Code, hv.Label, hv.PropertyId, hv.ContextId, hv.SubCategoryVId));
        }

        return nodes;
    }

    /// <summary>
    /// Resolves ONE <c>ContextID</c> (source 1 of a header, or source 5 of the table) to its
    /// (DimensionID, MemberID) pairs via <c>ContextComposition</c>: the SAME resolution in both
    /// cases, only where the <c>ContextID</c> comes from changes. Returns empty dictionaries if
    /// <paramref name="contextId"/> is <see langword="null"/> or has no rows in
    /// <c>ContextComposition</c> (it never happens with a real non-null <c>ContextID</c>, but it is
    /// the same defensive check the original code already had).
    /// </summary>
    private static (
        Dictionary<int, (int MemberId, string DimensionXbrlCode, string? MemberXbrlCode)> Own,
        Dictionary<int, (int MemberId, string DimensionXbrlCode, string? MemberXbrlCode, bool IsDefaultMember)> OwnWithDefaultFlag)
        ResolveContextOwnPairs(
            int? contextId,
            IReadOnlyDictionary<int, List<Dpm20ContextCompositionRow>> contextCompositionsByContextId,
            DimensionMemberResolver resolver)
    {
        var own = new Dictionary<int, (int, string, string?)>();
        var ownWithDefaultFlag = new Dictionary<int, (int, string, string?, bool)>();

        if (contextId is { } cid && contextCompositionsByContextId.TryGetValue(cid, out var pairs))
        {
            foreach (var pair in pairs)
            {
                var itemDomainId = resolver.ResolveItemDomainId(pair.ItemId);
                var resolved = resolver.ResolveDimensionForProperty(pair.PropertyId, itemDomainId);
                if (resolved is not { } r)
                {
                    resolver.UnresolvedContextPairs++;
                    continue;
                }

                var member = resolver.ResolveMemberByDomainAndItem(r.DomainId, pair.ItemId);
                if (member is not { } m)
                {
                    resolver.UnresolvedContextPairs++;
                    continue;
                }

                own[r.DimensionId] = (m.MemberId, r.DimensionXbrlCode, m.MemberXbrlCode);
                ownWithDefaultFlag[r.DimensionId] = (
                    m.MemberId, r.DimensionXbrlCode, m.MemberXbrlCode,
                    resolver.IsDefaultMemberByMemberId.GetValueOrDefault(m.MemberId));
            }
        }

        return (own, ownWithDefaultFlag);
    }

    /// <summary>
    /// Cartesian product of the CLOSED axes at <c>HeaderID</c> level (before minting
    /// <c>OrdinateID</c>) — the SAME combinatorics as the grid of <see cref="EmitTablePlan"/>,
    /// needed here in <see cref="BuildTablePlan"/> to project each cell's variable context onto the
    /// ordinates BEFORE fresh IDs exist (the categorisation is computed ONCE per
    /// <c>TableVID</c>). Deliberately a NEW function, not a reuse of the local
    /// <c>CartesianProduct</c> of <see cref="EmitTablePlan"/> (that one works with already minted
    /// <c>OrdinateID</c>): same logic, two different levels, zero risk of coupling the PLAN and
    /// EMIT phases.
    /// </summary>
    private static IEnumerable<List<(ClosedAxisPlan Axis, SurvivorNode Node)>> CartesianProductOfClosedAxes(
        IReadOnlyList<ClosedAxisPlan> closedAxes)
    {
        if (closedAxes.Count == 0)
        {
            yield break;
        }

        var indices = new int[closedAxes.Count];
        while (true)
        {
            var combo = new List<(ClosedAxisPlan, SurvivorNode)>(closedAxes.Count);
            for (var i = 0; i < closedAxes.Count; i++)
            {
                combo.Add((closedAxes[i], closedAxes[i].Survivors[indices[i]]));
            }

            yield return combo;

            var axisIndex = closedAxes.Count - 1;
            while (axisIndex >= 0)
            {
                indices[axisIndex]++;
                if (indices[axisIndex] < closedAxes[axisIndex].Survivors.Count)
                {
                    break;
                }

                indices[axisIndex] = 0;
                axisIndex--;
            }

            if (axisIndex < 0)
            {
                yield break;
            }
        }
    }

    /// <summary>
    /// Derives the OWN categorisation of each CLOSED ordinate (leaf or abstract, treated exactly
    /// the same: each has its OWN row of cells in the source) by projecting
    /// <c>TableVersionCell.VariableVID → VariableVersion.ContextID</c> — the ALREADY RESOLVED
    /// context of the data point — onto the ordinates that its cells touch.
    ///
    /// It replaces ENTIRELY the former sources 1 (<c>HeaderVersion.ContextID</c>) and 5
    /// (<c>TableVersion.ContextID</c>): both were a per-HEADER approximation of what this brings
    /// already resolved per CELL. Source 3 (metric) is not touched — it is added separately, in
    /// <see cref="BuildTablePlan"/>, after this projection.
    ///
    /// Algorithm, per ordinate O and dimension D of the table's universe (the set of
    /// <c>DimensionID</c> — without <c>MET</c> — that appears in ANY variable context of the
    /// table): if ALL the own cells of O (not shaded, with a resolved variable) agree on a member
    /// for D, that pair; if NONE carries it, a RESET — the default member of D's domain (it comes
    /// from the ABSENCE: 0 of 487,399 variable-context pairs carry a default member — it is not an
    /// empirical rule); if they vary, nothing (another ordinate/axis decides it). There is no need
    /// to merge with the tree parent: the variable already carries the COMPLETE context of its data
    /// point, so a LEAF ordinate recovers, directly from its own cells, everything an ancestor
    /// could have contributed — the effective closure == the own rows holds by CONSTRUCTION, not by
    /// an additional merge. An ABSTRACT ordinate keeps what its own cells determine (usually less):
    /// it is the "delta against the parent" form measured on the reference (109,538 rows).
    ///
    /// An ordinate WITHOUT any own cell with a resolved context (in practice, all its cells
    /// shaded/excluded/without variable) has nothing to project from and used to be left with only
    /// its metric — a measured defect: 286 ordinates, 675 pairs that the Access database, the
    /// reference AND the layouts all have. Source 1 is then used (<c>HeaderVersion.ContextID</c> of
    /// THAT header, via <see cref="ResolveContextOwnPairs"/>), the same one that the projection
    /// replaced for the general case but which is still what Access declares per header. Strictly
    /// gated to "zero resolved own cells": it does not change a single row of any ordinate that did
    /// have something to project (the general form is untouched).
    /// </summary>
    private static (
        Dictionary<int, Dictionary<int, (int MemberId, string DimensionXbrlCode, string? MemberXbrlCode)>> OwnByHeaderId,
        Dictionary<int, Dictionary<int, (int MemberId, string DimensionXbrlCode, string? MemberXbrlCode, bool IsDefaultMember)>> OwnWithDefaultFlagByHeaderId)
        ProjectVariableContextOntoOrdinates(
            List<ClosedAxisPlan> closedAxes,
            IReadOnlyDictionary<(int? Column, int? Row, int? Sheet), int> originCellIdByCoordinate,
            IReadOnlyDictionary<int, Dpm20TableVersionCellRow> versionCellByCellId,
            IReadOnlyDictionary<int, Dpm20VariableVersionRow> variableVersionByVariableVId,
            IReadOnlyDictionary<int, List<Dpm20ContextCompositionRow>> contextCompositionsByContextId,
            DimensionMemberResolver resolver,
            IReadOnlyDictionary<int, (int MemberId, string? MemberXbrlCode)> defaultMemberByDomainId,
            IReadOnlyDictionary<int, (int DomainId, string? DimensionXbrlCode)> dimensionInfoById,
            IReadOnlyDictionary<int, HeaderNode> nodesByHeaderId)
    {
        // Step 1: for each combination of the grid (a potential cell), resolve the context of its
        // variable — or null if it is shaded/excluded/without variable/without context — and
        // accumulate it under EACH HeaderID that the combination touches (each axis contributes ONE
        // position to the cell, abstract or leaf).
        var contextsByHeaderId = new Dictionary<int, List<Dictionary<int, (int MemberId, string DimensionXbrlCode, string? MemberXbrlCode)>>>();
        var universeDimensionIds = new HashSet<int>();

        foreach (var combo in CartesianProductOfClosedAxes(closedAxes))
        {
            int? columnHeaderId = null, rowHeaderId = null, sheetHeaderId = null;
            foreach (var (axis, node) in combo)
            {
                switch (axis.Orientation)
                {
                    case "X": columnHeaderId = node.HeaderId; break;
                    case "Y": rowHeaderId = node.HeaderId; break;
                    case "Z": sheetHeaderId = node.HeaderId; break;
                }
            }

            Dictionary<int, (int MemberId, string DimensionXbrlCode, string? MemberXbrlCode)>? resolvedContext = null;
            if (originCellIdByCoordinate.TryGetValue((columnHeaderId, rowHeaderId, sheetHeaderId), out var originCellId)
                && versionCellByCellId.TryGetValue(originCellId, out var versionCell)
                && !versionCell.IsExcluded
                && versionCell.VariableVId is { } variableVId
                && variableVersionByVariableVId.TryGetValue(variableVId, out var variableVersion))
            {
                var (own, _) = ResolveContextOwnPairs(variableVersion.ContextId, contextCompositionsByContextId, resolver);
                if (own.Count > 0)
                {
                    resolvedContext = own;
                }
            }

            if (resolvedContext is not null)
            {
                foreach (var dimensionId in resolvedContext.Keys)
                {
                    if (dimensionId != MetDimensionId)
                    {
                        universeDimensionIds.Add(dimensionId);
                    }
                }
            }

            foreach (var (_, node) in combo)
            {
                if (!contextsByHeaderId.TryGetValue(node.HeaderId, out var list))
                {
                    list = [];
                    contextsByHeaderId[node.HeaderId] = list;
                }

                if (resolvedContext is not null)
                {
                    list.Add(resolvedContext);
                }
            }
        }

        // Step 2: per HeaderID, decide each dimension of the universe (agreement / reset / mixed).
        var ownByHeaderId = new Dictionary<int, Dictionary<int, (int, string, string?)>>();
        var ownWithDefaultFlagByHeaderId = new Dictionary<int, Dictionary<int, (int, string, string?, bool)>>();

        foreach (var (headerId, relevant) in contextsByHeaderId)
        {
            var own = new Dictionary<int, (int, string, string?)>();
            var ownWithDefaultFlag = new Dictionary<int, (int, string, string?, bool)>();

            if (relevant.Count > 0)
            {
                foreach (var dimensionId in universeDimensionIds)
                {
                    var withValue = relevant.Where(r => r.ContainsKey(dimensionId)).ToList();
                    if (withValue.Count == relevant.Count)
                    {
                        var distinctMembers = withValue.Select(r => r[dimensionId].MemberId).Distinct().ToList();
                        if (distinctMembers.Count == 1)
                        {
                            var pair = withValue[0][dimensionId];
                            own[dimensionId] = pair;
                            ownWithDefaultFlag[dimensionId] = (
                                pair.MemberId, pair.DimensionXbrlCode, pair.MemberXbrlCode,
                                resolver.IsDefaultMemberByMemberId.GetValueOrDefault(pair.MemberId));
                        }

                        // count>1: the cells DO all carry the dimension but DISAGREE — it should
                        // not happen within the same ordinate (left without a pair, not forced;
                        // the residue, if any, is measured in the --validate report).
                    }
                    else if (withValue.Count == 0
                        && dimensionInfoById.TryGetValue(dimensionId, out var dimInfo)
                        && defaultMemberByDomainId.TryGetValue(dimInfo.DomainId, out var def))
                    {
                        // The reset comes from the ABSENCE — no own cell of this ordinate carries
                        // the dimension in its variable context.
                        var xbrlCode = dimInfo.DimensionXbrlCode ?? string.Empty;
                        own[dimensionId] = (def.MemberId, xbrlCode, def.MemberXbrlCode);
                        ownWithDefaultFlag[dimensionId] = (def.MemberId, xbrlCode, def.MemberXbrlCode, true);
                    }

                    // else: mixed (some do, some do not) -> nothing, another axis/ordinate decides it.
                }
            }
            else
            {
                // Ordinate WITHOUT ANY own cell with a resolved context (in practice, all shaded/
                // excluded/without variable, so the projection has nothing to project from). The
                // tree does not always save this (it may be a root, or its whole ancestor chain
                // may be just as empty, with 0 non-shaded cells): source 1 is used —
                // HeaderVersion.ContextID of THIS header, the same one that the projection replaced
                // for the general case but which Access DOES declare per header (88.63 % of the
                // headers with a parent repeat ALL their properties) — so as not to leave the
                // ordinate with only its metric. Gated EXACTLY to relevant.Count==0: no ordinate
                // with at least one resolved own cell passes through here, so it cannot move a
                // single row of those already projected (the general closure is untouched).
                (own, ownWithDefaultFlag) = ResolveContextOwnPairs(
                    nodesByHeaderId[headerId].ContextId, contextCompositionsByContextId, resolver);
            }

            ownByHeaderId[headerId] = own;
            ownWithDefaultFlagByHeaderId[headerId] = ownWithDefaultFlag;
        }

        return (ownByHeaderId, ownWithDefaultFlagByHeaderId);
    }

    // ==================================================================
    // ONE DIMENSION, ONE AXIS — the arbitration of (table, dimension) pairs on more than one axis
    // that ProjectVariableContextOntoOrdinates can duplicate by accidental coincidence.
    // ==================================================================

    /// <summary>
    /// The set of <c>DimensionID</c> that ONE <c>ContextID</c> declares via
    /// <c>ContextComposition.PropertyID</c> — EXISTENCE only, without resolving the member (the
    /// arbitration decides the AXIS, not the value). Deliberately WITHOUT the counter
    /// <see cref="DimensionMemberResolver.UnresolvedContextPairs"/>: that one counts failures of the
    /// real categorisation (<see cref="ResolveContextOwnPairs"/>, VARIABLE context); this is a
    /// different arbitration mechanism, over the HEADER/TABLE context, and counting it there would
    /// mix two things that <see cref="Result"/> reports separately.
    /// </summary>
    private static HashSet<int> ResolveDeclaredDimensionIds(
        int? contextId,
        IReadOnlyDictionary<int, List<Dpm20ContextCompositionRow>> contextCompositionsByContextId,
        DimensionMemberResolver resolver)
    {
        var result = new HashSet<int>();
        if (contextId is not { } cid || !contextCompositionsByContextId.TryGetValue(cid, out var pairs))
        {
            return result;
        }

        foreach (var pair in pairs)
        {
            var itemDomainId = resolver.ResolveItemDomainId(pair.ItemId);
            var resolved = resolver.ResolveDimensionForProperty(pair.PropertyId, itemDomainId);
            if (resolved is { } r)
            {
                result.Add(r.DimensionId);
            }
        }

        return result;
    }

    /// <summary>
    /// The two levels of the dimension-axis arbitration rule, per <c>TableVID</c>:
    /// <list type="bullet">
    /// <item><description>
    /// <b>Level 1</b> — the <c>Header.Direction</c> of ANY header (key or not, of any direction:
    /// <paramref name="nodes"/> carries ALL those of this table version, with the release cutoff
    /// already applied upstream in <c>ReadTableVersions</c>/<c>ReadTableVersionHeaders</c> — the
    /// cutoff is NOT cosmetic) whose <c>HeaderVersion.ContextID</c> declares the dimension.
    /// Measured 100% in agreement with the reference where it decides (6,805/6,805). If more than
    /// one direction declared the SAME dimension (0 measured with the release cutoff applied), it
    /// does not decide — it falls to level 2/3 of the caller.
    /// </description></item>
    /// <item><description>
    /// <b>Level 2</b> — if no header decides and the dimension is in
    /// <c>TableVersion.ContextID</c> (<paramref name="tableContextId"/>), the owner axis is
    /// <c>X</c>: empirical (63/63 measured on release 4.2 and one exporter — not a property of the
    /// format), NOT derived from which direction carries the table context.
    /// </description></item>
    /// </list>
    /// What neither level decides (0 measured cases) is left unresolved: the caller
    /// (<see cref="ArbitrateDimensionAxisConflicts"/>) decides what to do with it (level 3: it is
    /// not touched, it is counted).
    /// </summary>
    private static Dictionary<int, string> DetermineDimensionOwnerAxes(
        List<HeaderNode> nodes,
        int? tableContextId,
        IReadOnlyDictionary<int, List<Dpm20ContextCompositionRow>> contextCompositionsByContextId,
        DimensionMemberResolver resolver)
    {
        var directionsByDimension = new Dictionary<int, HashSet<string>>();
        foreach (var node in nodes)
        {
            foreach (var dimensionId in ResolveDeclaredDimensionIds(node.ContextId, contextCompositionsByContextId, resolver))
            {
                if (!directionsByDimension.TryGetValue(dimensionId, out var set))
                {
                    set = [];
                    directionsByDimension[dimensionId] = set;
                }

                set.Add(node.Direction);
            }
        }

        var owner = new Dictionary<int, string>();
        foreach (var (dimensionId, directions) in directionsByDimension)
        {
            if (directions.Count == 1)
            {
                owner[dimensionId] = directions.Single();
            }

            // directions.Count > 1: 0 measured with the release cutoff — not decided here, it
            // falls to level 3 of the caller (not forced).
        }

        if (tableContextId is not null)
        {
            foreach (var dimensionId in ResolveDeclaredDimensionIds(tableContextId, contextCompositionsByContextId, resolver))
            {
                owner.TryAdd(dimensionId, "X"); // Level 2: empirical, 63/63.
            }
        }

        return owner;
    }

    /// <summary>
    /// Applies the arbitration ONLY to the (dimension) pairs that the projection of
    /// <see cref="ProjectVariableContextOntoOrdinates"/> left on MORE THAN ONE CLOSED axis of this
    /// table — "the rule decides between conflicting axes, it does not give permission to exist":
    /// a pair already on a single axis is NOT touched, even if Access does not declare it (1,159
    /// measured cases, 0 wrongly assigned).
    ///
    /// It is applied to <paramref name="ownByHeaderId"/>/<paramref name="ownWithDefaultFlagByHeaderId"/>
    /// (the OWN categorisation, before the closure by <c>ParentHeaderId</c> done by
    /// <c>ResolveEffective</c> further down in <see cref="BuildTablePlan"/>): the closure only
    /// propagates WITHIN the tree of a single axis, so a conflict between axes visible in the
    /// closure was already visible here, at the "own" level — fixing it here is enough and avoids
    /// duplicating the detection logic after the closure.
    /// </summary>
    private static void ArbitrateDimensionAxisConflicts(
        int tableVId,
        List<ClosedAxisPlan> closedAxes,
        Dictionary<int, Dictionary<int, (int MemberId, string DimensionXbrlCode, string? MemberXbrlCode)>> ownByHeaderId,
        Dictionary<int, Dictionary<int, (int MemberId, string DimensionXbrlCode, string? MemberXbrlCode, bool IsDefaultMember)>> ownWithDefaultFlagByHeaderId,
        List<HeaderNode> nodes,
        int? tableContextId,
        IReadOnlyDictionary<int, List<Dpm20ContextCompositionRow>> contextCompositionsByContextId,
        DimensionMemberResolver resolver,
        List<string> structuralAnomalies)
    {
        if (closedAxes.Count < 2)
        {
            return; // a single closed axis: there is no other axis to compete with.
        }

        var orientationByHeaderId = new Dictionary<int, string>();
        foreach (var axis in closedAxes)
        {
            foreach (var survivor in axis.Survivors)
            {
                orientationByHeaderId[survivor.HeaderId] = axis.Orientation;
            }
        }

        // Which orientations each dimension touches, looking ONLY at the own projection (before
        // MET, before the closure).
        var orientationsByDimension = new Dictionary<int, HashSet<string>>();
        foreach (var (headerId, own) in ownByHeaderId)
        {
            if (!orientationByHeaderId.TryGetValue(headerId, out var orientation))
            {
                continue; // defensive: every HeaderID in ownByHeaderId should come from closedAxes.
            }

            foreach (var dimensionId in own.Keys)
            {
                if (!orientationsByDimension.TryGetValue(dimensionId, out var set))
                {
                    set = [];
                    orientationsByDimension[dimensionId] = set;
                }

                set.Add(orientation);
            }
        }

        var conflicting = orientationsByDimension.Where(kv => kv.Value.Count > 1).Select(kv => kv.Key).ToList();
        if (conflicting.Count == 0)
        {
            return;
        }

        var ownerAxisByDimension = DetermineDimensionOwnerAxes(nodes, tableContextId, contextCompositionsByContextId, resolver);

        foreach (var dimensionId in conflicting)
        {
            if (!ownerAxisByDimension.TryGetValue(dimensionId, out var ownerOrientation))
            {
                // Level 3 (0 cases measured today): neither Access nor TableVersion.ContextID
                // decides a single axis — no criterion is invented; it is left as is and counted
                // for the conversion report.
                var orientations = string.Join(",", orientationsByDimension[dimensionId].OrderBy(o => o, StringComparer.Ordinal));
                structuralAnomalies.Add(
                    $"TableVID={tableVId}: DimensionID={dimensionId} on more than one axis ({orientations}) and " +
                    "neither Access (Header.Direction) nor TableVersion.ContextID declares a single owner axis " +
                    "(arbitration level 3) — left unarbitrated.");
                continue;
            }

            foreach (var (headerId, orientation) in orientationByHeaderId)
            {
                if (orientation == ownerOrientation)
                {
                    continue;
                }

                if (ownByHeaderId.TryGetValue(headerId, out var own))
                {
                    own.Remove(dimensionId);
                }

                if (ownWithDefaultFlagByHeaderId.TryGetValue(headerId, out var ownWithFlag))
                {
                    ownWithFlag.Remove(dimensionId);
                }
            }
        }
    }

    // ==================================================================
    // The plan of ONE table: pruned closed axes, open axes, categorisation.
    // ==================================================================

    private static TablePlan BuildTablePlan(
        int tableVId,
        int entityTableId,
        List<HeaderNode> nodes,
        IReadOnlyDictionary<int, List<Dpm20ContextCompositionRow>> contextCompositionsByContextId,
        DimensionMemberResolver resolver,
        List<Dpm20CellRow> cellsOfEntity,
        List<Dpm20TableVersionCellRow> versionCellsOfTable,
        IReadOnlyDictionary<int, int> subCategoryIdBySubCategoryVId,
        IReadOnlyDictionary<int, int> allSubCategoryIdBySubCategoryVId,
        IReadOnlyDictionary<int, (int HierarchyId, string HierarchyCode, int DomainId)> hierarchyBySubCategoryId,
        IReadOnlyDictionary<int, Dpm20VariableVersionRow> variableVersionByVariableVId,
        IReadOnlyDictionary<int, (int MemberId, string? MemberXbrlCode)> defaultMemberByDomainId,
        IReadOnlyDictionary<int, (int DomainId, string? DimensionXbrlCode)> dimensionInfoById,
        int? tableContextId,
        List<string> structuralAnomalies)
    {
        var nodesByHeaderId = nodes.ToDictionary(n => n.HeaderId);
        var byDirection = nodes.GroupBy(n => n.Direction).ToDictionary(g => g.Key, g => g.ToList());

        var closedAxes = new List<ClosedAxisPlan>();
        var openAxes = new List<OpenAxisPlan>();

        foreach (var direction in new[] { "X", "Y", "Z" })
        {
            if (!byDirection.TryGetValue(direction, out var headersOfDirection))
            {
                continue;
            }

            var nonKey = headersOfDirection.Where(h => !h.IsKey).ToList();
            var key = headersOfDirection.Where(h => h.IsKey).ToList();

            if (nonKey.Count > 0)
            {
                var survivors = BuildPrunedSurvivorTree(tableVId, direction, nonKey, structuralAnomalies);
                if (survivors.Count > 0)
                {
                    closedAxes.Add(new ClosedAxisPlan { Orientation = direction, Survivors = survivors });
                }
            }

            foreach (var keyHeader in key.OrderBy(h => h.HeaderId))
            {
                var destOrientation = direction switch
                {
                    "X" => "Y", // transposition: key X -> open Y
                    "Z" => "Z", // transposition: key Z -> open Z
                    _ => null, // key Y: not contemplated (0 measured in the whole source)
                };

                if (destOrientation is null)
                {
                    structuralAnomalies.Add(
                        $"TableVID={tableVId}: key header HeaderID={keyHeader.HeaderId} of direction " +
                        $"'{direction}' — the transposition does not contemplate this direction " +
                        "(0 key headers of direction Y measured in the whole source). Skipped.");
                    continue;
                }

                var code = (keyHeader.Code ?? string.Empty).Trim();
                var label = (keyHeader.Label ?? string.Empty).Trim();

                (int, string, int, string?)? categorisation = null;
                if (keyHeader.PropertyId is { } keyPropertyId)
                {
                    var resolved = resolver.ResolveDimensionForProperty(keyPropertyId, targetDomainId: null);
                    if (resolved is { } r)
                    {
                        categorisation = (r.DimensionId, r.DimensionXbrlCode, OpenAxisMemberSentinel, null);
                    }
                    else
                    {
                        resolver.UnresolvedOpenAxisDimensions++;
                    }
                }
                else
                {
                    resolver.UnresolvedOpenAxisDimensions++;
                }

                // The key header itself can carry, IN ADDITION to the PropertyID that opens the
                // dimension, a HeaderVersion.ContextID with FIXED pairs — the DPM 2.0 equivalent
                // of the DPM 1.0 sentinel ordinate '999 ' which, next to the open pair, carries
                // fixed categorisations. Before this was handled, HeaderNode.ContextId was read
                // and never consulted here: a dead read. Measured on the 4.2 Access database (929
                // current TableVersion rows): 1 of 406 key headers carries it — TableVID=6965
                // (C_08.05), HeaderID=964, ContextID=759675, two pairs (APR=x66, EXC=qx4) — and 0
                // of 429 in 4.3 (the source itself nulled that ContextID between releases). It is
                // resolved with the SAME path as any other header ContextID
                // (ResolveContextOwnPairs, already used for the VARIABLE context of a closed
                // ordinate): same algorithm, another ContextID source.
                var fixedPairs = new List<(int DimensionId, string DimensionXbrlCode, int MemberId, string? MemberXbrlCode, bool IsDefaultMember)>();
                if (keyHeader.ContextId is { } keyContextId)
                {
                    var (_, ownPairsWithDefaultFlag) = ResolveContextOwnPairs(keyContextId, contextCompositionsByContextId, resolver);
                    var openDimensionId = categorisation?.Item1;

                    foreach (var (dimensionId, value) in ownPairsWithDefaultFlag)
                    {
                        if (dimensionId == openDimensionId)
                        {
                            // Not measured in 4.2/4.3 (the only real case, C_08.05, does not
                            // collide): the case is named and the fixed pair is discarded instead
                            // of silently overwriting the open pair, which is what decides "open"
                            // versus "semi-open".
                            structuralAnomalies.Add(
                                $"TableVID={tableVId}: key header HeaderID={keyHeader.HeaderId} " +
                                $"(ContextID={keyContextId}) declares a FIXED pair for its SAME " +
                                $"DimensionID={dimensionId} already used by the open pair: " +
                                "the fixed one is discarded, the open one wins.");
                            continue;
                        }

                        fixedPairs.Add((dimensionId, value.DimensionXbrlCode, value.MemberId, value.MemberXbrlCode, value.IsDefaultMember));
                    }

                    if (fixedPairs.Count > 0)
                    {
                        var pairsText = string.Join(", ", fixedPairs.Select(p => $"{p.DimensionXbrlCode}({p.MemberXbrlCode})"));
                        structuralAnomalies.Add(
                            $"TableVID={tableVId}: key header HeaderID={keyHeader.HeaderId} " +
                            $"(ContextID={keyContextId}) contributes {fixedPairs.Count} FIXED pair(s) in addition " +
                            $"to the open pair, via HeaderVersion.ContextID: {pairsText}.");
                    }
                }

                // SubCategoryVID -> SubCategoryVersion.SubCategoryID -> mHierarchy.
                // Independent of categorisation (that one comes from PropertyID): an axis can
                // resolve one without the other, although in the measured 4.2 source it does not
                // happen.
                (int HierarchyId, string HierarchyCode)? restriction = null;
                if (keyHeader.SubCategoryVId is { } subCategoryVId)
                {
                    var resolvedByCurrentVersion = subCategoryIdBySubCategoryVId.TryGetValue(subCategoryVId, out var subCategoryId);

                    // The literal SubCategoryVID may have stopped being current because the source
                    // re-versioned the subcategory without propagating the change to this header
                    // (measured, see the comment of allSubCategoryIdBySubCategoryVId above).
                    // Fallback WITHOUT the release-cutoff filter, safe by construction:
                    // hierarchyBySubCategoryId is indexed by the stable SubCategoryID, so it
                    // resolves to the SAME Hierarchy as the current version. It is NOT an error, it
                    // is a legitimate recovery — but it must leave a trace, naming the case,
                    // instead of being fixed silently.
                    var usedFallback = false;
                    if (!resolvedByCurrentVersion)
                    {
                        usedFallback = allSubCategoryIdBySubCategoryVId.TryGetValue(subCategoryVId, out subCategoryId);
                    }

                    if ((resolvedByCurrentVersion || usedFallback)
                        && hierarchyBySubCategoryId.TryGetValue(subCategoryId, out var hierarchy))
                    {
                        restriction = (hierarchy.HierarchyId, hierarchy.HierarchyCode);
                        if (usedFallback)
                        {
                            structuralAnomalies.Add(
                                $"TableVID={tableVId}: key header HeaderID={keyHeader.HeaderId} (SubCategoryVID="
                                + $"{subCategoryVId}) resolved its open-axis restriction by FALLBACK to "
                                + $"SubCategoryID={subCategoryId} — its SubCategoryVID is no longer current at the "
                                + "release cutoff, the source did not propagate the re-versioning to this header.");
                        }
                    }
                    else
                    {
                        resolver.UnresolvedOpenAxisRestrictions++;
                    }
                }

                openAxes.Add(new OpenAxisPlan
                {
                    DestOrientation = destOrientation,
                    HeaderId = keyHeader.HeaderId,
                    Code = code,
                    Label = label,
                    IsAbstract = keyHeader.IsAbstract,
                    Categorisation = categorisation,
                    Restriction = restriction,
                    FixedPairs = fixedPairs,
                });
            }
        }

        if (closedAxes.All(a => a.Orientation != "X"))
        {
            structuralAnomalies.Add(
                $"TableVID={tableVId}: no closed X axis (no non-key header of direction X " +
                "survives the pruning) — broken invariant (846/846 measured with an X axis always present).");
        }

        // ---- The source grid: coordinate (ColumnID,RowID,SheetID) -> CellID, and its
        // TableVersionCell row — needed NOW, before the categorisation: the context of each
        // cell's VARIABLE is projected onto the ordinates it touches. ----
        var originCellIdByCoordinate = new Dictionary<(int?, int?, int?), int>();
        foreach (var cell in cellsOfEntity)
        {
            originCellIdByCoordinate.TryAdd((cell.ColumnId, cell.RowId, cell.SheetId), cell.CellId);
        }

        var versionCellByCellId = new Dictionary<int, Dpm20TableVersionCellRow>();
        var isExcludedByCellId = new Dictionary<int, bool>();
        foreach (var vc in versionCellsOfTable)
        {
            versionCellByCellId[vc.CellId] = vc;
            isExcludedByCellId[vc.CellId] = vc.IsExcluded;
        }

        // ---- Own categorisation: PROJECTION of the context of each cell's VARIABLE onto the
        // CLOSED ordinates that touch it. It replaces ENTIRELY the former sources 1
        // (HeaderVersion.ContextID) and 5 (TableVersion.ContextID): both were a per-HEADER
        // approximation of what TableVersionCell.VariableVID -> VariableVersion.ContextID carries
        // ALREADY RESOLVED per CELL. Source 3 (metric) does NOT change: the property does not
        // travel in ContextComposition, it is added separately, below.
        //
        // There is no need to merge with the tree parent (the effective closure == the own rows
        // holds by CONSTRUCTION, not by merging — see the XML doc of
        // ProjectVariableContextOntoOrdinates): the DIRECT PROJECTION is written as the own
        // categorisation of each ordinate, leaf or abstract.
        var (ownByHeaderId, ownWithDefaultFlagByHeaderId) = ProjectVariableContextOntoOrdinates(
            closedAxes, originCellIdByCoordinate, versionCellByCellId, variableVersionByVariableVId,
            contextCompositionsByContextId, resolver, defaultMemberByDomainId, dimensionInfoById,
            nodesByHeaderId);

        // ONE DIMENSION, ONE AXIS. The projection above decides per ordinate, on each axis
        // INDEPENDENTLY ("all the own cells of this ordinate agree on a member for D") — an
        // ACCIDENTAL coincidence (a dimension constant across the whole table, or the shading
        // pattern leaving the live cells of a column in rows with the same member) duplicates the
        // (table, dimension) pair on more than one axis. It is arbitrated HERE, over the "own"
        // projection (ownByHeaderId/ownWithDefaultFlagByHeaderId) and BEFORE source 3 (MET) and the
        // closure (ResolveEffective, further down): the closure only propagates WITHIN one axis
        // (by ParentHeaderId, always the same tree), so fixing it here is enough — there is no
        // need to repeat the arbitration after the closure.
        ArbitrateDimensionAxisConflicts(
            tableVId, closedAxes, ownByHeaderId, ownWithDefaultFlagByHeaderId, nodes, tableContextId,
            contextCompositionsByContextId, resolver, structuralAnomalies);

        // Source 3: the metric — non-key header with a PropertyID.
        // It is never inherited; it is added AFTER the projection, in case an ordinate had no entry.
        foreach (var axis in closedAxes)
        {
            foreach (var survivor in axis.Survivors)
            {
                var node = nodesByHeaderId[survivor.HeaderId];
                if (node.IsKey || node.PropertyId is not { } propertyId)
                {
                    continue;
                }

                var member = resolver.ResolveMetMember(propertyId);
                if (member is not { } m)
                {
                    resolver.UnresolvedMetricPairs++;
                    continue;
                }

                if (!ownByHeaderId.TryGetValue(survivor.HeaderId, out var own))
                {
                    own = new Dictionary<int, (int, string, string?)>();
                    ownByHeaderId[survivor.HeaderId] = own;
                }

                if (!ownWithDefaultFlagByHeaderId.TryGetValue(survivor.HeaderId, out var ownWithDefaultFlag))
                {
                    ownWithDefaultFlag = new Dictionary<int, (int, string, string?, bool)>();
                    ownWithDefaultFlagByHeaderId[survivor.HeaderId] = ownWithDefaultFlag;
                }

                own[MetDimensionId] = (m.MemberId, "MET", m.MemberXbrlCode);
                // Real IsDefaultMember, even though the metric is ALWAYS exempt from the filter
                // ("the metric is never discarded"): informative, it does not decide.
                ownWithDefaultFlag[MetDimensionId] = (
                    m.MemberId, "MET", m.MemberXbrlCode,
                    resolver.IsDefaultMemberByMemberId.GetValueOrDefault(m.MemberId));
            }
        }

        // TableVersion.ContextID declares pairs that hold for the WHOLE table (what Access
        // declares is exported), and up to here it was only read as an AXIS ARBITER (arbitration
        // level 2, DetermineDimensionOwnerAxes) — never as a SOURCE of pairs. It is filled HERE,
        // on ownByHeaderId — before the closure below — so that the pair takes part in the same
        // ParentHeaderID inheritance as any other source: it is added to ALL the CLOSED ordinates
        // of the axis that OWNS that dimension (level 1 if a header already decides it, otherwise
        // level 2 — the table's own ContextID, empirical) that do NOT ALREADY have an OWN value for
        // that dimension. `TryAdd` is the containment condition: 0 ordinates with a live cell are
        // affected — those that already carry it correctly, through the cell projection or the
        // header fallback, are not touched. Deliberately <c>ownWithDefaultFlagByHeaderId</c>/
        // <c>OwnCategorisationByHeaderId</c> (the source of ComposeCellSignature, i.e. the
        // CELL DPS) is NOT touched: the 36 ordinates this fix reaches are COMPLETELY shaded (0 of
        // 1,620 live cells) — there is no cell to resolve there, and touching that other branch
        // would be a change with no measurable effect that risks moving the DPS of live cells of
        // OTHER ordinates through an unexpected ancestor merge.
        var (tableContextOwnPairs, _) = ResolveContextOwnPairs(tableContextId, contextCompositionsByContextId, resolver);
        if (tableContextOwnPairs.Count > 0)
        {
            var tableContextOwnerAxisByDimension = DetermineDimensionOwnerAxes(nodes, tableContextId, contextCompositionsByContextId, resolver);
            foreach (var (dimensionId, pair) in tableContextOwnPairs)
            {
                if (!tableContextOwnerAxisByDimension.TryGetValue(dimensionId, out var ownerOrientation))
                {
                    continue; // Level 3: neither a header nor the table context decides a single axis — nothing is invented. 0 measured cases.
                }

                foreach (var axis in closedAxes)
                {
                    if (axis.Orientation != ownerOrientation)
                    {
                        continue;
                    }

                    foreach (var survivor in axis.Survivors)
                    {
                        if (!ownByHeaderId.TryGetValue(survivor.HeaderId, out var own))
                        {
                            own = new Dictionary<int, (int, string, string?)>();
                            ownByHeaderId[survivor.HeaderId] = own;
                        }

                        own.TryAdd(dimensionId, pair);
                    }
                }
            }
        }

        // mOrdinateCategorisation: check A-OCA-01 requires the EFFECTIVE closure (climbing through
        // ParentOrdinateID) to match the OWN SET of every NON-abstract ordinate. The projection
        // alone does NOT guarantee it: an ABSTRACT ordinate can have own cells INDEPENDENT of those
        // of its descendants (a subtotal with its own VariableVID — the source does NOT aggregate
        // the children's cells into the parent's), so a dimension that only the ANCESTOR determines
        // (from ITS OWN cells) is not recovered by projecting ONLY the leaf's cells. That is why it
        // is merged downwards (source 2, ParentHeaderID): the ordinate's own value wins over the
        // inherited one, and what is WRITTEN is that already resolved closure — the same guarantee
        // by construction the pipeline had before the projection, now with the projection as the
        // origin of the "own" instead of the header/table ContextID.
        // <c>OwnCategorisationByHeaderId</c> (below, for ComposeCellSignature) is NOT merged here:
        // that closure is done by ComposeCellSignature itself at CELL level.
        var effectiveCache = new Dictionary<int, Dictionary<int, (int MemberId, string DimensionXbrlCode, string? MemberXbrlCode)>>();
        var parentByHeaderId = closedAxes
            .SelectMany(a => a.Survivors)
            .ToDictionary(s => s.HeaderId, s => s.ParentHeaderId);

        Dictionary<int, (int MemberId, string DimensionXbrlCode, string? MemberXbrlCode)> ResolveEffective(int headerId)
        {
            if (effectiveCache.TryGetValue(headerId, out var cached))
            {
                return cached;
            }

            var own = ownByHeaderId.GetValueOrDefault(headerId, []);
            Dictionary<int, (int, string, string?)> effective;

            if (parentByHeaderId.TryGetValue(headerId, out var parentId) && parentId is { } pid)
            {
                var parentEffective = ResolveEffective(pid);
                effective = new Dictionary<int, (int, string, string?)>(parentEffective);
                foreach (var (dimensionId, value) in own)
                {
                    effective[dimensionId] = value; // the ordinate's own value wins (the nearest one)
                }
            }
            else
            {
                effective = new Dictionary<int, (int, string, string?)>(own);
            }

            effectiveCache[headerId] = effective;
            return effective;
        }

        var categorisationByHeaderId = new Dictionary<int, List<(int, string, int, string?)>>();
        foreach (var headerId in closedAxes.SelectMany(a => a.Survivors).Select(s => s.HeaderId))
        {
            var effective = ResolveEffective(headerId);
            categorisationByHeaderId[headerId] = effective
                .Select(kv => (kv.Key, kv.Value.DimensionXbrlCode, kv.Value.MemberId, kv.Value.MemberXbrlCode))
                .ToList();
        }

        return new TablePlan
        {
            TableVId = tableVId,
            ClosedAxes = closedAxes,
            OpenAxes = openAxes,
            CategorisationByHeaderId = categorisationByHeaderId,
            OwnCategorisationByHeaderId = ownWithDefaultFlagByHeaderId,
            OriginCellIdByCoordinate = originCellIdByCoordinate,
            IsExcludedByCellId = isExcludedByCellId,
        };
    }

    /// <summary>
    /// Builds the tree of ONE direction with only the NON-KEY headers and applies the pruning of
    /// abstract headers AFTER the exclusion of keys (bottom-up fixed point): an abstract header
    /// with no surviving child is not emitted. <c>Level</c>/<c>Order</c> are recomputed over the
    /// PRUNED tree (preorder, siblings by source <c>Order</c>).
    /// </summary>
    private static List<SurvivorNode> BuildPrunedSurvivorTree(
        int tableVId, string direction, List<HeaderNode> nonKeyHeaders, List<string> structuralAnomalies)
    {
        var byId = nonKeyHeaders.ToDictionary(h => h.HeaderId);

        // If the parent of a NON-key header is a KEY header, it is an unforeseen reparenting
        // (never measured) — it is named and treated as a root, the whole conversion is not
        // aborted because of one table.
        foreach (var node in nonKeyHeaders)
        {
            if (node.ParentHeaderId is { } pid && !byId.ContainsKey(pid))
            {
                structuralAnomalies.Add(
                    $"TableVID={tableVId}, axis {direction}: HeaderID={node.HeaderId} has ParentHeaderID=" +
                    $"{pid}, which is not a NON-key header of the same direction (probably a key parent); " +
                    "it is treated as a root.");
            }
        }

        var childrenByParent = nonKeyHeaders
            .Where(h => h.ParentHeaderId.HasValue && byId.ContainsKey(h.ParentHeaderId!.Value))
            .GroupBy(h => h.ParentHeaderId!.Value)
            .ToDictionary(g => g.Key, g => g.OrderBy(h => h.OriginOrder).ToList());

        var roots = nonKeyHeaders
            .Where(h => h.ParentHeaderId is null || !byId.ContainsKey(h.ParentHeaderId!.Value))
            .OrderBy(h => h.OriginOrder)
            .ToList();

        // Fixed point: a node survives if it is NOT abstract, or if it is abstract and AT LEAST one
        // of its children (already evaluated, postorder) survives.
        var survives = new Dictionary<int, bool>();

        bool Survives(HeaderNode node)
        {
            if (survives.TryGetValue(node.HeaderId, out var cached))
            {
                return cached;
            }

            // Do NOT use Any(Survives): it short-circuits on the first child that survives and
            // leaves the following siblings unmemoized (and later not visited coherently). Each
            // child is always evaluated, without short-circuit.
            var kids = childrenByParent.GetValueOrDefault(node.HeaderId, []);
            var anyChildSurvives = false;
            foreach (var kid in kids)
            {
                if (Survives(kid))
                {
                    anyChildSurvives = true;
                }
            }

            var result = !node.IsAbstract || anyChildSurvives;
            survives[node.HeaderId] = result;
            return result;
        }

        foreach (var root in roots)
        {
            Survives(root);
        }

        var result = new List<SurvivorNode>();
        var nextOrder = 1;

        void Visit(HeaderNode node, int? survivingParentHeaderId, int level)
        {
            if (!survives[node.HeaderId])
            {
                return;
            }

            var code = (node.Code ?? string.Empty).Trim();
            var label = (node.Label ?? string.Empty).Trim();

            result.Add(new SurvivorNode
            {
                HeaderId = node.HeaderId,
                Code = code,
                Label = label,
                IsAbstract = node.IsAbstract,
                Level = level,
                Order = nextOrder++,
                ParentHeaderId = survivingParentHeaderId,
            });

            foreach (var kid in childrenByParent.GetValueOrDefault(node.HeaderId, []))
            {
                Visit(kid, node.HeaderId, level + 1);
            }
        }

        foreach (var root in roots)
        {
            Visit(root, null, 1);
        }

        return result;
    }

    // ==================================================================
    // Instantiation of ONE plan under ONE concrete TableID (pair), with fresh IDs.
    // ==================================================================

    private static void EmitTablePlan(
        TablePlan plan,
        int tableId,
        string tableCode,
        MintingCounters counters,
        List<AxisRowData> axisRows,
        List<TableAxisRowData> tableAxisRows,
        List<OrdinateRowData> ordinateRows,
        List<CellRowData> cellRows,
        List<CellPositionRowData> cellPositionRows,
        List<CategorisationRowData> categorisationRows,
        List<OpenAxisRestrictionRowData> openAxisRestrictionRows)
    {
        // ---- What ComposeBusinessCode/ComposeCellSignature need per OrdinateID, accumulated while
        // the ordinates of THIS instantiation are minted (below). ----
        var ordinateCodeByOrdinateId = new Dictionary<int, string>();
        var orientationRankAndAxisIdByOrdinateId = new Dictionary<int, (int Rank, int AxisId)>();
        var ownCategorisationsByOrdinateId = new Dictionary<int, List<(int DimensionId, string DimensionXbrlCode, int MemberId, string? MemberXbrlCode, bool IsDefaultMember, (int HierarchyId, string HierarchyCode)? Restriction)>>();
        var parentOrdinateIdByOrdinateId = new Dictionary<int, int?>();

        // ---- Order of mTableAxis (adapted — there is no open X in DPM 2.0): closed X < Y < Z < open Y < open Z ----
        var orderedClosed = plan.ClosedAxes
            .OrderBy(a => a.Orientation switch { "X" => 0, "Y" => 1, "Z" => 2, _ => 3 })
            .ToList();
        var orderedOpen = plan.OpenAxes
            .OrderBy(a => a.DestOrientation == "Y" ? 0 : 1)
            .ThenBy(a => a.HeaderId)
            .ToList();

        var axisOrder = 1;
        var axisIdByOpenHeaderId = new Dictionary<int, int>();
        var ordinateIdByOpenHeaderId = new Dictionary<int, int>();
        var closedAxisEmissions = new List<(ClosedAxisPlan Plan, int AxisId, Dictionary<int, int> OrdinateIdByHeaderId)>();

        foreach (var axis in orderedClosed)
        {
            var axisId = counters.NextAxisId++;
            var label = axis.Orientation switch { "X" => "Columns", "Y" => "Rows", "Z" => "Sheets", _ => axis.Orientation };

            axisRows.Add(new AxisRowData(axisId, axis.Orientation, label, false, false, null));
            tableAxisRows.Add(new TableAxisRowData(axisId, tableId, axisOrder++));

            var ordinateIdByHeaderId = new Dictionary<int, int>(axis.Survivors.Count);
            foreach (var survivor in axis.Survivors)
            {
                var ordinateId = counters.NextOrdinateId++;
                ordinateIdByHeaderId[survivor.HeaderId] = ordinateId;

                var parentOrdinateId = survivor.ParentHeaderId is { } pid ? ordinateIdByHeaderId.GetValueOrDefault(pid) : (int?)null;
                if (survivor.ParentHeaderId is { } && parentOrdinateId == 0)
                {
                    parentOrdinateId = null; // parent not seen yet (should not happen: preorder guarantees the parent first) — defensive
                }

                ordinateRows.Add(new OrdinateRowData(
                    axisId, ordinateId, survivor.Label, survivor.Code, survivor.IsAbstract,
                    false, // IsRowKey: only the ordinates of an open axis (below)
                    survivor.Level, survivor.Order, parentOrdinateId));

                EmitCategorisation(categorisationRows, ordinateId, plan.CategorisationByHeaderId.GetValueOrDefault(survivor.HeaderId));

                // Code for BusinessCode, orientation+AxisID for its order (Y,X,Z), and OWN
                // categorisation (without closure) + parent for ComposeCellSignature.
                ordinateCodeByOrdinateId[ordinateId] = survivor.Code;
                orientationRankAndAxisIdByOrdinateId[ordinateId] = (OrientationRank(axis.Orientation), axisId);
                parentOrdinateIdByOrdinateId[ordinateId] = parentOrdinateId;
                ownCategorisationsByOrdinateId[ordinateId] = plan.OwnCategorisationByHeaderId.TryGetValue(survivor.HeaderId, out var ownDict)
                    ? ownDict.Select(kv => (kv.Key, kv.Value.DimensionXbrlCode, kv.Value.MemberId, kv.Value.MemberXbrlCode, kv.Value.IsDefaultMember, ((int HierarchyId, string HierarchyCode)?)null)).ToList()
                    : []; // pairs of a CLOSED axis never carry a hierarchy restriction (only the open one does)
            }

            closedAxisEmissions.Add((axis, axisId, ordinateIdByHeaderId));
        }

        foreach (var openAxis in orderedOpen)
        {
            var axisId = counters.NextAxisId++;
            axisRows.Add(new AxisRowData(axisId, openAxis.DestOrientation, openAxis.Label, true, true, openAxis.Code));
            tableAxisRows.Add(new TableAxisRowData(axisId, tableId, axisOrder++));

            var ordinateId = counters.NextOrdinateId++;
            ordinateRows.Add(new OrdinateRowData(
                axisId, ordinateId, openAxis.Label, openAxis.Code, openAxis.IsAbstract,
                true, // IsRowKey: 1 in the only ordinate of the open axis (389/389 measured)
                0, // Level: 0 in the ordinates of an open axis
                0, null)); // Order: 0, not 1 (check A-AXE-10 — 0 violations in the reference, all 389 with 0)

            if (openAxis.Categorisation is { } c)
            {
                EmitCategorisation(
                    categorisationRows, ordinateId,
                    [(c.DimensionId, c.DimensionXbrlCode, c.MemberId, c.MemberXbrlCode)],
                    openAxis.Restriction);
            }

            // The FIXED pairs of the key header's own ContextID (see the XML doc of
            // OpenAxisPlan.FixedPairs) — WITHOUT restriction: the hierarchy restriction belongs to
            // the OPEN PAIR, and ComposeDps/ComposeDms only apply it when MemberID is the sentinel
            // (never the case of a fixed pair), so passing it here would be inert but confusing —
            // null is passed on purpose, not by omission.
            if (openAxis.FixedPairs.Count > 0)
            {
                EmitCategorisation(
                    categorisationRows, ordinateId,
                    openAxis.FixedPairs
                        .Select(p => (p.DimensionId, p.DimensionXbrlCode, p.MemberId, p.MemberXbrlCode))
                        .ToList(),
                    restriction: null);
            }

            // One row per emitted open axis whose key header carried a resolved SubCategoryVID —
            // independent of whether Categorisation resolved (only AxisID + HierarchyID are
            // required).
            if (openAxis.Restriction is { } restriction)
            {
                openAxisRestrictionRows.Add(new OpenAxisRestrictionRowData(axisId, restriction.HierarchyId));
            }

            axisIdByOpenHeaderId[openAxis.HeaderId] = axisId;
            ordinateIdByOpenHeaderId[openAxis.HeaderId] = ordinateId;

            // Same accumulation as for closed ordinates. The open-axis ordinate has no parent
            // (Level=0, a single level) and its pair — if it exists — is NEVER "default"
            // (sentinel MemberID, not a real member of mMember; see the XML doc of
            // DimensionMemberResolver.IsDefaultMemberByMemberId, same criterion as DPM 1.0).
            ordinateCodeByOrdinateId[ordinateId] = openAxis.Code;
            orientationRankAndAxisIdByOrdinateId[ordinateId] = (OrientationRank(openAxis.DestOrientation), axisId);
            parentOrdinateIdByOrdinateId[ordinateId] = null;
            // The open pair (if it resolved) PLUS the fixed pairs of the key header's own ContextID
            // (normally none, see the XML doc of OpenAxisPlan.FixedPairs) — both feed
            // ComposeCellSignature like any other own categorisation of an ordinate: a fixed pair IS
            // discarded if it is the default member of its domain (real IsDefaultMember), the open
            // pair never is (sentinel MemberID, not a real member — see the comment above).
            var ownPairs = new List<(int DimensionId, string DimensionXbrlCode, int MemberId, string? MemberXbrlCode, bool IsDefaultMember, (int HierarchyId, string HierarchyCode)? Restriction)>();
            if (openAxis.Categorisation is { } cat)
            {
                ownPairs.Add((cat.DimensionId, cat.DimensionXbrlCode, cat.MemberId, cat.MemberXbrlCode, false, openAxis.Restriction));
            }

            foreach (var fixedPair in openAxis.FixedPairs)
            {
                ownPairs.Add((fixedPair.DimensionId, fixedPair.DimensionXbrlCode, fixedPair.MemberId, fixedPair.MemberXbrlCode, fixedPair.IsDefaultMember, null));
            }

            ownCategorisationsByOrdinateId[ordinateId] = ownPairs;
        }

        // ---- The grid: cartesian product AXIS BY AXIS of the CLOSED axes. Open axes contribute a
        // fixed position (their only ordinate) to ALL cells, without multiplying. ----
        var fixedOpenOrdinateIds = ordinateIdByOpenHeaderId.Values.ToList();

        IEnumerable<List<(ClosedAxisPlan Axis, SurvivorNode Node)>> CartesianProduct()
        {
            if (closedAxisEmissions.Count == 0)
            {
                yield break;
            }

            var indices = new int[closedAxisEmissions.Count];
            while (true)
            {
                var combo = new List<(ClosedAxisPlan, SurvivorNode)>(closedAxisEmissions.Count);
                for (var i = 0; i < closedAxisEmissions.Count; i++)
                {
                    combo.Add((closedAxisEmissions[i].Plan, closedAxisEmissions[i].Plan.Survivors[indices[i]]));
                }

                yield return combo;

                var axisIndex = closedAxisEmissions.Count - 1;
                while (axisIndex >= 0)
                {
                    indices[axisIndex]++;
                    if (indices[axisIndex] < closedAxisEmissions[axisIndex].Plan.Survivors.Count)
                    {
                        break;
                    }

                    indices[axisIndex] = 0;
                    axisIndex--;
                }

                if (axisIndex < 0)
                {
                    yield break;
                }
            }
        }

        foreach (var combo in CartesianProduct())
        {
            int? columnHeaderId = null, rowHeaderId = null, sheetHeaderId = null;
            var positionsForThisCell = new List<int>(closedAxisEmissions.Count + fixedOpenOrdinateIds.Count);

            foreach (var (axis, node) in combo)
            {
                var emission = closedAxisEmissions.First(e => ReferenceEquals(e.Plan, axis));
                positionsForThisCell.Add(emission.OrdinateIdByHeaderId[node.HeaderId]);

                switch (axis.Orientation)
                {
                    case "X": columnHeaderId = node.HeaderId; break;
                    case "Y": rowHeaderId = node.HeaderId; break;
                    case "Z": sheetHeaderId = node.HeaderId; break;
                }
            }

            positionsForThisCell.AddRange(fixedOpenOrdinateIds);

            var isShaded = true;
            if (plan.OriginCellIdByCoordinate.TryGetValue((columnHeaderId, rowHeaderId, sheetHeaderId), out var originCellId)
                && plan.IsExcludedByCellId.TryGetValue(originCellId, out var isExcluded))
            {
                isShaded = isExcluded;
            }

            var cellId = counters.NextCellId++;

            // BusinessCode for ALL cells, order Y,X,Z (by orientation, NOT by mTableAxis.Order —
            // 100% against 96.54%, measured). DPS/DatapointSignature only if NOT shaded; shaded ->
            // NULL, not an empty string.
            var businessCode = ComposeBusinessCode(tableCode, positionsForThisCell, orientationRankAndAxisIdByOrdinateId, ordinateCodeByOrdinateId);
            string? dps = null;
            string? dms = null;
            if (!isShaded)
            {
                (dps, dms) = ComposeCellSignature(positionsForThisCell, ownCategorisationsByOrdinateId, parentOrdinateIdByOrdinateId);
            }

            cellRows.Add(new CellRowData(cellId, tableId, isShaded, businessCode, dps, dms));

            foreach (var ordinateId in positionsForThisCell)
            {
                cellPositionRows.Add(new CellPositionRowData(cellId, ordinateId));
            }
        }
    }

    /// <summary>Y=0, X=1, Z=2 (the usual row, column, sheet convention).</summary>
    private static int OrientationRank(string orientation) => orientation switch { "Y" => 0, "X" => 1, "Z" => 2, _ => 3 };

    /// <summary>
    /// <c>mTableCell.BusinessCode</c>: <c>{table,ordinates in order Y,X,Z}</c>. The PRIMARY order
    /// is the orientation (100% measured): there are no two ordinates of the same cell with
    /// different orientations to break a tie.
    ///
    /// The SECONDARY order — between several ordinates of the SAME orientation in the SAME cell,
    /// which only occurs with several OPEN axes of that orientation (several key X headers of the
    /// same TableVID, transposed to Y; it never coexists with a CLOSED axis of that same
    /// orientation, 0 measured cases) — has NO rule derivable from a single source column:
    /// <list type="bullet">
    /// <item><description>
    /// It is NOT <c>mTableAxis.Order</c> (measured: it gives the OPPOSITE order to the reference in
    /// <c>K_50.00</c>).
    /// </description></item>
    /// <item><description>
    /// It is NOT the <c>HeaderID</c>/<c>TableVersionHeader.Order</c> of the source key header, in
    /// either direction: in <c>K_50.00</c> (2 axes) descending DOES reproduce the reference, but in
    /// <c>K_43.00.a</c> (3 axes, codes <c>0010</c>/<c>0020</c>/<c>0040</c> with source
    /// <c>Order</c> 1020/1030/1040) the reference gives <c>0040,0010,0020</c> — neither ascending
    /// nor descending by either field.
    /// </description></item>
    /// </list>
    /// Measured over the 144,463 (table, BusinessCode) keys of the 4.2 reference: ascending by own
    /// <c>AxisID</c> matches 142,488 (98.63%); descending, 143,280 (99.18%). NEITHER closes at
    /// 100%, and — the proof that there is no rule slipping away — they are NOT a subset of each
    /// other: 927 keys that ascending gets right are lost by descending, and descending wins 1,719
    /// different ones that ascending did not have. That is the sign that the secondary order is NOT
    /// by <c>AxisID</c> in any direction: the reference uses a convention of its own, not derivable
    /// from the source with the available fields.
    ///
    /// That is why DESCENDING is a DECLARED CHOICE, not a derived rule — it is documented as a
    /// divergence from the reference, with the two measured figures, not as "the variant that
    /// matches most". The residue (~1,183 of 144,463 BusinessCode, 0.8%) stays named, not forced.
    /// </summary>
    private static string ComposeBusinessCode(
        string tableCode,
        List<int> ordinateIdsOfCell,
        IReadOnlyDictionary<int, (int Rank, int AxisId)> orientationRankAndAxisIdByOrdinateId,
        IReadOnlyDictionary<int, string> ordinateCodeByOrdinateId)
    {
        var codes = ordinateIdsOfCell
            .OrderBy(id => orientationRankAndAxisIdByOrdinateId[id].Rank)
            .ThenByDescending(id => orientationRankAndAxisIdByOrdinateId[id].AxisId)
            .Select(id => ordinateCodeByOrdinateId[id]);

        return "{" + tableCode + "," + string.Join(",", codes) + "}";
    }

    /// <summary>Maximum depth when climbing through <c>ParentOrdinateID</c> composing a signature — the same defensive guard as DPM 1.0 (possible cycle: aborts with a clear message, not silently).</summary>
    private const int MaxOrdinateTreeDepth = 64;

    /// <summary>
    /// <c>mTableCell.DPS</c>/<c>DatapointSignature</c> (the same algorithm as DPM 1.0, not
    /// re-derived): for each ordinate of the cell, it climbs through
    /// <paramref name="parentOrdinateIdByOrdinateId"/> accumulating depth; for each
    /// <c>DimensionID</c>, across ALL the ordinates of the cell at once (not axis by axis), the
    /// pair seen at the LOWEST depth wins. Default members are discarded except the metric (which
    /// always survives, whether or not it is marked default — in 4.0/4.2 ALL its members are).
    /// It composes <c>MET(...)</c> first and the rest ORDINALLY ordered by the dimension's XBRL
    /// code, joined by <c>|</c>, reusing <see cref="ComposeDps"/>/<see cref="ComposeDms"/> — the
    /// same pair-composition rule already used to write <c>mOrdinateCategorisation</c>.
    /// Each winning pair carries its own <c>Restriction</c> (inherited from the ordinate that
    /// contributed it), so <c>Dps</c> and <c>Dms</c> can differ in the bracket of an open axis
    /// WITH a restriction — both are returned, never one derived from the other.
    /// </summary>
    private static (string? Dps, string? Dms) ComposeCellSignature(
        List<int> ordinateIdsOfCell,
        IReadOnlyDictionary<int, List<(int DimensionId, string DimensionXbrlCode, int MemberId, string? MemberXbrlCode, bool IsDefaultMember, (int HierarchyId, string HierarchyCode)? Restriction)>> ownCategorisationsByOrdinateId,
        IReadOnlyDictionary<int, int?> parentOrdinateIdByOrdinateId)
    {
        var chosen = new Dictionary<int, (int Depth, (int DimensionId, string DimensionXbrlCode, int MemberId, string? MemberXbrlCode, bool IsDefaultMember, (int HierarchyId, string HierarchyCode)? Restriction) Pair)>();

        foreach (var start in ordinateIdsOfCell)
        {
            var depth = 0;
            int? current = start;
            var steps = 0;

            while (current is { } currentId)
            {
                if (++steps > MaxOrdinateTreeDepth)
                {
                    throw new InvalidOperationException(
                        $"The ordinate tree starting at OrdinateID={start} exceeds the maximum " +
                        $"expected depth ({MaxOrdinateTreeDepth}): possible cycle, unforeseen anomaly.");
                }

                if (ownCategorisationsByOrdinateId.TryGetValue(currentId, out var pairs))
                {
                    foreach (var pair in pairs)
                    {
                        if (!chosen.TryGetValue(pair.DimensionId, out var existing) || depth < existing.Depth)
                        {
                            chosen[pair.DimensionId] = (depth, pair);
                        }
                    }
                }

                current = parentOrdinateIdByOrdinateId.TryGetValue(currentId, out var parent) ? parent : null;
                depth++;
            }
        }

        // Pairs whose member is the default of its domain are discarded, except the metric, which
        // is never discarded.
        var alive = chosen.Values
            .Where(v => v.Pair.DimensionId == MetDimensionId || !v.Pair.IsDefaultMember)
            .ToList();

        var orderedDps = new List<string>();
        var orderedDms = new List<string>();
        var metricPair = alive.Where(v => v.Pair.DimensionId == MetDimensionId).Select(v => v.Pair).ToList();
        if (metricPair.Count > 0)
        {
            var m = metricPair[0];
            // The metric never carries a bracket (the open-axis sentinel is NEVER the metric
            // dimension, and ComposeDps/ComposeDms resolve it the same way without looking at
            // Restriction), so Dps and Dms of the metric pair are literally the same text.
            orderedDps.Add(ComposeDps(m.DimensionId, m.DimensionXbrlCode, m.MemberId, m.MemberXbrlCode, m.Restriction));
            orderedDms.Add(ComposeDms(m.DimensionId, m.DimensionXbrlCode, m.MemberId, m.MemberXbrlCode, m.Restriction));
        }

        foreach (var v in alive
            .Where(v => v.Pair.DimensionId != MetDimensionId)
            .OrderBy(v => v.Pair.DimensionXbrlCode, StringComparer.Ordinal))
        {
            orderedDps.Add(ComposeDps(v.Pair.DimensionId, v.Pair.DimensionXbrlCode, v.Pair.MemberId, v.Pair.MemberXbrlCode, v.Pair.Restriction));
            orderedDms.Add(ComposeDms(v.Pair.DimensionId, v.Pair.DimensionXbrlCode, v.Pair.MemberId, v.Pair.MemberXbrlCode, v.Pair.Restriction));
        }

        var dps = orderedDps.Count > 0 ? string.Join("|", orderedDps) : null;
        var dms = orderedDms.Count > 0 ? string.Join("|", orderedDms) : null;
        return (dps, dms);
    }

    private static void EmitCategorisation(
        List<CategorisationRowData> categorisationRows,
        int ordinateId,
        List<(int DimensionId, string DimensionXbrlCode, int MemberId, string? MemberXbrlCode)>? pairs,
        (int HierarchyId, string HierarchyCode)? restriction = null)
    {
        if (pairs is null)
        {
            return;
        }

        foreach (var (dimensionId, dimensionXbrlCode, memberId, memberXbrlCode) in pairs)
        {
            var dps = ComposeDps(dimensionId, dimensionXbrlCode, memberId, memberXbrlCode, restriction);
            var dms = ComposeDms(dimensionId, dimensionXbrlCode, memberId, memberXbrlCode, restriction);
            categorisationRows.Add(new CategorisationRowData(ordinateId, dimensionId, memberId, dps, dms));
        }
    }

    /// <summary>
    /// The <b>DPS</b> signature (XBRL codes) of ONE PAIR — applied here only at PAIR level; the
    /// composition of a full cell, with inheritance and Y/X/Z order, is done by
    /// <see cref="ComposeCellSignature"/>: metric first (<c>MET(memberXbrl)</c>), open-axis
    /// sentinel — without restriction, <c>dimXbrl(*)</c>; WITH restriction,
    /// <c>dimXbrl(*[HierarchyCode])</c> — or the normal closed pair (<c>dimXbrl(memberXbrl)</c>).
    /// <paramref name="restriction"/> is only looked at when <paramref name="memberId"/> is the
    /// open-axis sentinel (the metric never produces a bracket even if a metric categorisation came
    /// with a restriction).
    /// </summary>
    private static string ComposeDps(
        int dimensionId, string dimensionXbrlCode, int memberId, string? memberXbrlCode,
        (int HierarchyId, string HierarchyCode)? restriction = null)
    {
        if (dimensionId == MetDimensionId)
        {
            return $"MET({memberXbrlCode})";
        }

        if (memberId == OpenAxisMemberSentinel)
        {
            return restriction is { } r ? $"{dimensionXbrlCode}(*[{r.HierarchyCode}])" : $"{dimensionXbrlCode}(*)";
        }

        return $"{dimensionXbrlCode}({memberXbrlCode})";
    }

    /// <summary>
    /// The <b>DMS</b> signature (database IDs) of ONE PAIR — same case as
    /// <see cref="ComposeDps"/> and DIFFERENT only in the bracket of the open-axis sentinel WITH a
    /// restriction: <c>dimXbrl(*[HierarchyID])</c> instead of <c>dimXbrl(*[HierarchyCode])</c>.
    /// Outside that case, <c>Dps</c> and <c>Dms</c> are the same text (same invariant as DPM 1.0).
    /// </summary>
    private static string ComposeDms(
        int dimensionId, string dimensionXbrlCode, int memberId, string? memberXbrlCode,
        (int HierarchyId, string HierarchyCode)? restriction = null)
    {
        if (dimensionId == MetDimensionId)
        {
            return $"MET({memberXbrlCode})";
        }

        if (memberId == OpenAxisMemberSentinel)
        {
            return restriction is { } r ? $"{dimensionXbrlCode}(*[{r.HierarchyId}])" : $"{dimensionXbrlCode}(*)";
        }

        return $"{dimensionXbrlCode}({memberXbrlCode})";
    }
}
