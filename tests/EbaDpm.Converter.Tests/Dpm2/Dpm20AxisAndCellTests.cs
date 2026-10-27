using EbaDpm.Converter.Tests.Dictionary;
using EbaDpm.Converter.Tests.Infrastructure;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Tests.Dpm2;

/// <summary>
/// Verification of the DPM 2.0 <c>mAxis</c>/<c>mTableAxis</c>, <c>mAxisOrdinate</c>,
/// <c>mTableCell</c>, <c>mCellPosition</c> and <c>mOrdinateCategorisation</c> tables.
/// Reuses <see cref="Dpm20SkeletonFixture"/> (collection <c>Dpm2Skeleton</c>): the same
/// <c>--all</c> conversion always runs <c>Dpm20AxisAndCellLoader.Load</c>, with no separate flag
/// (<c>CliRunner.RunConvertDpm20</c>), so the large DPM 2.0 Access database does not need to be read
/// again.
///
/// Every census of this file is a scoped pin of <see cref="ReleasePins"/> (scope
/// <see cref="ReleasePins.Dpm20Cutoff42"/>): an OUTPUT of the mapping rules that cannot be derived
/// from the Access source without re-implementing them. The failure message prints the scope, and
/// the identities that follow from <c>docs/mapping-dpm2.md</c> are asserted as a second measurement.
///
/// The counts are NOT those measured against the reference over its own 846 (table, taxonomy)
/// pairs: the converter emits a different set of pairs - it used to miss
/// <c>(C_34.02.b, if 4.2)</c> and to emit <c>(S_04.00, pay 4.1)</c> in excess, the two named cases.
/// Everything hanging from a pair inherits that difference, and it is spelled out in each test, not
/// only in this comment, so that whoever reads a failure understands why the figure is not the one
/// measured against the reference.
///
/// The <c>(C_34.02.b, if 4.2)</c> gap is CLOSED by the abstract-closure rule - see
/// <see cref="Dpm20StructureLoaderTests"/>. The 846 pairs become 847, and with them ALL the
/// "adjusted" figures of this file, which were measured again against the real output after the
/// change: <c>mAxis</c>/<c>mTableAxis</c> 1,990 -&gt; 1,993 (+3: the closed X/Y/Z axes of the new
/// table), <c>mAxisOrdinate</c> 20,679 -&gt; 20,712 (+33), <c>mTableCell</c> 162,778 -&gt; 163,218
/// (+440), <c>mCellPosition</c> 414,266 -&gt; 415,586 (+1,320).
/// </summary>
[Collection("Dpm2Skeleton")]
[Trait("Tier", "RealData")]
public sealed class Dpm20AxisAndCellTests(Dpm20SkeletonFixture fixture)
{
    [DataFact]
    public void Sanity_ThePipelineLoadedRowsInTheSixTables()
    {
        foreach (var table in new[] { "mAxis", "mAxisOrdinate", "mTableCell", "mCellPosition", "mOrdinateCategorisation" })
        {
            var rows = CountRows(table);
            Assert.True(rows > 0, $"{table} has {rows} rows: the pipeline loaded nothing into it.");
        }
    }

    // ------------------------------------------------------------------
    // The ADJUSTED counts (measured against the reference: 1,991/20,705/163,206/415,562 over ITS
    // 846 pairs; we emit 846 DIFFERENT pairs - subtract C_34.02.b/if 4.2, add S_04.00/pay 4.1).
    // ------------------------------------------------------------------

    [DataFact]
    public void MAxis_AndMTableAxis_MatchThePinnedCensus()
    {
        // Reference: 1,991. -3 (C_34.02.b, not linked with IF) +2 (S_04.00, orphan in pay 4.1) = 1,990,
        // +3 after the abstract-closure rule (C_34.02.b/if 4.2 is back, with its three closed X/Y/Z
        // axes) = 1,993.
        var axes = CountRows("mAxis");
        var tableAxes = CountRows("mTableAxis");
        ReleasePins.AssertPinned(ReleasePins.Dpm20Cutoff42, "mAxis rows", ReleasePins.Dpm20Cutoff42MAxisRows, axes);
        ReleasePins.AssertPinned(ReleasePins.Dpm20Cutoff42, "mTableAxis rows", ReleasePins.Dpm20Cutoff42MAxisRows, tableAxes);

        // Second measurement: every axis is minted for exactly one table (fresh IDs per pair), so
        // the two tables hold the same number of rows whatever the release.
        Assert.True(axes == tableAxes, $"mAxis has {axes} rows but mTableAxis has {tableAxes}: an axis belongs to exactly one table.");
    }

    [DataFact]
    public void MAxisOrdinate_MatchesThePinnedCensus()
    {
        // Reference: 20,705. -33 (C_34.02.b) +7 (S_04.00) = 20,679, +33 after the abstract-closure rule = 20,712.
        ReleasePins.AssertPinned(ReleasePins.Dpm20Cutoff42, "mAxisOrdinate rows", ReleasePins.Dpm20Cutoff42MAxisOrdinateRows, CountRows("mAxisOrdinate"));
    }

    [DataFact]
    public void MTableCell_MatchesThePinnedCensus()
    {
        // Reference: 163,206. -440 (C_34.02.b) +12 (S_04.00) = 162,778, +440 after the abstract-closure rule = 163,218.
        ReleasePins.AssertPinned(ReleasePins.Dpm20Cutoff42, "mTableCell rows", ReleasePins.Dpm20Cutoff42MTableCellRows, CountRows("mTableCell"));
    }

    [DataFact]
    public void MCellPosition_MatchesThePinnedCensus()
    {
        // Reference: 415,562. -1,320 (C_34.02.b) +24 (S_04.00) = 414,266, +1,320 after the abstract-closure rule = 415,586.
        var positions = CountRows("mCellPosition");
        ReleasePins.AssertPinned(ReleasePins.Dpm20Cutoff42, "mCellPosition rows", ReleasePins.Dpm20Cutoff42MCellPositionRows, positions);

        // Second measurement (docs/mapping-dpm2.md, "mCellPosition: one row per axis of the table"):
        // the rows are the sum, over the cells, of the number of axes of their table.
        var expected = ScalarLong(
            """
            SELECT COALESCE(SUM(cells.n * axes.n), 0)
            FROM (SELECT "TableID", COUNT(*) AS n FROM "mTableCell" GROUP BY "TableID") cells
            JOIN (SELECT "TableID", COUNT(*) AS n FROM "mTableAxis" GROUP BY "TableID") axes ON axes."TableID" = cells."TableID"
            """);
        Assert.True(
            expected == positions,
            $"mCellPosition has {positions} rows, the sum over the cells of the number of axes of their table is {expected}.");
    }

    // ------------------------------------------------------------------
    // The four EXACT splits (they add up directly, without adjustment: they are the best check of
    // the closed/open axis separation).
    // ------------------------------------------------------------------

    [DataFact]
    public void TheAxisSplits_MatchThePinnedCensus_AndAddUpToMAxis()
    {
        var rows = QueryHelpers.Rows(
            fixture.GeneratedConnection,
            "SELECT \"AxisOrientation\", \"IsOpenAxis\", COUNT(*) FROM \"mAxis\" GROUP BY 1, 2",
            3);

        var counts = rows.ToDictionary(r => (Orientation: r[0]!, IsOpen: r[1]!), r => int.Parse(r[2]!));

        // Since issue 66 (taxonomy = framework + StartRelease of the module version) the pairs that the
        // old rule hung from taxonomies without a module are gone: every count of this test is the old one
        // minus the axes of those pairs (checked cell by cell against the old output, see ReleasePins).
        // After the abstract-closure rule: +1 on each closed one (X/Y/Z) for the three closed axes
        // of C_34.02.b/if 4.2 - the open ones (Y=241, Z=148) do not change, the new table
        // contributes no open axis. Before it: X=846, closed Y=718, closed Z=37.
        var pin = ReleasePins.Dpm20Cutoff42;
        ReleasePins.AssertPinned(pin, "closed X axes", ReleasePins.Dpm20Cutoff42ClosedXAxes, counts.GetValueOrDefault(("X", "0")));
        ReleasePins.AssertPinned(pin, "closed Y axes", ReleasePins.Dpm20Cutoff42ClosedYAxes, counts.GetValueOrDefault(("Y", "0")));
        ReleasePins.AssertPinned(pin, "open Y axes", ReleasePins.Dpm20Cutoff42OpenYAxes, counts.GetValueOrDefault(("Y", "1")));
        ReleasePins.AssertPinned(pin, "closed Z axes", ReleasePins.Dpm20Cutoff42ClosedZAxes, counts.GetValueOrDefault(("Z", "0")));
        ReleasePins.AssertPinned(pin, "open Z axes", ReleasePins.Dpm20Cutoff42OpenZAxes, counts.GetValueOrDefault(("Z", "1")));

        // Second measurement: every emitted table has exactly one closed X axis, so the closed X
        // axes are as many as the rows of mTable.
        var tables = ScalarLong("SELECT COUNT(*) FROM \"mTable\"");
        Assert.True(
            tables == counts.GetValueOrDefault(("X", "0")),
            $"mTable has {tables} rows but there are {counts.GetValueOrDefault(("X", "0"))} closed X axes: one per table was expected.");

        // No open X axis: T2 only produces a transposition of key X -> open Y and key Z -> open Z
        // (0 key headers with direction Y measured in the whole source).
        Assert.False(counts.ContainsKey(("X", "1")), "There is an open X axis: T2 does not contemplate that transposition.");

        // The five values exhaust mAxis: there is no sixth (orientation, openness) combination.
        var total = counts.Values.Sum();
        var axes = ScalarLong("SELECT COUNT(*) FROM \"mAxis\"");
        Assert.True(axes == total, $"The (orientation, openness) groups add up to {total} axes but mAxis has {axes} rows.");
    }

    // ------------------------------------------------------------------
    // THE T2 TRANSPOSITION: a key header with direction X produces an OPEN Y axis; a key header
    // with direction Z produces a Z axis. It is the first rule that someone would "correct"
    // thinking it is a typo (241/148 do not look like "their" orientations) - hence the test name
    // says it explicitly.
    // ------------------------------------------------------------------

    [DataFact]
    public void TheT2Transposition_KeyHeaderWithDirectionX_ProducesAnOpenYAxis_NotAnOpenXAxis()
    {
        var openYCount = ScalarLong("SELECT COUNT(*) FROM \"mAxis\" WHERE \"AxisOrientation\" = 'Y' AND \"IsOpenAxis\" = 1");
        var openXCount = ScalarLong("SELECT COUNT(*) FROM \"mAxis\" WHERE \"AxisOrientation\" = 'X' AND \"IsOpenAxis\" = 1");

        ReleasePins.AssertPinned(ReleasePins.Dpm20Cutoff42, "open Y axes", ReleasePins.Dpm20Cutoff42OpenYAxes, openYCount);
        Assert.True(openXCount == 0, $"{openXCount} open X axes: T2 only transposes to open Y and open Z.");
    }

    [DataFact]
    public void TheT2Transposition_KeyHeaderWithDirectionZ_ProducesAnOpenZAxis()
    {
        var openZCount = ScalarLong("SELECT COUNT(*) FROM \"mAxis\" WHERE \"AxisOrientation\" = 'Z' AND \"IsOpenAxis\" = 1");
        ReleasePins.AssertPinned(ReleasePins.Dpm20Cutoff42, "open Z axes", ReleasePins.Dpm20Cutoff42OpenZAxes, openZCount);
    }

    // ------------------------------------------------------------------
    // The pruning of abstract headers happens AFTER the transposition. Invariant: no abstract
    // header that was left without NON-KEY descendants reaches mAxisOrdinate - that is, no abstract
    // ordinate is a leaf of the tree (without children).
    // ------------------------------------------------------------------

    [DataFact]
    public void NoAbstractOrdinate_IsALeafOfTheTree_PruningHappensAfterTheTransposition()
    {
        var leafAbstracts = ScalarLong(
            """
            SELECT COUNT(*) FROM "mAxisOrdinate" o
            WHERE o."IsAbstractHeader" = 1
              AND NOT EXISTS (SELECT 1 FROM "mAxisOrdinate" c WHERE c."ParentOrdinateID" = o."OrdinateID")
            """);
        Assert.Equal(0, leafAbstracts);

        var abstractCount = ScalarLong("SELECT COUNT(*) FROM \"mAxisOrdinate\" WHERE \"IsAbstractHeader\" = 1");
        Assert.True(abstractCount > 0, "There is no abstract ordinate in the converted universe: the invariant would be vacuous.");
    }

    // ------------------------------------------------------------------
    // mTableCell IS the AXIS-BY-AXIS cartesian product of the CLOSED axes (abstracts included).
    // Internal invariant, without a reference: it is checked in all 847 tables (846 before the
    // abstract-closure rule), not only in a sample.
    // ------------------------------------------------------------------

    [DataFact]
    public void MTableCell_IsTheCartesianProductOfTheClosedAxes_InEveryTable()
    {
        var cellCountByTableId = QueryHelpers.Rows(
                fixture.GeneratedConnection, "SELECT \"TableID\", COUNT(*) FROM \"mTableCell\" GROUP BY \"TableID\"", 2)
            .ToDictionary(r => int.Parse(r[0]!), r => long.Parse(r[1]!));

        // "Every table" is measured, not assumed: the tables with cells are all the rows of mTable.
        var tables = ScalarLong("SELECT COUNT(*) FROM \"mTable\"");
        Assert.True(
            tables == cellCountByTableId.Count,
            $"mTable has {tables} rows but {cellCountByTableId.Count} tables have cells.");

        var closedAxisOrdinateCounts = QueryHelpers.Rows(
            fixture.GeneratedConnection,
            """
            SELECT ta."TableID", o."n" FROM "mTableAxis" ta
            JOIN "mAxis" a ON a."AxisID" = ta."AxisID"
            JOIN (SELECT "AxisID", COUNT(*) AS "n" FROM "mAxisOrdinate" GROUP BY "AxisID") o ON o."AxisID" = ta."AxisID"
            WHERE a."IsOpenAxis" = 0
            """,
            2);

        var expectedProductByTableId = new Dictionary<int, long>();
        foreach (var row in closedAxisOrdinateCounts)
        {
            var tableId = int.Parse(row[0]!);
            var n = long.Parse(row[1]!);
            expectedProductByTableId[tableId] = expectedProductByTableId.TryGetValue(tableId, out var current) ? current * n : n;
        }

        var mismatches = new List<string>();
        foreach (var (tableId, actualCells) in cellCountByTableId)
        {
            var expected = expectedProductByTableId.GetValueOrDefault(tableId, 1);
            if (expected != actualCells)
            {
                mismatches.Add($"TableID={tableId}: expected (product of closed axes)={expected}, cells emitted={actualCells}");
            }
        }

        Assert.True(
            mismatches.Count == 0,
            $"{mismatches.Count} of {tables} tables where mTableCell is NOT the cartesian product of its closed axes:\n"
            + string.Join("\n", mismatches.Take(20)));
    }

    // ------------------------------------------------------------------
    // mCellPosition: ONE position PER AXIS (open ones included, they contribute the fixed position
    // of their single ordinate), and ALL the cells covered - 0 cells without a position.
    // ------------------------------------------------------------------

    [DataFact]
    public void CellPosition_ASinglePositionPerAxis_WithoutDuplicates()
    {
        var duplicated = ScalarLong(
            """
            SELECT COUNT(*) FROM (
                SELECT cp."CellID", a."AxisID", COUNT(*) AS n
                FROM "mCellPosition" cp
                JOIN "mAxisOrdinate" o ON o."OrdinateID" = cp."OrdinateID"
                JOIN "mAxis" a ON a."AxisID" = o."AxisID"
                GROUP BY cp."CellID", a."AxisID"
                HAVING n > 1
            )
            """);
        Assert.Equal(0, duplicated);
    }

    // Every cell of mTableCell (pinned census, ReleasePins.Dpm20Cutoff42MTableCellRows).
    [DataFact]
    public void CellPosition_EveryCellHasExactlyOnePositionForEachAxisOfItsTable_0UncoveredCells()
    {
        var positionCountByCellId = QueryHelpers.Rows(
                fixture.GeneratedConnection,
                """
                SELECT c."CellID", c."TableID", COUNT(cp."CellID")
                FROM "mTableCell" c
                LEFT JOIN "mCellPosition" cp ON cp."CellID" = c."CellID"
                GROUP BY c."CellID"
                """,
                3)
            .Select(r => (CellId: int.Parse(r[0]!), TableId: int.Parse(r[1]!), Positions: long.Parse(r[2]!)))
            .ToList();

        ReleasePins.AssertPinned(ReleasePins.Dpm20Cutoff42, "cells examined", ReleasePins.Dpm20Cutoff42MTableCellRows, positionCountByCellId.Count);

        var axisCountByTableId = QueryHelpers.Rows(
                fixture.GeneratedConnection, "SELECT \"TableID\", COUNT(*) FROM \"mTableAxis\" GROUP BY \"TableID\"", 2)
            .ToDictionary(r => int.Parse(r[0]!), r => long.Parse(r[1]!));

        var uncovered = positionCountByCellId.Where(r => r.Positions == 0).ToList();
        Assert.True(uncovered.Count == 0, $"{uncovered.Count} cells without any position. Examples: {string.Join(",", uncovered.Take(10).Select(r => r.CellId))}");

        var mismatches = positionCountByCellId
            .Where(r => r.Positions != axisCountByTableId.GetValueOrDefault(r.TableId))
            .ToList();
        Assert.True(
            mismatches.Count == 0,
            $"{mismatches.Count} cells whose number of positions is not the number of axes of their table. Examples: "
            + string.Join(",", mismatches.Take(10).Select(r => $"CellID={r.CellId}: {r.Positions} positions, {axisCountByTableId.GetValueOrDefault(r.TableId)} axes")));
    }

    // ------------------------------------------------------------------
    // mOrdinateCategorisation: NEVER by count (we emit the closure, the reference emits the
    // minimisation). Internal invariants only.
    // ------------------------------------------------------------------

    [DataFact]
    public void OrdinateCategorisation_ZeroRowsWithADimensionIdThatDoesNotExistInMDimension()
    {
        // An inconsistency that is forbidden, and that was just fixed with the MET row (DimensionID
        // 9999) in mDimension: if that row were missing, this test would be the one to detect it.
        QueryHelpers.AssertNoOrphans(fixture.GeneratedConnection, "mOrdinateCategorisation", "DimensionID", "mDimension", "DimensionID");
    }

    [DataFact]
    public void OrdinateCategorisation_ZeroOrphanRowsByMemberIdOrOrdinateId()
    {
        QueryHelpers.AssertNoOrphans(fixture.GeneratedConnection, "mOrdinateCategorisation", "MemberID", "mMember", "MemberID");
        QueryHelpers.AssertNoOrphans(fixture.GeneratedConnection, "mOrdinateCategorisation", "OrdinateID", "mAxisOrdinate", "OrdinateID");
    }

    /// <summary>
    /// Deliberately kept as it is: this is still an exact count of a PART of
    /// <c>mOrdinateCategorisation</c>, which counting in general discourages. But the metric census is not
    /// the same kind of figure as the 21,952 rows that the one-dimension-one-axis rule removed - it
    /// is the census of source 3 (MET), which <c>Dpm20AxisAndCellLoader.BuildTablePlan</c> adds
    /// AFTER <c>ArbitrateDimensionAxisConflicts</c> and which never takes part in the arbitration
    /// between axes: the metric is structurally IMMUNE to that rule, and that is why the figure did
    /// not move a single number when it was applied (verified on the real conversion). The value
    /// pinned here does not chase the state of a defect: it is the sentinel that would detect the
    /// day the metric STARTS taking part in the arbitration - if that ever happens, this test must
    /// fail, and only then will it have to be reviewed.
    /// </summary>
    [DataFact]
    public void TheRowsOfDimension9999_MatchThePinnedCensus_AndPointToTheMetRow_WhichNowExists()
    {
        var metDimensionId = ScalarLong("SELECT \"DimensionID\" FROM \"mDimension\" WHERE \"DimensionCode\" = 'MET'");
        Assert.Equal(9999, metDimensionId); // measured value, literal (see Dpm20AxisAndCellLoader)

        // Source 3/MET of the ordinates (9,104 before the abstract-closure rule, +19 for the new
        // ordinates of C_34.02.b/if 4.2) - structurally immune to the one-dimension-one-axis rule,
        // see the comment of this test above, which remains valid with no change of substance.
        var rowsPointingToMet = ScalarLong($"SELECT COUNT(*) FROM \"mOrdinateCategorisation\" WHERE \"DimensionID\" = {metDimensionId}");
        ReleasePins.AssertPinned(ReleasePins.Dpm20Cutoff42, "mOrdinateCategorisation rows with the metric dimension", ReleasePins.Dpm20Cutoff42MetCategorisationRows, rowsPointingToMet);
    }

    // ------------------------------------------------------------------
    // The measured columns, without a single exception except the ones named in the mapping.
    // ------------------------------------------------------------------

    [DataFact]
    public void OptionalKey_IsAlwaysEqualToIsOpenAxis_WithoutException()
    {
        var mismatched = ScalarLong("SELECT COUNT(*) FROM \"mAxis\" WHERE \"OptionalKey\" IS NOT \"IsOpenAxis\"");
        Assert.Equal(0, mismatched);
    }

    [DataFact]
    public void AxisCode_OnlyOnTheOpenAxes_NullOnTheClosedOnes()
    {
        var closedWithCode = ScalarLong("SELECT COUNT(*) FROM \"mAxis\" WHERE \"IsOpenAxis\" = 0 AND \"AxisCode\" IS NOT NULL");
        Assert.Equal(0, closedWithCode);

        var openWithoutCode = ScalarLong("SELECT COUNT(*) FROM \"mAxis\" WHERE \"IsOpenAxis\" = 1 AND \"AxisCode\" IS NULL");
        Assert.Equal(0, openWithoutCode);

        var openCount = ScalarLong("SELECT COUNT(*) FROM \"mAxis\" WHERE \"IsOpenAxis\" = 1");
        var openY = ScalarLong("SELECT COUNT(*) FROM \"mAxis\" WHERE \"IsOpenAxis\" = 1 AND \"AxisOrientation\" = 'Y'");
        var openZ = ScalarLong("SELECT COUNT(*) FROM \"mAxis\" WHERE \"IsOpenAxis\" = 1 AND \"AxisOrientation\" = 'Z'");
        // T2 only produces open Y and open Z axes, so the open axes are exactly the two.
        Assert.True(openCount == openY + openZ, $"{openCount} open axes but {openY} open Y + {openZ} open Z.");
        ReleasePins.AssertPinned(
            ReleasePins.Dpm20Cutoff42,
            "open axes",
            ReleasePins.Dpm20Cutoff42OpenYAxes + ReleasePins.Dpm20Cutoff42OpenZAxes,
            openCount);
        var withCode = ScalarLong("SELECT COUNT(*) FROM \"mAxis\" WHERE \"AxisCode\" IS NOT NULL");
        Assert.True(withCode == openCount, $"{withCode} axes carry an AxisCode but {openCount} axes are open.");
    }

    [DataFact]
    public void Level_IsZeroInOpenAxisOrdinates_OneToSevenInTheTreeOfTheClosedOnes()
    {
        var openWithBadLevel = ScalarLong(
            """
            SELECT COUNT(*) FROM "mAxisOrdinate" o
            JOIN "mAxis" a ON a."AxisID" = o."AxisID"
            WHERE a."IsOpenAxis" = 1 AND o."Level" <> 0
            """);
        Assert.Equal(0, openWithBadLevel);

        var closedLevels = QueryHelpers.Rows(
            fixture.GeneratedConnection,
            """
            SELECT MIN(o."Level"), MAX(o."Level") FROM "mAxisOrdinate" o
            JOIN "mAxis" a ON a."AxisID" = o."AxisID"
            WHERE a."IsOpenAxis" = 0
            """,
            2).Single();

        Assert.Equal("1", closedLevels[0]);
        Assert.Equal("7", closedLevels[1]);
    }

    [DataFact]
    public void IsDisplayBeforeChildren_TypeOfKey_RelatedDimensionTableId_AreConstantInMAxisOrdinate()
    {
        var total = ScalarLong("SELECT COUNT(*) FROM \"mAxisOrdinate\"");
        ReleasePins.AssertPinned(ReleasePins.Dpm20Cutoff42, "mAxisOrdinate rows examined", ReleasePins.Dpm20Cutoff42MAxisOrdinateRows, total);

        Assert.Equal(0, ScalarLong("SELECT COUNT(*) FROM \"mAxisOrdinate\" WHERE \"IsDisplayBeforeChildren\" IS NOT 0"));
        Assert.Equal(0, ScalarLong("SELECT COUNT(*) FROM \"mAxisOrdinate\" WHERE \"TypeOfKey\" IS NOT NULL"));
        Assert.Equal(0, ScalarLong("SELECT COUNT(*) FROM \"mAxisOrdinate\" WHERE \"RelatedDimensionTableId\" IS NOT NULL"));
    }

    [DataFact]
    public void TableCell_IsRowKey_IsConstant0_InEveryRow()
    {
        var total = ScalarLong("SELECT COUNT(*) FROM \"mTableCell\"");
        ReleasePins.AssertPinned(ReleasePins.Dpm20Cutoff42, "mTableCell rows examined", ReleasePins.Dpm20Cutoff42MTableCellRows, total);

        var notZero = ScalarLong("SELECT COUNT(*) FROM \"mTableCell\" WHERE \"IsRowKey\" IS NOT 0");
        Assert.Equal(0, notZero);
    }

    [DataFact]
    public void OrdinateCategorisation_Source_IsAlwaysNull()
    {
        var total = ScalarLong("SELECT COUNT(*) FROM \"mOrdinateCategorisation\"");
        Assert.True(total > 0, $"mOrdinateCategorisation has {total} rows: the invariant would be empty (not counted by design, see above).");

        var populated = ScalarLong("SELECT COUNT(*) FROM \"mOrdinateCategorisation\" WHERE \"Source\" IS NOT NULL");
        Assert.Equal(0, populated);
    }

    // ------------------------------------------------------------------
    // This CLOSES the gap that this test used to document: BusinessCode is now emitted in all the
    // rows, and DatapointSignature/DPS in the NON-shaded cells (the rest, the shaded ones, are
    // NULL by design: a cell with IsShaded=1 has a NULL signature). The full verification of the
    // algorithm's invariants and the comparison against the 4.2 reference lives in
    // <see cref="Dpm20SignatureTests"/>; here it is only recorded that the declared gap no longer
    // exists, so that nobody reintroduces the old assertion.
    // ------------------------------------------------------------------

    [DataFact]
    public void BusinessCodeAndCellSignatures_AreNoLongerNull()
    {
        var total = ScalarLong("SELECT COUNT(*) FROM \"mTableCell\"");
        ReleasePins.AssertPinned(ReleasePins.Dpm20Cutoff42, "mTableCell rows examined", ReleasePins.Dpm20Cutoff42MTableCellRows, total);

        var withoutBusinessCode = ScalarLong("SELECT COUNT(*) FROM \"mTableCell\" WHERE \"BusinessCode\" IS NULL");
        Assert.Equal(0, withoutBusinessCode);

        var unshadedWithoutSignature = ScalarLong(
            "SELECT COUNT(*) FROM \"mTableCell\" WHERE \"IsShaded\" = 0 AND (\"DatapointSignature\" IS NULL OR \"DPS\" IS NULL)");
        Assert.Equal(0, unshadedWithoutSignature);

        var shadedWithSignature = ScalarLong(
            "SELECT COUNT(*) FROM \"mTableCell\" WHERE \"IsShaded\" = 1 AND (\"DatapointSignature\" IS NOT NULL OR \"DPS\" IS NOT NULL)");
        Assert.Equal(0, shadedWithSignature);
    }

    // ------------------------------------------------------------------
    // This CLOSES the gap that this test used to document: mOpenAxisValueRestriction is NO longer
    // empty (see ReleasePins.Dpm20Cutoff42OpenAxisValueRestrictionRows). The full verification - invariants, contract, business key against the
    // reference and the signature bracket - lives in <see cref="Dpm20OpenAxisValueRestrictionTests"/>;
    // here it is only recorded that the declared gap no longer exists, so that nobody
    // reintroduces the old assertion.
    // ------------------------------------------------------------------

    [DataFact]
    public void MOpenAxisValueRestriction_IsNoLongerEmpty()
    {
        ReleasePins.AssertPinned(
            ReleasePins.Dpm20Cutoff42,
            "mOpenAxisValueRestriction rows",
            ReleasePins.Dpm20Cutoff42OpenAxisValueRestrictionRows,
            CountRows("mOpenAxisValueRestriction"));
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private long CountRows(string table)
    {
        using var command = fixture.GeneratedConnection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM \"{table}\"";
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private long ScalarLong(string sql) => QueryHelpers.Scalar(fixture.GeneratedConnection, sql);
}
