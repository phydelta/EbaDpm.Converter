using System.Globalization;
using EbaDpm.Converter.Core.Access;
using EbaDpm.Converter.Core.Sqlite;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Core.Mapping;

/// <summary>
/// Loads the axes and the cells: <c>mAxis</c>, <c>mTableAxis</c>, <c>mAxisOrdinate</c>,
/// <c>mOrdinateCategorisation</c>, <c>mTableCell</c>, <c>mCellPosition</c> and
/// <c>mOpenAxisValueRestriction</c>. It runs after <see cref="TemplateOrTableLoader.Load"/>, on the
/// same connection: it reuses the <c>TableIdByTableVId</c> that loader already computed (the same
/// synthetic <c>TableID</c>, without repeating the deduplication).
///
/// <b>The axis transformation, summarised:</b>
/// <list type="bullet">
/// <item><description>
/// Axes with <c>AxisOrientation = 'O'</c> are discarded: 0 ordinates, 0 labels, 0 restrictions.
/// If one carried any content an exception is thrown, since it signals that the source changed.
/// </description></item>
/// <item><description>
/// Closed table: each <c>Axis</c> becomes one <c>mAxis</c>, with its ordinates preserving the
/// Access tree (<c>ParentOrdinateID</c> is the only authoritative field; <c>Path</c> is NEVER
/// read).
/// </description></item>
/// <item><description>
/// Open table: the ordinates of the X axis with <c>IsRowKey = 1</c> stop being columns and each
/// one generates an open Y axis of its own with a single ordinate, which keeps the literal
/// <c>OrdinateCode</c> and <c>OrdinateLabel</c>. The open Y axis of Access (the one of the
/// <c>'999 '</c> ordinate) disappears. The open Z axis is split into N Z axes, one per open
/// dimension of the sentinel (<c>MemberID = 999</c>), ordered by ascending <c>DimensionID</c>,
/// with <c>OrdinateCode</c>/<c>AxisCode</c> renumbered to 4 digits.
/// </description></item>
/// <item><description>
/// <c>mTableAxis.Order</c>: by class (<c>X</c> &lt; closed <c>Y</c> &lt; closed <c>Z</c> &lt; open
/// <c>Y</c> &lt; open <c>Z</c>) and, within the class, by ascending <c>DimensionID</c> of the
/// axis's open dimension; dense 1..n.
/// </description></item>
/// </list>
///
/// <b>IDs.</b> The <c>AxisID</c> and <c>OrdinateID</c> of the CLOSED axes/ordinates and of the key
/// Y axes (whose single ordinate is literally the Access X column, only relocated) reuse the
/// Access ID: there is a real 1:1 correspondence with a source row, just as
/// <c>mDomain</c>/<c>mMember</c>/<c>mDimension</c>/<c>mHierarchy</c> reuse theirs. The axes with no
/// 1:1 counterpart in <c>Access.Axis</c> (the open Z axes split by dimension) and their ordinates
/// ARE synthesised, with a counter that starts above the maximum real ID read, so it never
/// collides with a real ID nor with another synthetic one.
///
/// The <c>mConcept.ConceptID</c> of every emitted axis and ordinate, on the other hand, is
/// **always minted**: it has no usable source, and the target renumbers it, so it is a surrogate
/// that the converter mints. The counter starts above the <c>MAX(ConceptID)</c> already written
/// to the target (by <c>DictionaryLoader</c>/<c>TemplateOrTableLoader</c>/<c>ModuleLoader</c>).
/// <c>Access.Axis.ConceptID</c>/<c>Access.AxisOrdinate.ConceptID</c> are never reused verbatim,
/// because the target requires <c>ConceptType = 'Axis'</c>/<c>'Ordinate'</c> while Access already
/// uses a different <c>ConceptType</c> (e.g. <c>'AxisOrdinate'</c>) for that same concept, and
/// mixing "real ID, rewritten type" with the rest of the dictionary (which does copy the
/// <c>ConceptType</c> as is) would introduce a stylistic inconsistency with no measurable benefit:
/// no consumer can compare these <c>ConceptID</c> values with anything (all comparisons go by
/// business key). This is an implementation choice, not a figure measured anywhere.
///
/// Each of those minted <c>ConceptID</c> values is seeded into <c>mConceptTranslation</c> with the
/// same <c>AxisLabel</c>/<c>OrdinateLabel</c> that <c>mAxis</c>/<c>mAxisOrdinate</c> already emit
/// (<see cref="WriteConceptTranslations"/>).
///
/// <b>Data point signatures.</b>
/// <c>mOrdinateCategorisation.DPS</c>/<c>DimensionMemberSignature</c> and
/// <c>mTableCell.DPS</c>/<c>DatapointSignature</c> are computed here, not left
/// <see langword="null"/>. The signature algorithm is implemented literally:
/// <list type="bullet">
/// <item><description>
/// The signature of ONE PAIR (<see cref="ComputeCategorisationSignature"/>) has two variants that
/// are composed SEPARATELY: <c>Dps</c> with XBRL codes and <c>Dms</c> (which feeds
/// <c>DimensionMemberSignature</c>/<c>DatapointSignature</c>) with database IDs. In practice they
/// only differ in the bracket of the hierarchy restriction of a semi-open axis (<c>*[BT3]</c>
/// versus <c>*[31]</c>): everything else (dimension, member, metric) is emitted verbatim and is
/// therefore identical in both variants, measured at 100% over the three references. Measured
/// extension: when the restriction of a semi-open axis also fixes a STARTING MEMBER (51 of 351 in
/// Access v4.1), the bracket has three parts,
/// <c>*[hierarchyCode;startMemberCode;includedFlag]</c> /
/// <c>*[hierarchyId;startMemberId;includedFlag]</c>, instead of one. See
/// <see cref="ComputeCategorisationSignature"/>.
/// </description></item>
/// <item><description>
/// The signature of a CELL (<see cref="ComposeCellSignature"/>) inherits through the ordinate tree
/// (the closest wins), discards default members except the metric, and composes <c>MET(...)</c>
/// first and the rest ordered ordinally by dimension XBRL code. A cell with <c>IsShaded = 1</c>
/// carries a <see langword="null"/> signature.
/// </description></item>
/// <item><description>
/// The metric dimension is identified by its role (<see cref="IdentifyMetricDimensionIds"/>),
/// never by a literal ID. OUR emission does not inherit the metric in
/// <c>mOrdinateCategorisation</c> (it is not reset), but the cell composition algorithm CAN
/// inherit it through the tree if no ordinate of its own declares it: that is the consistency
/// check counted by <c>Result.MetricInheritedFromAncestorCellCount</c>, which must currently be 0.
/// </description></item>
/// </list>
/// </summary>
public static class AxisAndCellLoader
{
    private const int ClassX = 0;
    private const int ClassYClosed = 1;
    private const int ClassZClosed = 2;
    private const int ClassYOpen = 3;
    private const int ClassZOpen = 4;

    private const int OpenMemberSentinel = 999; // Access.OrdinateCategorisation.MemberID
    private const int DestinationOpenMember = 9999; // mOrdinateCategorisation.MemberID

    /// <summary>
    /// Defensive depth limit when walking <c>ParentOrdinateID</c> up to the root: far above any
    /// real header nesting (tens, not hundreds), to turn an unforeseen cycle into an explicit
    /// failure instead of a hang.
    /// </summary>
    private const int MaxOrdinateTreeDepth = 64;

    private static readonly List<AccessOrdinateCategorisationRow> EmptyCategorisations = [];

    /// <summary>Row count written per table, for the CLI report.</summary>
    /// <param name="MetricInheritedFromAncestorCellCount">
    /// Number of cells whose winning metric categorisation comes from an ANCESTOR ordinate and not
    /// from the ordinate directly attached to the cell (the signature algorithm does not
    /// distinguish the metric when inheriting, but our emission should never produce this case).
    /// It does not abort the conversion: it is counted and reported.
    /// </param>
    /// <param name="TransferredSentinelFixedPairs">
    /// Number of FIXED categorisations (<c>IsDefaultMember=False</c>, <c>MemberID != 999</c>) of the
    /// Access sentinel ordinate that have been transferred to the target ordinate that inherits its
    /// position on the open axis. They are transferred when the sentinel → target ordinate
    /// correspondence is 1:1, and also in the 1:2 pattern (two open axes from the same sentinel,
    /// a single fixed dimension: they go to the FIRST axis in <c>mTableAxis.Order</c> order, shown
    /// to be indifferent cell by cell). Any other shape is not decided; see
    /// <paramref name="AmbiguousSentinelFixedPairSkips"/>.
    /// </param>
    /// <param name="AmbiguousSentinelFixedPairSkips">
    /// Number of sentinel ordinates with FIXED categorisations whose split shape is neither of the
    /// two already adjudicated (1:1, or 1:2 with a single dimension), e.g. a sentinel split into
    /// 3+, or split into 2 with more than one fixed dimension. They are NOT relocated: they are
    /// counted and listed in <paramref name="AmbiguousSentinelFixedPairDetails"/> so that each case
    /// can be adjudicated.
    /// </param>
    /// <param name="AmbiguousSentinelFixedPairDetails">
    /// One line per sentinel counted in <paramref name="AmbiguousSentinelFixedPairSkips"/>: table,
    /// ordinate, axis, split factor and the fixed dimensions involved.
    /// </param>
    public sealed record Result(
        int AxisRows,
        int TableAxisRows,
        int AxisOrdinateRows,
        int OrdinateCategorisationRows,
        int TableCellRows,
        int CellPositionRows,
        int OpenAxisValueRestrictionRows,
        int SkippedDefaultMemberResets,
        int MetricInheritedFromAncestorCellCount,
        int TransferredSentinelFixedPairs,
        int AmbiguousSentinelFixedPairSkips,
        IReadOnlyList<string> AmbiguousSentinelFixedPairDetails);

    public static Result Load(
        IDpmSourceReader source,
        SqliteConnection destination,
        IReadOnlyDictionary<int, int> tableIdByTableVId)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(tableIdByTableVId);

        var tableVIds = tableIdByTableVId.Keys.OrderBy(id => id).ToList();
        if (tableVIds.Count == 0)
        {
            return new Result(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, []);
        }

        var dimensionsById = source.ReadDimensions().ToDictionary(d => d.DimensionId);
        var membersById = source.ReadMembers().ToDictionary(m => m.MemberId); // MemberXBRLCode/IsDefaultMember
        var defaultMemberIdByDomainId = BuildDefaultMemberByDomainId(membersById.Values);
        var hierarchiesById = source.ReadHierarchies().ToDictionary(h => h.HierarchyId); // HierarchyCode
        var openMemberRestrictionsById = source.ReadOpenMemberRestrictions().ToDictionary(r => r.RestrictionId);
        var accessOwnerId = source.ReadOwners().Select(o => o.OwnerId).First();

        // The metric dimension (Access models it as a real Dimension, "ATY", with the DomainID
        // identified by its role) is EXCLUDED from the inheritance/reset mechanism of
        // mOrdinateCategorisation: each ordinate that has its own metric declares it (the closure
        // of its own categorisations already does that, unchanged), but NO ordinate inherits the
        // metric from its parent nor is reset.
        var metricDimensionIds = IdentifyMetricDimensionIds(source, dimensionsById);

        var axesByTableVId = source.ReadAxesByTableVIds(tableVIds)
            .GroupBy(a => a.TableVId ?? throw new InvalidOperationException("Axis without TableVID."))
            .ToDictionary(g => g.Key, g => g.ToList());

        var ordinatesByAxisId = source.ReadAxisOrdinatesByTableVIds(tableVIds)
            .GroupBy(o => o.AxisId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var categorisationsByOrdinateId = source.ReadOrdinateCategorisationsByTableVIds(tableVIds)
            .GroupBy(c => c.OrdinateId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var maxAccessAxisId = axesByTableVId.Count == 0
            ? 0
            : axesByTableVId.Values.SelectMany(list => list).Max(a => a.AxisId);
        var maxAccessOrdinateId = ordinatesByAxisId.Count == 0
            ? 0
            : ordinatesByAxisId.Values.SelectMany(list => list).Max(o => o.OrdinateId);

        var counters = new MintingCounters(
            NextAxisId: maxAccessAxisId + 1,
            NextOrdinateId: maxAccessOrdinateId + 1,
            NextConceptId: ReadDestinationMaxConceptId(destination) + 1);

        var accumulator = new Accumulator();

        foreach (var tableVId in tableVIds)
        {
            var tableId = tableIdByTableVId[tableVId];
            var axesOfTable = axesByTableVId.GetValueOrDefault(tableVId, []);

            ProcessTable(
                tableVId,
                tableId,
                axesOfTable,
                ordinatesByAxisId,
                categorisationsByOrdinateId,
                dimensionsById,
                openMemberRestrictionsById,
                counters,
                accumulator);
        }

        // Two deliberately separate phases (see the XML doc of each method): phase 1 fixes the
        // semantics (closure + resets) and does not change; phase 2 is currently the identity and
        // is the place where a future minimisation rule would go without touching phase 1.
        var safeForm = BuildSafeFormCategorisations(
            accumulator.ClosedOrdinateTree,
            categorisationsByOrdinateId,
            dimensionsById,
            defaultMemberIdByDomainId,
            metricDimensionIds,
            out var skippedResets);
        var minimizedClosedCategorisations = MinimizeSafeFormCategorisations(safeForm, accumulator.ClosedOrdinateTree);

        var openAxisValueRestrictionRows = ResolveOpenAxisValueRestrictions(
            accumulator.PendingRestrictions,
            categorisationsByOrdinateId,
            openMemberRestrictionsById);

        // The signature of each pair is computed once, in the same order in which the row is
        // written (closedRows first, openAxisRows after): it is the list that
        // WriteOrdinateCategorisations dumps literally and the same one that, grouped by
        // OrdinateID, feeds the tree inheritance of the cell signature.
        var categorisationSignatures = BuildCategorisationSignatures(
            minimizedClosedCategorisations,
            accumulator.OpenAxisCategorisationRows,
            metricDimensionIds,
            dimensionsById,
            membersById,
            openMemberRestrictionsById,
            hierarchiesById);

        WriteAxisTables(destination, accumulator);
        var categorisationRows = WriteOrdinateCategorisations(destination, categorisationSignatures);
        WriteOpenAxisValueRestrictions(destination, openAxisValueRestrictionRows);
        WriteMintedConcepts(destination, accumulator.MintedConcepts, accessOwnerId);
        WriteConceptTranslations(destination, accumulator);

        var ownCategorisationsByOrdinateId = categorisationSignatures
            .GroupBy(s => s.OrdinateId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<CategorisationSignature>)g.ToList());

        // Inheritance tree: closed ordinates carry their real ParentOrdinateID; the ordinates of an
        // open axis are not in ClosedOrdinateTree and therefore resolve to "no parent"
        // (TryGetValue returns false), which is exactly their semantics: a single position, with
        // no ancestry.
        var parentByOrdinateId = accumulator.ClosedOrdinateTree
            .ToDictionary(t => t.OrdinateId, t => t.ParentOrdinateId);

        var excludedCellIds = new HashSet<int>();
        var cellSignatures = new Dictionary<int, (string Dps, string Dms, bool MetricInheritedFromAncestor)>();
        var cellPositionRows = WriteCellPositions(
            source, destination, tableVIds, accumulator.ClosedOrdinateIds, accumulator.ExcludedOrdinateIds,
            accumulator.SentinelExpansion, excludedCellIds, ownCategorisationsByOrdinateId, parentByOrdinateId,
            metricDimensionIds, cellSignatures);
        var tableCellRows = WriteTableCells(
            source, destination, tableVIds, tableIdByTableVId, excludedCellIds, cellSignatures,
            out var metricInheritedFromAncestorCellCount);

        return new Result(
            AxisRows: accumulator.AxisRows.Count,
            TableAxisRows: accumulator.TableAxisRows.Count,
            AxisOrdinateRows: accumulator.OrdinateRows.Count,
            OrdinateCategorisationRows: categorisationRows,
            TableCellRows: tableCellRows,
            CellPositionRows: cellPositionRows,
            OpenAxisValueRestrictionRows: openAxisValueRestrictionRows.Count,
            SkippedDefaultMemberResets: skippedResets,
            MetricInheritedFromAncestorCellCount: metricInheritedFromAncestorCellCount,
            TransferredSentinelFixedPairs: accumulator.TransferredSentinelFixedPairs,
            AmbiguousSentinelFixedPairSkips: accumulator.AmbiguousSentinelSkips.Count,
            AmbiguousSentinelFixedPairDetails: accumulator.AmbiguousSentinelSkips
                .OrderBy(s => s.TableVId)
                .Select(s => $"TableVID={s.TableVId} OrdinateID={s.SentinelOrdinateId} Axis={s.AxisOrientation} " +
                    $"SplitFactor={s.SplitFactor} FixedPairs={s.FixedPairCount} DimensionIDs=[{s.FixedDimensionIds}]")
                .ToList());
    }

    private sealed record MintingCounters(int NextAxisId, int NextOrdinateId, int NextConceptId)
    {
        public int NextAxisId { get; set; } = NextAxisId;
        public int NextOrdinateId { get; set; } = NextOrdinateId;
        public int NextConceptId { get; set; } = NextConceptId;
    }

    /// <summary>In-memory accumulators for all the tables (small census: thousands of rows, not millions).</summary>
    private sealed class Accumulator
    {
        public List<AxisWriteRow> AxisRows { get; } = [];
        public List<(int AxisId, int TableId, int Order)> TableAxisRows { get; } = [];
        public List<OrdinateWriteRow> OrdinateRows { get; } = [];
        public List<(int OrdinateId, int DimensionId, int MemberId, int? RestrictionId)> OpenAxisCategorisationRows { get; } = [];
        public List<(int OrdinateId, int? ParentOrdinateId)> ClosedOrdinateTree { get; } = [];
        public List<PendingRestriction> PendingRestrictions { get; } = [];
        public List<(int ConceptId, string ConceptType)> MintedConcepts { get; } = [];
        public HashSet<int> ClosedOrdinateIds { get; } = [];
        public HashSet<int> ExcludedOrdinateIds { get; } = [];
        public Dictionary<int, List<int>> SentinelExpansion { get; } = [];

        // Fixed pairs of the sentinel ordinate.
        public int TransferredSentinelFixedPairs { get; set; }
        public HashSet<int> ReportedAmbiguousSentinelIds { get; } = [];
        public List<AmbiguousSentinelSkip> AmbiguousSentinelSkips { get; } = [];
    }

    /// <summary>
    /// A sentinel with FIXED categorisations whose open axis is split into <c>SplitFactor</c> &gt; 1
    /// target ordinates: the relocation stops being 1:1 and is not decided here. One is
    /// accumulated per <c>SentinelOrdinateId</c> (not one per generated target ordinate).
    /// </summary>
    private sealed record AmbiguousSentinelSkip(
        int TableVId, int SentinelOrdinateId, string AxisOrientation, int SplitFactor,
        int FixedPairCount, string FixedDimensionIds);

    private sealed record AxisWriteRow(
        int AxisId, string AxisOrientation, string? AxisLabel, bool IsOpenAxis, bool OptionalKey,
        int ConceptId, string? AxisCode);

    private sealed record OrdinateWriteRow(
        int AxisId, int OrdinateId, string OrdinateLabel, string OrdinateCode, bool IsAbstractHeader,
        bool IsRowKey, int Level, int Order, int? ParentOrdinateId, int ConceptId);

    private sealed record PendingRestriction(int AxisId, int SentinelOrdinateId, int DimensionId);

    // ------------------------------------------------------------------
    // Per table: classifies the Access axes and emits the target axis/ordinate plans.
    // ------------------------------------------------------------------

    private static void ProcessTable(
        int tableVId,
        int tableId,
        List<AccessAxisRow> axesOfTable,
        IReadOnlyDictionary<int, List<AccessAxisOrdinateRow>> ordinatesByAxisId,
        IReadOnlyDictionary<int, List<AccessOrdinateCategorisationRow>> categorisationsByOrdinateId,
        IReadOnlyDictionary<int, AccessDimensionRow> dimensionsById,
        IReadOnlyDictionary<int, AccessOpenMemberRestrictionRow> openMemberRestrictionsById,
        MintingCounters counters,
        Accumulator acc)
    {
        _ = openMemberRestrictionsById; // resolved later, after accumulating PendingRestrictions

        foreach (var axis in axesOfTable)
        {
            // NEVER use Access.Axis.AxisOrder (NULL in 6,508/6,508). If it appears populated, the
            // source has changed and the rule must be revisited, not silently continued.
            if (axis.AxisOrder is not null)
            {
                throw new InvalidOperationException(
                    $"Axis.AxisOrder is not NULL (AxisID={axis.AxisId}, TableVID={tableVId}): the source " +
                    "has changed; the axis ordering rule must be revisited before continuing.");
            }
        }

        var orientationGroups = axesOfTable
            .Where(a => a.AxisOrientation != "O")
            .GroupBy(a => a.AxisOrientation)
            .ToDictionary(g => g.Key ?? throw new InvalidOperationException(
                $"Axis without AxisOrientation (TableVID={tableVId})."));

        foreach (var (orientation, group) in orientationGroups)
        {
            var count = group.Count();
            if (count > 1)
            {
                throw new InvalidOperationException(
                    $"TableVID={tableVId} has {count} axes with orientation '{orientation}': " +
                    "Access never has more than one per orientation.");
            }
        }

        // 'O' axes are discarded; if one carries content it is an anomaly and the run must fail,
        // not silently discard it.
        foreach (var oAxis in axesOfTable.Where(a => a.AxisOrientation == "O"))
        {
            var ordinates = ordinatesByAxisId.GetValueOrDefault(oAxis.AxisId, []);
            if (ordinates.Count > 0 || !string.IsNullOrWhiteSpace(oAxis.AxisLabel))
            {
                throw new InvalidOperationException(
                    $"'O' axis with content (AxisID={oAxis.AxisId}, TableVID={tableVId}): " +
                    "the source has changed; the rule for 'O' axes must be revisited.");
            }
        }

        var xAxis = orientationGroups.GetValueOrDefault("X")?.SingleOrDefault();
        var yAxis = orientationGroups.GetValueOrDefault("Y")?.SingleOrDefault();
        var zAxis = orientationGroups.GetValueOrDefault("Z")?.SingleOrDefault();

        if (xAxis is null)
        {
            throw new InvalidOperationException($"TableVID={tableVId} has no X axis.");
        }

        if (xAxis.IsOpenAxis)
        {
            // Access v4.1 has no open X axes. If one appeared, the case is not implemented:
            // fail, do not guess.
            throw new InvalidOperationException(
                $"Open X axis (AxisID={xAxis.AxisId}, TableVID={tableVId}): unsupported case " +
                "(the mapping assumes that Access v4.1 has no open X axes).");
        }

        var xOrdinates = ordinatesByAxisId.GetValueOrDefault(xAxis.AxisId, []);
        var keyOrdinates = xOrdinates.Where(o => o.IsRowKey).OrderBy(o => o.OrdinateId).ToList();
        var excludedIds = keyOrdinates.Select(o => o.OrdinateId).ToHashSet();

        // ---- target X axis (non-key columns) ----
        var xSurvivors = BuildClosedAxisOrdinateTree(xOrdinates, excludedIds, tableVId, "X");
        if (xSurvivors.Count == 0)
        {
            throw new InvalidOperationException(
                $"The X axis of TableVID={tableVId} is left without ordinates after excluding the key " +
                "columns: unsupported anomaly.");
        }

        var pendingAxes = new List<PendingAxis>
        {
            new(xAxis.AxisId, "X", IsOpenAxis: false, ClassX, DimensionSortKey: 0,
                AxisLabel: ResolveClosedAxisLabel(xAxis, "Columns"), AxisCode: null,
                Ordinates: xSurvivors, SentinelOrdinateIdForRestriction: null, RestrictionDimensionId: null),
        };

        RegisterClosedOrdinates(xSurvivors, acc);

        // ---- target Y axis: closed, or N open key Y axes ----
        int? ySentinelOrdinateId = null;
        if (yAxis is not null && !yAxis.IsOpenAxis)
        {
            var yOrdinates = ordinatesByAxisId.GetValueOrDefault(yAxis.AxisId, []);
            var ySurvivors = BuildClosedAxisOrdinateTree(yOrdinates, [], tableVId, "Y");
            pendingAxes.Add(new PendingAxis(
                yAxis.AxisId, "Y", IsOpenAxis: false, ClassYClosed, DimensionSortKey: 0,
                AxisLabel: ResolveClosedAxisLabel(yAxis, "Rows"), AxisCode: null,
                Ordinates: ySurvivors, SentinelOrdinateIdForRestriction: null, RestrictionDimensionId: null));
            RegisterClosedOrdinates(ySurvivors, acc);
        }
        else if (yAxis is not null && yAxis.IsOpenAxis)
        {
            var ySentinelOrdinates = ordinatesByAxisId.GetValueOrDefault(yAxis.AxisId, []);
            if (ySentinelOrdinates.Count != 1)
            {
                throw new InvalidOperationException(
                    $"The open Y axis of TableVID={tableVId} (AxisID={yAxis.AxisId}) does not have " +
                    $"exactly 1 ordinate (it has {ySentinelOrdinates.Count}); source invariant violated.");
            }

            ySentinelOrdinateId = ySentinelOrdinates[0].OrdinateId;
        }

        if (keyOrdinates.Count > 0 && ySentinelOrdinateId is null)
        {
            throw new InvalidOperationException(
                $"TableVID={tableVId} has {keyOrdinates.Count} X columns with IsRowKey=1 but no " +
                "open Y axis from which their restriction hung (source invariant violated).");
        }

        foreach (var key in keyOrdinates)
        {
            var ownCategorisations = categorisationsByOrdinateId.GetValueOrDefault(key.OrdinateId, EmptyCategorisations)
                .Where(c => c.MemberId == OpenMemberSentinel)
                .ToList();

            if (ownCategorisations.Count != 1)
            {
                throw new InvalidOperationException(
                    $"The key column OrdinateID={key.OrdinateId} (TableVID={tableVId}) has " +
                    $"{ownCategorisations.Count} categorisations with MemberID={OpenMemberSentinel}, " +
                    "exactly 1 was expected.");
            }

            var dimensionId = ownCategorisations[0].DimensionId;
            var axisId = counters.NextAxisId++;
            var label = key.OrdinateLabel.Trim();
            var code = key.OrdinateCode; // already TRIMmed by the reader

            var ordinate = new OrdinatePlan(
                key.OrdinateId, label, code, key.IsAbstractHeader, IsRowKey: true, Level: 0, Order: 0,
                ParentOrdinateId: null);

            pendingAxes.Add(new PendingAxis(
                axisId, "Y", IsOpenAxis: true, ClassYOpen, DimensionSortKey: dimensionId,
                AxisLabel: label, AxisCode: code,
                Ordinates: [ordinate], SentinelOrdinateIdForRestriction: ySentinelOrdinateId!.Value,
                RestrictionDimensionId: dimensionId));

            // The OrdinateID is reused literally (it is the same Access column, relocated): it does
            // not enter ClosedOrdinateIds (it is not a "closed" ordinate), and it does not take
            // part in the closure of mOrdinateCategorisation; its only row is that of the open axis.
            acc.ExcludedOrdinateIds.Add(key.OrdinateId);
        }

        // ---- target Z axis: closed, or N open Z axes by dimension ----
        if (zAxis is not null && !zAxis.IsOpenAxis)
        {
            var zOrdinates = ordinatesByAxisId.GetValueOrDefault(zAxis.AxisId, []);
            var zSurvivors = BuildClosedAxisOrdinateTree(zOrdinates, [], tableVId, "Z");
            pendingAxes.Add(new PendingAxis(
                zAxis.AxisId, "Z", IsOpenAxis: false, ClassZClosed, DimensionSortKey: 0,
                AxisLabel: ResolveClosedAxisLabel(zAxis, "Sheets"), AxisCode: null,
                Ordinates: zSurvivors, SentinelOrdinateIdForRestriction: null, RestrictionDimensionId: null));
            RegisterClosedOrdinates(zSurvivors, acc);
        }
        else if (zAxis is not null && zAxis.IsOpenAxis)
        {
            var zSentinelOrdinates = ordinatesByAxisId.GetValueOrDefault(zAxis.AxisId, []);
            if (zSentinelOrdinates.Count != 1)
            {
                throw new InvalidOperationException(
                    $"The open Z axis of TableVID={tableVId} (AxisID={zAxis.AxisId}) does not have " +
                    $"exactly 1 ordinate (it has {zSentinelOrdinates.Count}); source invariant violated.");
            }

            var sentinel = zSentinelOrdinates[0];
            var dimensionIds = categorisationsByOrdinateId.GetValueOrDefault(sentinel.OrdinateId, EmptyCategorisations)
                .Where(c => c.MemberId == OpenMemberSentinel)
                .Select(c => c.DimensionId)
                .Distinct()
                .OrderBy(d => d)
                .ToList();

            if (dimensionIds.Count == 0)
            {
                throw new InvalidOperationException(
                    $"The sentinel of the open Z axis (OrdinateID={sentinel.OrdinateId}, " +
                    $"TableVID={tableVId}) has no categorisation with MemberID=" +
                    $"{OpenMemberSentinel}.");
            }

            for (var k = 0; k < dimensionIds.Count; k++)
            {
                var dimensionId = dimensionIds[k];
                var axisId = counters.NextAxisId++;
                var ordinateId = counters.NextOrdinateId++;
                var code = ((k + 1) * 10).ToString("D4"); // 0010, 0020, ...

                var label = dimensionIds.Count == 1
                    ? sentinel.OrdinateLabel.Trim()
                    : dimensionsById.TryGetValue(dimensionId, out var dim)
                        ? dim.DimensionLabel
                        : throw new InvalidOperationException(
                            $"Categorisation references DimensionID={dimensionId}, which is missing from Dimension.");

                var ordinate = new OrdinatePlan(
                    ordinateId, label, code, sentinel.IsAbstractHeader, IsRowKey: true, Level: 0, Order: 0,
                    ParentOrdinateId: null);

                pendingAxes.Add(new PendingAxis(
                    axisId, "Z", IsOpenAxis: true, ClassZOpen, DimensionSortKey: dimensionId,
                    AxisLabel: label, AxisCode: code,
                    Ordinates: [ordinate], SentinelOrdinateIdForRestriction: sentinel.OrdinateId,
                    RestrictionDimensionId: dimensionId));
            }
        }

        // Split factor of each sentinel: how many open PendingAxis entries (key Y or Z split by
        // dimension) share the SAME Access sentinel OrdinateID. 1 => the relocation of its FIXED
        // categorisations is unambiguous. 2 => it was shown (comparing the DPS of ALL the cells of
        // the 3 affected tables, assigning first to the first axis and then to the second, byte for
        // byte identical) that the cell is the intersection of one ordinate from EACH axis, so the
        // fixed pair reaches every cell whichever axis it is on: it is assigned to the FIRST axis
        // in this same order (putting it on both is forbidden). N>1 with any other shape (split
        // into 3+, or more than one fixed dimension spread over a split in 2) remains undecided and
        // must KEEP being reported, not generalised without measuring.
        var openAxisSentinelSplitFactor = pendingAxes
            .Where(a => a.IsOpenAxis)
            .GroupBy(a => a.SentinelOrdinateIdForRestriction!.Value)
            .ToDictionary(g => g.Key, g => g.Count());

        // OrdinateID of the FIRST open axis of each sentinel, in the same order (ascending
        // DimensionSortKey) with which mTableAxis.Order is numbered below. Each open PendingAxis has
        // exactly 1 ordinate (by construction above), so its OrdinateID is already known here,
        // before the `ordered` loop.
        var firstOrdinateIdBySentinel = pendingAxes
            .Where(a => a.IsOpenAxis)
            .GroupBy(a => a.SentinelOrdinateIdForRestriction!.Value)
            .ToDictionary(g => g.Key, g => g.OrderBy(a => a.DimensionSortKey).First().Ordinates[0].OrdinateId);

        // ---- number mTableAxis.Order: by class, and within the class by ascending DimensionID ----
        var ordered = pendingAxes
            .OrderBy(a => a.ClassOrder)
            .ThenBy(a => a.DimensionSortKey)
            .ToList();

        for (var i = 0; i < ordered.Count; i++)
        {
            var plan = ordered[i];
            var conceptId = counters.NextConceptId++;
            acc.MintedConcepts.Add((conceptId, "Axis"));

            acc.AxisRows.Add(new AxisWriteRow(
                plan.AxisId, plan.AxisOrientation, plan.AxisLabel, plan.IsOpenAxis,
                OptionalKey: plan.IsOpenAxis, conceptId, plan.AxisCode));

            acc.TableAxisRows.Add((plan.AxisId, tableId, i + 1));

            foreach (var ordinate in plan.Ordinates)
            {
                var ordinateConceptId = counters.NextConceptId++;
                acc.MintedConcepts.Add((ordinateConceptId, "Ordinate"));

                acc.OrdinateRows.Add(new OrdinateWriteRow(
                    plan.AxisId, ordinate.OrdinateId, ordinate.OrdinateLabel, ordinate.OrdinateCode,
                    ordinate.IsAbstractHeader, ordinate.IsRowKey, ordinate.Level, ordinate.Order,
                    ordinate.ParentOrdinateId, ordinateConceptId));
            }

            if (plan.IsOpenAxis)
            {
                var ordinate = plan.Ordinates[0];
                var dimensionId = plan.RestrictionDimensionId!.Value;

                // The RestrictionID that decides "open" (*) versus "semi-open"
                // (*[HierarchyCode]) lives in the categorisation of the Access SENTINEL ordinate,
                // not in the row emitted here. It is resolved now, once, so as not to repeat the
                // walk over categorisationsByOrdinateId when composing the signature.
                var restrictionId = ResolveSentinelRestrictionId(
                    plan.SentinelOrdinateIdForRestriction!.Value, dimensionId, categorisationsByOrdinateId);

                acc.OpenAxisCategorisationRows.Add((ordinate.OrdinateId, dimensionId, DestinationOpenMember, restrictionId));
                acc.PendingRestrictions.Add(new PendingRestriction(
                    plan.AxisId, plan.SentinelOrdinateIdForRestriction!.Value, dimensionId));

                if (!acc.SentinelExpansion.TryGetValue(plan.SentinelOrdinateIdForRestriction!.Value, out var list))
                {
                    list = [];
                    acc.SentinelExpansion[plan.SentinelOrdinateIdForRestriction!.Value] = list;
                }

                list.Add(ordinate.OrdinateId);

                // The Access sentinel ordinate can carry, besides the open pair (MemberID=999),
                // FIXED categorisations (IsDefaultMember=False, MemberID != 999) that would be lost
                // when the sentinel is withdrawn. They are transferred to the target ordinate that
                // inherits its position when the sentinel → target correspondence is 1:1, or when it
                // is the measured and adjudicated 1:2 pattern (two open axes from the same
                // sentinel, a SINGLE fixed dimension): in that case they go to the FIRST axis in
                // mTableAxis.Order order, since putting them on both is forbidden and the second
                // axis does nothing more here. Outside those two shapes the relocation is NOT
                // decided: it is reported, and must KEEP being reported for any new shape.
                var sentinelId = plan.SentinelOrdinateIdForRestriction!.Value;
                var fixedPairsOfSentinel = categorisationsByOrdinateId
                    .GetValueOrDefault(sentinelId, EmptyCategorisations)
                    .Where(c => c.MemberId != OpenMemberSentinel)
                    .ToList();

                if (fixedPairsOfSentinel.Count > 0)
                {
                    var splitFactor = openAxisSentinelSplitFactor[sentinelId];
                    var isTwoWayPattern = splitFactor == 2 && fixedPairsOfSentinel.Count == 1;

                    if (splitFactor == 1 || (isTwoWayPattern && ordinate.OrdinateId == firstOrdinateIdBySentinel[sentinelId]))
                    {
                        foreach (var fixedPair in fixedPairsOfSentinel)
                        {
                            if (fixedPair.DimensionId == dimensionId)
                            {
                                throw new InvalidOperationException(
                                    $"Sentinel OrdinateID={sentinelId} (TableVID={tableVId}) carries both " +
                                    $"the open pair and a FIXED categorisation for the same DimensionID=" +
                                    $"{dimensionId}: unsupported anomaly.");
                            }

                            acc.OpenAxisCategorisationRows.Add(
                                (ordinate.OrdinateId, fixedPair.DimensionId, fixedPair.MemberId, null));
                            acc.TransferredSentinelFixedPairs++;
                        }
                    }
                    else if (isTwoWayPattern)
                    {
                        // Second axis of the two-way pattern: it was already assigned to the first
                        // one, there is nothing to do or to report here (one dimension, a single axis).
                    }
                    else if (acc.ReportedAmbiguousSentinelIds.Add(sentinelId))
                    {
                        acc.AmbiguousSentinelSkips.Add(new AmbiguousSentinelSkip(
                            tableVId, sentinelId, plan.AxisOrientation, splitFactor,
                            fixedPairsOfSentinel.Count,
                            string.Join(",", fixedPairsOfSentinel.Select(c => c.DimensionId).Distinct().OrderBy(d => d))));
                    }
                }
            }
        }
    }

    private static void RegisterClosedOrdinates(List<OrdinatePlan> survivors, Accumulator acc)
    {
        foreach (var o in survivors)
        {
            acc.ClosedOrdinateIds.Add(o.OrdinateId);
            acc.ClosedOrdinateTree.Add((o.OrdinateId, o.ParentOrdinateId));
        }
    }

    /// <summary>
    /// Label of a closed axis: the Access one if present, otherwise the literal by orientation
    /// (<c>COREP_3.2 / C_67.00.a</c> is the only legitimate exception, and this same rule
    /// reproduces it).
    /// </summary>
    private static string ResolveClosedAxisLabel(AccessAxisRow axis, string defaultLabel)
        => string.IsNullOrWhiteSpace(axis.AxisLabel) ? defaultLabel : axis.AxisLabel.Trim();

    private sealed record PendingAxis(
        int AxisId, string AxisOrientation, bool IsOpenAxis, int ClassOrder, int DimensionSortKey,
        string? AxisLabel, string? AxisCode, List<OrdinatePlan> Ordinates,
        int? SentinelOrdinateIdForRestriction, int? RestrictionDimensionId);

    private sealed record OrdinatePlan(
        int OrdinateId, string OrdinateLabel, string OrdinateCode, bool IsAbstractHeader, bool IsRowKey,
        int Level, int Order, int? ParentOrdinateId);

    /// <summary>
    /// Builds the tree of a closed axis after excluding <paramref name="excludedIds"/> (the
    /// <c>IsRowKey</c> columns of the X axis; empty for closed Y/Z), and assigns
    /// <c>mAxisOrdinate.[Order]</c> as the PREORDER traversal of the survivors, siblings ordered by
    /// <c>Access.[Order]</c>, 1-based. <c>Level</c> is copied literally (not recomputed), which is
    /// only correct if no survivor hangs from an excluded ordinate; this is validated explicitly,
    /// and the run fails if it happens (an unforeseen case is never silently repaired).
    /// </summary>
    private static List<OrdinatePlan> BuildClosedAxisOrdinateTree(
        List<AccessAxisOrdinateRow> allOrdinatesOfAxis, HashSet<int> excludedIds, int tableVId, string orientation)
    {
        var survivors = allOrdinatesOfAxis.Where(o => !excludedIds.Contains(o.OrdinateId)).ToList();

        foreach (var o in survivors)
        {
            if (o.ParentOrdinateId is { } pid && excludedIds.Contains(pid))
            {
                throw new InvalidOperationException(
                    $"Ordinate {o.OrdinateId} (TableVID={tableVId}, axis {orientation}) survives the transformation " +
                    $"but its parent {pid} is an excluded IsRowKey column: unsupported reparenting.");
            }
        }

        var childrenByParent = survivors
            .Where(o => o.ParentOrdinateId.HasValue)
            .GroupBy(o => o.ParentOrdinateId!.Value)
            .ToDictionary(g => g.Key, g => g.OrderBy(o => o.Order).ToList());

        var roots = survivors.Where(o => o.ParentOrdinateId is null).OrderBy(o => o.Order).ToList();

        var result = new List<OrdinatePlan>(survivors.Count);
        var nextOrder = 1;

        void Visit(AccessAxisOrdinateRow node)
        {
            result.Add(new OrdinatePlan(
                node.OrdinateId, node.OrdinateLabel.Trim(), node.OrdinateCode, node.IsAbstractHeader,
                IsRowKey: false, node.Level, nextOrder++, node.ParentOrdinateId));

            if (childrenByParent.TryGetValue(node.OrdinateId, out var kids))
            {
                foreach (var kid in kids)
                {
                    Visit(kid);
                }
            }
        }

        foreach (var r in roots)
        {
            Visit(r);
        }

        if (result.Count != survivors.Count)
        {
            throw new InvalidOperationException(
                $"Incomplete preorder traversal in TableVID={tableVId}, axis {orientation}: {result.Count} of " +
                $"{survivors.Count} ordinates visited. There are cycles or parents outside the " +
                "survivor set (unsupported anomaly).");
        }

        return result;
    }

    // ------------------------------------------------------------------
    // mOrdinateCategorisation: two separate phases.
    // ------------------------------------------------------------------

    private static Dictionary<int, int> BuildDefaultMemberByDomainId(IEnumerable<AccessMemberRow> members)
    {
        var result = new Dictionary<int, int>();

        foreach (var member in members)
        {
            if (!member.IsDefaultMember || member.DomainId is not { } domainId)
            {
                continue;
            }

            if (!result.TryAdd(domainId, member.MemberId))
            {
                throw new InvalidOperationException(
                    $"The domain DomainID={domainId} has more than one member with IsDefaultMember=1 " +
                    $"(at least {result[domainId]} and {member.MemberId}).");
            }
        }

        return result;
    }

    /// <summary>
    /// Identifies the metric dimension(s) by their structural role, the same criterion that
    /// <c>DictionaryLoader.IdentifyMetricsDomainId</c> uses for the domain: the domain whose set of
    /// <c>MemberID</c> values matches exactly that of <c>Metric</c>. It returns every
    /// <c>Dimension</c> of that domain. In Access v4.1 it is a single real dimension (<c>ATY</c>),
    /// but the criterion is robust to there being more than one or to its ID changing between
    /// releases: a literal ID is never compared.
    /// </summary>
    private static HashSet<int> IdentifyMetricDimensionIds(
        IDpmSourceReader source, IReadOnlyDictionary<int, AccessDimensionRow> dimensionsById)
    {
        var metricMemberIds = new HashSet<int>(source.ReadMetrics().Select(m => m.MetricId));

        var memberIdsByDomain = new Dictionary<int, HashSet<int>>();
        foreach (var member in source.ReadMembers())
        {
            if (member.DomainId is not { } domainId)
            {
                continue;
            }

            if (!memberIdsByDomain.TryGetValue(domainId, out var members))
            {
                members = [];
                memberIdsByDomain[domainId] = members;
            }

            members.Add(member.MemberId);
        }

        var candidateDomainIds = memberIdsByDomain
            .Where(kv => kv.Value.SetEquals(metricMemberIds))
            .Select(kv => kv.Key)
            .ToList();

        if (candidateDomainIds.Count != 1)
        {
            throw new InvalidOperationException(
                "The metrics domain could not be identified unambiguously in order to exclude it from " +
                $"the inheritance of mOrdinateCategorisation. {candidateDomainIds.Count} " +
                $"candidate domains: [{string.Join(",", candidateDomainIds)}].");
        }

        var metricsDomainId = candidateDomainIds[0];

        return dimensionsById.Values
            .Where(d => d.DomainId == metricsDomainId)
            .Select(d => d.DimensionId)
            .ToHashSet();
    }

    /// <summary>
    /// Phase 1: builds the "safe form" for the CLOSED ordinates: for each closed ordinate O with
    /// parent P, (a) it emits every categorisation of O's own and (b) it emits a RESET to the
    /// default member of the domain, for each dimension that P has and O does not. This step fixes
    /// the semantics consumed by the signature computation (the effective closure) and <b>does not
    /// change</b>: any future adjustment of the minimisation goes in
    /// <see cref="MinimizeSafeFormCategorisations"/>, not here.
    ///
    /// The metric dimension is EXCLUDED from step (b): it is never inherited nor reset, only
    /// declared when the ordinate itself carries it (step (a), unchanged). This is not a
    /// relaxation: it is the business rule, measured over the four references.
    ///
    /// If the domain of a reset NON-metric dimension has no member with
    /// <c>IsDefaultMember = 1</c>, the reset is impossible: it is skipped and counted in
    /// <paramref name="skippedResets"/>; no default member is synthesised and the conversion is not
    /// aborted. No specific cause is attributed here: it is a counter that is reported, not an
    /// assertion that aborts. With the metric exclusion the count should be 0 in the measured
    /// universe, but if one day it is not, it is news from the source that must be looked at; it is
    /// not fixed as an expected figure in the converter.
    /// </summary>
    private static List<(int OrdinateId, int DimensionId, int MemberId)> BuildSafeFormCategorisations(
        IReadOnlyList<(int OrdinateId, int? ParentOrdinateId)> closedOrdinateTree,
        IReadOnlyDictionary<int, List<AccessOrdinateCategorisationRow>> categorisationsByOrdinateId,
        IReadOnlyDictionary<int, AccessDimensionRow> dimensionsById,
        IReadOnlyDictionary<int, int> defaultMemberIdByDomainId,
        IReadOnlySet<int> metricDimensionIds,
        out int skippedResets)
    {
        var result = new List<(int, int, int)>();
        var skipped = 0;

        foreach (var (ordinateId, parentId) in closedOrdinateTree)
        {
            var own = categorisationsByOrdinateId.GetValueOrDefault(ordinateId, EmptyCategorisations);
            var ownDimensions = new HashSet<int>();

            foreach (var c in own)
            {
                if (c.MemberId == OpenMemberSentinel)
                {
                    throw new InvalidOperationException(
                        $"The closed ordinate OrdinateID={ordinateId} carries a categorisation with the " +
                        $"sentinel MemberID={OpenMemberSentinel}: anomaly.");
                }

                if (!ownDimensions.Add(c.DimensionId))
                {
                    throw new InvalidOperationException(
                        $"OrdinateID={ordinateId} has more than one categorisation for " +
                        $"DimensionID={c.DimensionId}.");
                }

                result.Add((ordinateId, c.DimensionId, c.MemberId));
            }

            if (parentId is not { } pid)
            {
                continue; // root: nothing to reset
            }

            var parentOwn = categorisationsByOrdinateId.GetValueOrDefault(pid, EmptyCategorisations);

            foreach (var pc in parentOwn)
            {
                if (pc.MemberId == OpenMemberSentinel
                    || ownDimensions.Contains(pc.DimensionId)
                    || metricDimensionIds.Contains(pc.DimensionId)) // the metric is not inherited and never reset
                {
                    continue;
                }

                var domainId = dimensionsById.TryGetValue(pc.DimensionId, out var dim)
                    ? dim.DomainId
                    : throw new InvalidOperationException(
                        $"Categorisation references DimensionID={pc.DimensionId}, which is missing from Dimension.");

                if (defaultMemberIdByDomainId.TryGetValue(domainId, out var defaultMemberId))
                {
                    result.Add((ordinateId, pc.DimensionId, defaultMemberId));
                }
                else
                {
                    skipped++; // domain without a default member, reset impossible
                }
            }
        }

        skippedResets = skipped;
        return result;
    }

    /// <summary>
    /// Phase 2: <b>extension point</b> for a future minimisation rule for
    /// <c>mOrdinateCategorisation</c>. The reference generator applies a non-uniform compression
    /// (delta against the parent, sometimes with the row lifted to an abstract header); if a
    /// derivable rule appears, it replaces <b>only this method</b>;
    /// <see cref="BuildSafeFormCategorisations"/> does not change, because it is the one that
    /// guarantees that the effective closure, and with it the signature, is correct.
    ///
    /// TODAY it is the identity: it removes no rows (comparison is by effective closure).
    ///
    /// <b>Strict limit of any admissible minimisation</b>: it can only remove a
    /// <c>(OrdinateID, DimensionID, MemberID)</c> pair if the ordinate's PARENT already contributes
    /// that same pair identically (that is, if it is redundant with the natural inheritance of the
    /// tree). It can never remove a RESET row, the one that cancels the inheritance with the default
    /// member, because a reset by definition is not redundant with the parent ("the parent has it
    /// and the child does not" is exactly what it says), and removing it would let the child
    /// inherit in the target a member that Access declares absent: the signature would silently
    /// come out wrong.
    /// <paramref name="ordinateTree"/> (the tree of closed ordinates, OrdinateID →
    /// ParentOrdinateID) is the information that any minimisation rule will need to decide what is
    /// redundant with the parent.
    /// </summary>
    private static List<(int OrdinateId, int DimensionId, int MemberId)> MinimizeSafeFormCategorisations(
        List<(int OrdinateId, int DimensionId, int MemberId)> safeForm,
        IReadOnlyList<(int OrdinateId, int? ParentOrdinateId)> ordinateTree)
    {
        _ = ordinateTree; // not used by the identity; kept in the signature for the future rule
        return safeForm;
    }

    // ------------------------------------------------------------------
    // mOpenAxisValueRestriction.
    // ------------------------------------------------------------------

    /// <summary>
    /// The axis → restriction link comes from the categorisation of the Access SENTINEL ordinate
    /// (<c>MemberID = 999</c>) for the given dimension, not from <c>OpenAxisValueRestriction</c>.
    /// It returns the <c>RestrictionID</c> if there is exactly one, or <see langword="null"/> if the
    /// axis is open without restriction (<c>RestrictionID IS NULL</c>). More than one distinct
    /// <c>RestrictionID</c> for the same (sentinel, dimension) is an anomaly: the run fails, it
    /// does not guess.
    /// </summary>
    private static int? ResolveSentinelRestrictionId(
        int sentinelOrdinateId,
        int dimensionId,
        IReadOnlyDictionary<int, List<AccessOrdinateCategorisationRow>> categorisationsByOrdinateId)
    {
        var restrictionIds = categorisationsByOrdinateId
            .GetValueOrDefault(sentinelOrdinateId, EmptyCategorisations)
            .Where(c => c.DimensionId == dimensionId && c.MemberId == OpenMemberSentinel)
            .Select(c => c.RestrictionId)
            .Where(r => r.HasValue)
            .Select(r => r!.Value)
            .Distinct()
            .ToList();

        if (restrictionIds.Count > 1)
        {
            throw new InvalidOperationException(
                $"The sentinel OrdinateID={sentinelOrdinateId}, dimension {dimensionId}, has more " +
                $"than one distinct RestrictionID ({string.Join(",", restrictionIds)}): exactly one " +
                "is required.");
        }

        return restrictionIds.Count == 1 ? restrictionIds[0] : null;
    }

    private static List<(int AxisId, int HierarchyId, int? StartMemberId, bool? StartIncluded)> ResolveOpenAxisValueRestrictions(
        List<PendingRestriction> pending,
        IReadOnlyDictionary<int, List<AccessOrdinateCategorisationRow>> categorisationsByOrdinateId,
        IReadOnlyDictionary<int, AccessOpenMemberRestrictionRow> openMemberRestrictionsById)
    {
        var result = new List<(int, int, int?, bool?)>();

        foreach (var candidate in pending)
        {
            var restrictionId = ResolveSentinelRestrictionId(
                candidate.SentinelOrdinateId, candidate.DimensionId, categorisationsByOrdinateId);

            if (restrictionId is not { } rid)
            {
                continue; // r IS NULL -> the axis has no row
            }

            var restriction = openMemberRestrictionsById.TryGetValue(rid, out var r)
                ? r
                : throw new InvalidOperationException(
                    $"OrdinateCategorisation references RestrictionID={rid}, which is missing from " +
                    "OpenMemberRestriction.");

            if (restriction.HierarchyId is not { } hierarchyId)
            {
                continue; // without HierarchyID there is no row to emit (the target PK requires it)
            }

            var startMemberId = restriction.IgnoreMemberId ? (int?)null : restriction.MemberId;
            var startIncluded = restriction.IgnoreMemberId ? (bool?)null : restriction.MemberIncluded;

            result.Add((candidate.AxisId, hierarchyId, startMemberId, startIncluded));
        }

        return result;
    }

    // ------------------------------------------------------------------
    // Data point signatures.
    // ------------------------------------------------------------------

    /// <summary>
    /// The computed signature of ONE (dimension, member) PAIR of <c>mOrdinateCategorisation</c>:
    /// two variants composed separately. <see cref="Dps"/> uses XBRL codes (feeds
    /// <c>mOrdinateCategorisation.DPS</c> / <c>mTableCell.DPS</c>); <see cref="Dms"/> uses database
    /// IDs (feeds <c>DimensionMemberSignature</c> / <c>DatapointSignature</c>). In practice they
    /// only differ in the bracket of a semi-open axis, measured at 100% over the three references,
    /// with 0 differences outside that bracket. <see cref="SortKey"/> is the XBRL code of the
    /// dimension (or <c>MET</c> for the metric, although it is not used for ordering: the metric
    /// always goes first), the key of the ascending ordinal ordering of the remaining pairs.
    /// </summary>
    private sealed record CategorisationSignature(
        int OrdinateId,
        int DimensionId,
        int MemberId,
        bool IsDefaultMember,
        string Dps,
        string Dms,
        string SortKey);

    /// <summary>
    /// Signature of a (dimension, member) pair. Priority of cases: metric first (note that
    /// <c>RestrictionID</c> can accompany a metric categorisation, 1187/1177 in R 22.03, and there
    /// it does NOT produce brackets, so it is not even looked at), then the target open-value
    /// sentinel (open or semi-open depending on <paramref name="restrictionId"/>), and finally the
    /// explicit case. All XBRL codes are emitted VERBATIM: a release suffix is never composed here.
    /// </summary>
    private static CategorisationSignature ComputeCategorisationSignature(
        int ordinateId,
        int dimensionId,
        int memberId,
        int? restrictionId,
        IReadOnlySet<int> metricDimensionIds,
        IReadOnlyDictionary<int, AccessDimensionRow> dimensionsById,
        IReadOnlyDictionary<int, AccessMemberRow> membersById,
        IReadOnlyDictionary<int, AccessOpenMemberRestrictionRow> openMemberRestrictionsById,
        IReadOnlyDictionary<int, AccessHierarchyRow> hierarchiesById)
    {
        if (metricDimensionIds.Contains(dimensionId))
        {
            var metricMember = membersById.TryGetValue(memberId, out var mm)
                ? mm
                : throw new InvalidOperationException(
                    $"Metric categorisation (OrdinateID={ordinateId}) references MemberID={memberId}, " +
                    "which is missing from Member.");

            var metricXbrl = metricMember.MemberXbrlCode
                ?? throw new InvalidOperationException(
                    $"The metric member MemberID={memberId} (OrdinateID={ordinateId}) has no " +
                    "MemberXbrlCode: MET(...) cannot be composed.");

            var metricPair = $"MET({metricXbrl})";
            return new CategorisationSignature(
                ordinateId, dimensionId, memberId, metricMember.IsDefaultMember, metricPair, metricPair, "MET");
        }

        var dimension = dimensionsById.TryGetValue(dimensionId, out var dim)
            ? dim
            : throw new InvalidOperationException(
                $"Categorisation (OrdinateID={ordinateId}) references DimensionID={dimensionId}, which is " +
                "missing from Dimension.");

        var dimXbrl = dimension.DimensionXbrlCode
            ?? throw new InvalidOperationException(
                $"DimensionID={dimensionId} (OrdinateID={ordinateId}) has no DimensionXbrlCode and is not " +
                "the metric dimension: the signature algorithm does not cover this case.");

        if (memberId == DestinationOpenMember)
        {
            if (restrictionId is not { } rid)
            {
                var openPair = $"{dimXbrl}(*)";
                return new CategorisationSignature(
                    ordinateId, dimensionId, memberId, IsDefaultMember: false, openPair, openPair, dimXbrl);
            }

            var restriction = openMemberRestrictionsById.TryGetValue(rid, out var r)
                ? r
                : throw new InvalidOperationException(
                    $"OrdinateCategorisation (OrdinateID={ordinateId}) references RestrictionID={rid}, " +
                    "which is missing from OpenMemberRestriction.");

            var hierarchyId = restriction.HierarchyId
                ?? throw new InvalidOperationException(
                    $"RestrictionID={rid} (OrdinateID={ordinateId}) has no HierarchyID: the signature " +
                    "algorithm requires it for the semi-open case.");

            var hierarchy = hierarchiesById.TryGetValue(hierarchyId, out var h)
                ? h
                : throw new InvalidOperationException(
                    $"RestrictionID={rid} references HierarchyID={hierarchyId}, which is missing from Hierarchy.");

            // Measured extension, verified against the 3.2 reference: when the restriction fixes a
            // STARTING MEMBER (OpenMemberRestriction.MemberID with IgnoreMemberID=0, the same
            // thing that feeds mOpenAxisValueRestriction.HierarchyStartingMemberID /
            // IsStartingMemberIncluded), the bracket has THREE parts, not one:
            // "*[hierarchyCode;startMemberCode;includedFlag]" in DPS and
            // "*[hierarchyId;startMemberId;includedFlag]" in the ID variant. Verified against
            // Access OpenMemberRestriction (RestrictionID 32/36, HierarchyID 12729/12674,
            // MemberCode 'x0') and mOpenAxisValueRestriction of the 3.2 reference (HierarchyID
            // 52/81, HierarchyStartingMemberID 764/1458). Without a starting member
            // (IgnoreMemberID=1, 300 of 351 restrictions in Access v4.1) the bracket stays a
            // single part.
            string dps;
            string dms;

            if (restriction.IgnoreMemberId)
            {
                dps = $"{dimXbrl}(*[{hierarchy.HierarchyCode}])";
                dms = $"{dimXbrl}(*[{hierarchyId.ToString(CultureInfo.InvariantCulture)}])";
            }
            else
            {
                var startMemberId = restriction.MemberId
                    ?? throw new InvalidOperationException(
                        $"RestrictionID={rid} (OrdinateID={ordinateId}) has IgnoreMemberID=0 but " +
                        "MemberID is NULL: unsupported anomaly.");

                var startMember = membersById.TryGetValue(startMemberId, out var sm)
                    ? sm
                    : throw new InvalidOperationException(
                        $"RestrictionID={rid} references MemberID={startMemberId} as the starting " +
                        "member, which is missing from Member.");

                var includedFlag = restriction.MemberIncluded ? "1" : "0";

                dps = $"{dimXbrl}(*[{hierarchy.HierarchyCode};{startMember.MemberCode};{includedFlag}])";
                dms = $"{dimXbrl}(*[{hierarchyId.ToString(CultureInfo.InvariantCulture)};" +
                    $"{startMemberId.ToString(CultureInfo.InvariantCulture)};{includedFlag}])";
            }

            return new CategorisationSignature(
                ordinateId, dimensionId, memberId, IsDefaultMember: false, dps, dms, dimXbrl);
        }

        var member = membersById.TryGetValue(memberId, out var mem)
            ? mem
            : throw new InvalidOperationException(
                $"Categorisation (OrdinateID={ordinateId}) references MemberID={memberId}, which is missing from Member.");

        var memberXbrl = member.MemberXbrlCode
            ?? throw new InvalidOperationException(
                $"MemberID={memberId} (OrdinateID={ordinateId}, DimensionID={dimensionId}) has no " +
                "MemberXbrlCode: the signature algorithm does not cover this case.");

        var explicitPair = $"{dimXbrl}({memberXbrl})";
        return new CategorisationSignature(
            ordinateId, dimensionId, memberId, member.IsDefaultMember, explicitPair, explicitPair, dimXbrl);
    }

    /// <summary>
    /// Computes the signature of each row to be written to <c>mOrdinateCategorisation</c>, in the
    /// SAME order in which <see cref="WriteOrdinateCategorisations"/> dumps them (closedRows, then
    /// openAxisRows): that list is also the one that, grouped by <c>OrdinateID</c>, feeds the tree
    /// inheritance (<see cref="ComposeCellSignature"/>); it is the effective closure.
    /// </summary>
    private static List<CategorisationSignature> BuildCategorisationSignatures(
        List<(int OrdinateId, int DimensionId, int MemberId)> closedRows,
        List<(int OrdinateId, int DimensionId, int MemberId, int? RestrictionId)> openAxisRows,
        IReadOnlySet<int> metricDimensionIds,
        IReadOnlyDictionary<int, AccessDimensionRow> dimensionsById,
        IReadOnlyDictionary<int, AccessMemberRow> membersById,
        IReadOnlyDictionary<int, AccessOpenMemberRestrictionRow> openMemberRestrictionsById,
        IReadOnlyDictionary<int, AccessHierarchyRow> hierarchiesById)
    {
        var result = new List<CategorisationSignature>(closedRows.Count + openAxisRows.Count);

        foreach (var (ordinateId, dimensionId, memberId) in closedRows)
        {
            result.Add(ComputeCategorisationSignature(
                ordinateId, dimensionId, memberId, restrictionId: null,
                metricDimensionIds, dimensionsById, membersById, openMemberRestrictionsById, hierarchiesById));
        }

        foreach (var (ordinateId, dimensionId, memberId, restrictionId) in openAxisRows)
        {
            result.Add(ComputeCategorisationSignature(
                ordinateId, dimensionId, memberId, restrictionId,
                metricDimensionIds, dimensionsById, membersById, openMemberRestrictionsById, hierarchiesById));
        }

        return result;
    }

    /// <summary>
    /// Full signature of a cell, literal algorithm ("the rules that are not obvious").
    /// <paramref name="ordinateIdsOfCell"/> are the already EXPANDED <c>OrdinateID</c> values
    /// (sentinel → open axes) that <c>mCellPosition</c> associates with the cell.
    /// <list type="number">
    /// <item><description>
    /// Collect with inheritance: for each ordinate of the cell, walk up through
    /// <c>ParentOrdinateID</c> (<paramref name="parentByOrdinateId"/>) accumulating depth; for each
    /// dimension, the pair seen at the LOWEST depth (the closest ordinate) wins.
    /// </description></item>
    /// <item><description>
    /// Discard default members, except the metric dimension (always exempt), which is kept even if
    /// its member is flagged as default.
    /// </description></item>
    /// <item><description>
    /// Compose <c>MET(...)</c> first, the rest ordered ordinally by the dimension XBRL code
    /// (<see cref="CategorisationSignature.SortKey"/>).
    /// </description></item>
    /// </list>
    /// The consistency check (does the winning metric come from an ANCESTOR, depth &gt; 0, instead
    /// of the ordinate itself?) is returned in <c>MetricInheritedFromAncestor</c>: it does not
    /// abort, it is only counted. The algorithm does not distinguish the metric when inheriting,
    /// but our emission should never let it happen.
    /// </summary>
    private static (string Dps, string Dms, bool MetricInheritedFromAncestor) ComposeCellSignature(
        List<int> ordinateIdsOfCell,
        IReadOnlyDictionary<int, IReadOnlyList<CategorisationSignature>> ownCategorisationsByOrdinateId,
        IReadOnlyDictionary<int, int?> parentByOrdinateId,
        IReadOnlySet<int> metricDimensionIds)
    {
        var chosen = new Dictionary<int, (int Depth, CategorisationSignature Pair)>();

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
                        $"The ordinate tree starting at OrdinateID={start} exceeds the maximum expected " +
                        $"depth ({MaxOrdinateTreeDepth}): possible cycle, unsupported anomaly.");
                }

                if (ownCategorisationsByOrdinateId.TryGetValue(currentId, out var cats))
                {
                    foreach (var c in cats)
                    {
                        if (!chosen.TryGetValue(c.DimensionId, out var existing) || depth < existing.Depth)
                        {
                            chosen[c.DimensionId] = (depth, c);
                        }
                    }
                }

                current = parentByOrdinateId.TryGetValue(currentId, out var parent) ? parent : null;
                depth++;
            }
        }

        // Rules 2/3: default members are discarded, except the metric (always exempt, even with
        // IsDefaultMember=1; in 4.0/4.2 ALL metric members are flagged that way).
        var alive = chosen.Values
            .Where(v => metricDimensionIds.Contains(v.Pair.DimensionId) || !v.Pair.IsDefaultMember)
            .ToList();

        var metricEntries = alive.Where(v => metricDimensionIds.Contains(v.Pair.DimensionId)).ToList();
        if (metricEntries.Count > 1)
        {
            throw new InvalidOperationException(
                $"Cell with ordinates [{string.Join(",", ordinateIdsOfCell)}]: {metricEntries.Count} " +
                "metric categorisations alive simultaneously (at most one MET(...) is allowed).");
        }

        var ordered = new List<CategorisationSignature>(alive.Count);
        var metricInherited = false;

        if (metricEntries.Count == 1)
        {
            ordered.Add(metricEntries[0].Pair);
            metricInherited = metricEntries[0].Depth > 0; // consistency check, does not abort
        }

        ordered.AddRange(alive
            .Where(v => !metricDimensionIds.Contains(v.Pair.DimensionId))
            .OrderBy(v => v.Pair.SortKey, StringComparer.Ordinal)
            .Select(v => v.Pair));

        var dps = string.Join("|", ordered.Select(p => p.Dps));
        var dms = string.Join("|", ordered.Select(p => p.Dms));

        return (dps, dms, metricInherited);
    }

    // ------------------------------------------------------------------
    // Writing: one live SqliteBatchWriter at a time on the connection (see its type doc).
    // ------------------------------------------------------------------

    private static void WriteAxisTables(SqliteConnection destination, Accumulator acc)
    {
        using (var writer = new SqliteBatchWriter(
            destination, "mAxis",
            ["AxisID", "AxisOrientation", "AxisLabel", "IsOpenAxis", "OptionalKey", "ConceptID", "AxisCode"]))
        {
            foreach (var row in acc.AxisRows)
            {
                writer.AddRow(row.AxisId, row.AxisOrientation, row.AxisLabel, row.IsOpenAxis, row.OptionalKey, row.ConceptId, row.AxisCode);
            }
        }

        using (var writer = new SqliteBatchWriter(destination, "mTableAxis", ["AxisID", "TableID", "Order"]))
        {
            foreach (var row in acc.TableAxisRows)
            {
                writer.AddRow(row.AxisId, row.TableId, row.Order);
            }
        }

        using (var writer = new SqliteBatchWriter(
            destination, "mAxisOrdinate",
            [
                "AxisID", "OrdinateID", "OrdinateLabel", "OrdinateCode", "IsDisplayBeforeChildren",
                "IsAbstractHeader", "IsRowKey", "Level", "Order", "ParentOrdinateID", "ConceptID",
                "TypeOfKey", "RelatedDimensionTableId",
            ]))
        {
            foreach (var row in acc.OrdinateRows)
            {
                writer.AddRow(
                    row.AxisId, row.OrdinateId, row.OrdinateLabel, row.OrdinateCode,
                    false, // IsDisplayBeforeChildren: constant 0
                    row.IsAbstractHeader, row.IsRowKey, row.Level, row.Order, row.ParentOrdinateId,
                    row.ConceptId,
                    null, // TypeOfKey: constant NULL
                    null); // RelatedDimensionTableId: constant NULL
            }
        }
    }

    private static int WriteOrdinateCategorisations(
        SqliteConnection destination, List<CategorisationSignature> signatures)
    {
        using var writer = new SqliteBatchWriter(
            destination, "mOrdinateCategorisation",
            ["OrdinateID", "DimensionID", "MemberID", "DimensionMemberSignature", "Source", "DPS"]);

        foreach (var s in signatures)
        {
            writer.AddRow(s.OrdinateId, s.DimensionId, s.MemberId, s.Dms, null, s.Dps); // Source NULL
        }

        return (int)writer.RowsWritten;
    }

    private static void WriteOpenAxisValueRestrictions(
        SqliteConnection destination,
        List<(int AxisId, int HierarchyId, int? StartMemberId, bool? StartIncluded)> rows)
    {
        using var writer = new SqliteBatchWriter(
            destination, "mOpenAxisValueRestriction",
            ["AxisID", "HierarchyID", "HierarchyStartingMemberID", "IsStartingMemberIncluded"]);

        foreach (var row in rows)
        {
            writer.AddRow(row.AxisId, row.HierarchyId, row.StartMemberId, row.StartIncluded);
        }
    }

    private static void WriteMintedConcepts(
        SqliteConnection destination, List<(int ConceptId, string ConceptType)> mintedConcepts, int accessOwnerId)
    {
        using var writer = new SqliteBatchWriter(
            destination, "mConcept",
            ["ConceptID", "ConceptType", "OwnerID", "ReleaseID", "CreationDate", "ModificationDate", "FromDate", "ToDate"]);

        foreach (var (conceptId, conceptType) in mintedConcepts)
        {
            writer.AddRow(
                conceptId, conceptType, accessOwnerId,
                null, // ReleaseID: always NULL for Axis/Ordinate
                null, null, null, null); // dates: no source, minted concept (see the class XML doc)
        }
    }

    /// <summary>
    /// Seeds <c>mConceptTranslation</c> for each <c>ConceptID</c> MINTED by this phase
    /// (Axis/Ordinate, always synthetic; see the class XML doc) with the same
    /// <c>AxisLabel</c>/<c>OrdinateLabel</c> that <c>mAxis</c>/<c>mAxisOrdinate</c> already emit.
    /// </summary>
    private static void WriteConceptTranslations(SqliteConnection destination, Accumulator acc)
    {
        var seeds = acc.AxisRows
            .Select(r => (r.ConceptId, r.AxisLabel, (string?)null))
            .Concat(acc.OrdinateRows.Select(r => (r.ConceptId, (string?)r.OrdinateLabel, (string?)null)));

        ConceptTranslationWriter.Write(destination, seeds);
    }

    // ------------------------------------------------------------------
    // mCellPosition / mTableCell: cells and positions, BusinessCode.
    // ------------------------------------------------------------------

    private static int WriteCellPositions(
        IDpmSourceReader source,
        SqliteConnection destination,
        List<int> tableVIds,
        HashSet<int> closedOrdinateIds,
        HashSet<int> excludedOrdinateIds,
        Dictionary<int, List<int>> sentinelExpansion,
        HashSet<int> excludedCellIds,
        IReadOnlyDictionary<int, IReadOnlyList<CategorisationSignature>> ownCategorisationsByOrdinateId,
        IReadOnlyDictionary<int, int?> parentByOrdinateId,
        IReadOnlySet<int> metricDimensionIds,
        Dictionary<int, (string Dps, string Dms, bool MetricInheritedFromAncestor)> cellSignatures)
    {
        var rows = 0;
        using var writer = new SqliteBatchWriter(destination, "mCellPosition", ["CellID", "OrdinateID"]);

        foreach (var group in GroupConsecutiveByCell(source.ReadCellPositionsByTableVIds(tableVIds)))
        {
            var cellId = group[0].CellId;
            var excluded = group.Exists(p => excludedOrdinateIds.Contains(p.OrdinateId));

            if (excluded)
            {
                excludedCellIds.Add(cellId);
                continue;
            }

            // The already EXPANDED OrdinateID values (sentinel -> open axes) are exactly the ones
            // the cell signature walks to compose the signature, the same ones written to
            // mCellPosition, so they are accumulated in the same loop without re-reading anything.
            var expandedOrdinateIds = new List<int>(group.Count);

            foreach (var position in group)
            {
                if (closedOrdinateIds.Contains(position.OrdinateId))
                {
                    writer.AddRow(cellId, position.OrdinateId);
                    rows++;
                    expandedOrdinateIds.Add(position.OrdinateId);
                }
                else if (sentinelExpansion.TryGetValue(position.OrdinateId, out var destinationIds))
                {
                    foreach (var destinationOrdinateId in destinationIds)
                    {
                        writer.AddRow(cellId, destinationOrdinateId);
                        rows++;
                        expandedOrdinateIds.Add(destinationOrdinateId);
                    }
                }
                else
                {
                    throw new InvalidOperationException(
                        $"CellPosition (CellID={cellId}, OrdinateID={position.OrdinateId}) does not correspond " +
                        "to any emitted ordinate nor to a known open-axis sentinel.");
                }
            }

            var (dps, dms, metricInheritedFromAncestor) = ComposeCellSignature(
                expandedOrdinateIds, ownCategorisationsByOrdinateId, parentByOrdinateId, metricDimensionIds);

            cellSignatures[cellId] = (dps, dms, metricInheritedFromAncestor);
        }

        return rows;
    }

    private static int WriteTableCells(
        IDpmSourceReader source,
        SqliteConnection destination,
        List<int> tableVIds,
        IReadOnlyDictionary<int, int> tableIdByTableVId,
        HashSet<int> excludedCellIds,
        IReadOnlyDictionary<int, (string Dps, string Dms, bool MetricInheritedFromAncestor)> cellSignatures,
        out int metricInheritedFromAncestorCellCount)
    {
        var rows = 0;
        var metricInherited = 0;
        using var writer = new SqliteBatchWriter(
            destination, "mTableCell",
            ["CellID", "TableID", "IsRowKey", "IsShaded", "BusinessCode", "DatapointSignature", "DPS"]);

        foreach (var cell in source.ReadTableCellsByTableVIds(tableVIds))
        {
            if (excludedCellIds.Contains(cell.CellId))
            {
                continue;
            }

            var tableId = cell.TableVId is { } tableVId && tableIdByTableVId.TryGetValue(tableVId, out var id)
                ? id
                : throw new InvalidOperationException(
                    $"TableCell CellID={cell.CellId} references TableVID={cell.TableVId}, outside the selection.");

            // Shaded -> NULL signature, not an empty string. If it is not shaded, the cell must
            // have a computed signature (every non-excluded TableCell has >=1 CellPosition,
            // measured 0 exceptions in Access v4.1): if it is missing, it is an anomaly.
            // The metric-inherited-from-ancestor consistency check is only counted here, over
            // NON-shaded cells: the signature of a shaded cell is NULL anyway, so its inheritance
            // tree is irrelevant to what is actually emitted.
            string? datapointSignature = null;
            string? dps = null;

            if (!cell.IsShaded)
            {
                if (!cellSignatures.TryGetValue(cell.CellId, out var signature))
                {
                    throw new InvalidOperationException(
                        $"TableCell CellID={cell.CellId} is not shaded but has no computed signature " +
                        "(no resolved CellPosition): unsupported anomaly.");
                }

                datapointSignature = signature.Dms;
                dps = signature.Dps;

                if (signature.MetricInheritedFromAncestor)
                {
                    metricInherited++;
                }
            }

            writer.AddRow(
                cell.CellId,
                tableId,
                false, // IsRowKey: constant 0
                cell.IsShaded, // literal
                TranslateBusinessCode(cell.CellCode),
                datapointSignature,
                dps);

            rows++;
        }

        metricInheritedFromAncestorCellCount = metricInherited;
        return rows;
    }

    private static IEnumerable<List<AccessCellPositionRow>> GroupConsecutiveByCell(
        IEnumerable<AccessCellPositionRow> positions)
    {
        List<AccessCellPositionRow>? current = null;
        var currentCellId = 0;

        foreach (var position in positions)
        {
            if (current is null || position.CellId != currentCellId)
            {
                if (current is not null)
                {
                    yield return current;
                }

                current = [];
                currentCellId = position.CellId;
            }

            current.Add(position);
        }

        if (current is not null)
        {
            yield return current;
        }
    }

    /// <summary>
    /// Translates the literal <c>Access.TableCell.CellCode</c> to <c>mTableCell.BusinessCode</c>,
    /// discarding the sentinel coordinates <c>999</c> of the open axes. Measured at 100.0000 %
    /// against the primary reference.
    /// </summary>
    internal static string TranslateBusinessCode(string cellCode)
    {
        var trimmed = cellCode.Trim();
        if (trimmed.Length < 2 || trimmed[0] != '{' || trimmed[^1] != '}')
        {
            throw new InvalidOperationException($"CellCode with an unexpected shape (no braces): '{cellCode}'.");
        }

        var inner = trimmed[1..^1];
        var tokens = inner.Split(',');
        if (tokens.Length < 1 || string.IsNullOrWhiteSpace(tokens[0]))
        {
            throw new InvalidOperationException($"CellCode without a table segment: '{cellCode}'.");
        }

        var tableSegment = tokens[0].Trim().Replace(' ', '_');
        var coords = new List<string>(tokens.Length - 1);

        for (var i = 1; i < tokens.Length; i++)
        {
            var token = tokens[i].Trim();
            if (token.Length < 2 || (token[0] != 'r' && token[0] != 'c' && token[0] != 's'))
            {
                throw new InvalidOperationException(
                    $"CellCode with a coordinate of unexpected prefix (r/c/s expected): '{cellCode}'.");
            }

            var code = token[1..];
            if (code != "999")
            {
                coords.Add(code);
            }
        }

        return "{" + tableSegment + "," + string.Join(",", coords) + "}";
    }

    private static int ReadDestinationMaxConceptId(SqliteConnection destination)
    {
        using var command = destination.CreateCommand();
        command.CommandText = "SELECT MAX(\"ConceptID\") FROM mConcept";
        var result = command.ExecuteScalar();
        return result is null or DBNull ? 0 : Convert.ToInt32(result);
    }
}
