using EbaDpm.Converter.Core.Mapping;
using EbaDpm.Converter.Tests.Dictionary;

namespace EbaDpm.Converter.Tests.AxesAndCells;

/// <summary>
/// STRUCTURAL invariants of <c>mTableCell</c>, <c>mCellPosition</c>,
/// <c>mOrdinateCategorisation</c> and <c>mOpenAxisValueRestriction</c>, checked WITHOUT looking at
/// any reference database.
///
/// Critical acceptance layer. Includes the "free" invariant: abstract header => every cell that
/// touches it is shaded (89,930/89,930 in the four official references).
/// </summary>
[Collection("AxesAndCells")]
[Trait("Tier", "RealData")]
public sealed class CellAndCategorisationStructuralTests
{
    private readonly AxisAndCellFixture _fixture;

    public CellAndCategorisationStructuralTests(AxisAndCellFixture fixture)
    {
        _fixture = fixture;
    }

    // ------------------------------------------------------------------
    // mCellPosition
    // ------------------------------------------------------------------

    [DataFact]
    public void CellPosition_HasNoOrphans_InEitherDirection()
    {
        QueryHelpers.AssertNoOrphans(_fixture.GeneratedConnection, "mCellPosition", "CellID", "mTableCell", "CellID");
        QueryHelpers.AssertNoOrphans(_fixture.GeneratedConnection, "mCellPosition", "OrdinateID", "mAxisOrdinate", "OrdinateID");
    }

    [DataFact]
    public void EveryEmittedCell_HasAtLeastOnePositionOnX_AndOnY()
    {
        var missing = QueryHelpers.Scalar(
            _fixture.GeneratedConnection,
            """
            SELECT COUNT(*) FROM "mTableCell" c
            WHERE NOT EXISTS (
                SELECT 1 FROM "mCellPosition" cp JOIN "mAxisOrdinate" o ON o."OrdinateID" = cp."OrdinateID"
                JOIN "mAxis" a ON a."AxisID" = o."AxisID"
                WHERE cp."CellID" = c."CellID" AND a."AxisOrientation" = 'X'
            )
            OR NOT EXISTS (
                SELECT 1 FROM "mCellPosition" cp JOIN "mAxisOrdinate" o ON o."OrdinateID" = cp."OrdinateID"
                JOIN "mAxis" a ON a."AxisID" = o."AxisID"
                WHERE cp."CellID" = c."CellID" AND a."AxisOrientation" = 'Y'
            )
            """);
        Assert.Equal(0, missing);
    }

    [DataFact]
    public void EveryPosition_HasExactlyOnePositionPerAxisItParticipatesIn()
    {
        var bad = QueryHelpers.Scalar(
            _fixture.GeneratedConnection,
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
        Assert.Equal(0, bad);
    }

    [DataFact]
    public void ThePositionsOrdinate_BelongsToAnAxisOfTheSameTableAsTheCell()
    {
        var bad = QueryHelpers.Scalar(
            _fixture.GeneratedConnection,
            """
            SELECT COUNT(*) FROM "mCellPosition" cp
            JOIN "mTableCell" c   ON c."CellID"     = cp."CellID"
            JOIN "mAxisOrdinate" o ON o."OrdinateID" = cp."OrdinateID"
            JOIN "mAxis" a         ON a."AxisID"     = o."AxisID"
            WHERE NOT EXISTS (
                SELECT 1 FROM "mTableAxis" ta WHERE ta."AxisID" = a."AxisID" AND ta."TableID" = c."TableID"
            )
            """);
        Assert.Equal(0, bad);
    }

    [DataFact]
    public void NoCellPosition_ReferencesAnXAxisOrdinateWithIsRowKeySet()
    {
        // The Access X columns with IsRowKey=1 stop being columns and become open Y axes; cells
        // DO legitimately reference those open Y/Z axes, which carry IsRowKey=1 by construction,
        // so that is not an anomaly. What must never happen is an IsRowKey ordinate surviving on
        // the X axis.
        var bad = QueryHelpers.Scalar(
            _fixture.GeneratedConnection,
            """
            SELECT COUNT(*) FROM "mCellPosition" cp
            JOIN "mAxisOrdinate" o ON o."OrdinateID" = cp."OrdinateID"
            JOIN "mAxis" a         ON a."AxisID"     = o."AxisID"
            WHERE o."IsRowKey" = 1 AND a."AxisOrientation" = 'X'
            """);
        Assert.Equal(0, bad);
    }

    [DataFact]
    public void AbstractOrdinatePositions_AreRetained_NotFiltered()
    {
        // A "leaves only" filter would be an error: there must be positions on abstract
        // ordinates in the converted universe.
        var abstractPositions = QueryHelpers.Scalar(
            _fixture.GeneratedConnection,
            """
            SELECT COUNT(*) FROM "mCellPosition" cp
            JOIN "mAxisOrdinate" o ON o."OrdinateID" = cp."OrdinateID"
            WHERE o."IsAbstractHeader" = 1
            """);
        Assert.True(abstractPositions > 0, "There is no position on an abstract ordinate: was it filtered out by mistake?");
    }

    // ------------------------------------------------------------------
    // "Free" invariant: abstract header => every cell that touches it is shaded
    // (89,930/89,930 in the four official references; here, over the generated output).
    // ------------------------------------------------------------------

    [DataFact]
    public void EveryCellTouchingAnAbstractHeader_IsShaded()
    {
        var violating = QueryHelpers.Scalar(
            _fixture.GeneratedConnection,
            """
            SELECT COUNT(DISTINCT c."CellID") FROM "mTableCell" c
            JOIN "mCellPosition" cp ON cp."CellID" = c."CellID"
            JOIN "mAxisOrdinate" o  ON o."OrdinateID" = cp."OrdinateID"
            WHERE o."IsAbstractHeader" = 1 AND c."IsShaded" = 0
            """);
        Assert.Equal(0, violating);

        var touchingCount = QueryHelpers.Scalar(
            _fixture.GeneratedConnection,
            """
            SELECT COUNT(DISTINCT c."CellID") FROM "mTableCell" c
            JOIN "mCellPosition" cp ON cp."CellID" = c."CellID"
            JOIN "mAxisOrdinate" o  ON o."OrdinateID" = cp."OrdinateID"
            WHERE o."IsAbstractHeader" = 1
            """);
        Assert.True(touchingCount > 0, "No cell touches an abstract header: the invariant would be vacuous.");
    }

    // ------------------------------------------------------------------
    // BusinessCode <=> mCellPosition: this cross-check is EXACT over the whole Access universe
    // (904,204/904,204). Here it is reproduced ONLY with the generated output, without any
    // reference: both paths must coincide byte for byte.
    // ------------------------------------------------------------------

    [DataFact]
    public void BusinessCode_ReconstructedFromCellPosition_MatchesTheEmittedOne_Exactly()
    {
        var cells = QueryHelpers.Rows(
            _fixture.GeneratedConnection,
            "SELECT c.\"CellID\", c.\"TableID\", c.\"BusinessCode\", tab.\"TableCode\" FROM \"mTableCell\" c JOIN \"mTable\" tab ON tab.\"TableID\" = c.\"TableID\"",
            4);
        Assert.True(cells.Count > 0);

        var positions = QueryHelpers.Rows(
            _fixture.GeneratedConnection,
            """
            SELECT cp."CellID", a."AxisOrientation", o."OrdinateCode" FROM "mCellPosition" cp
            JOIN "mAxisOrdinate" o ON o."OrdinateID" = cp."OrdinateID"
            JOIN "mAxis" a         ON a."AxisID"     = o."AxisID"
            WHERE a."IsOpenAxis" = 0
            """,
            3);

        var positionsByCell = positions
            .GroupBy(r => int.Parse(r[0]!))
            .ToDictionary(g => g.Key, g => g.Select(r => (Orientation: r[1]!, Code: r[2]!)).ToList());

        var orientationOrder = new Dictionary<string, int> { ["Y"] = 0, ["X"] = 1, ["Z"] = 2 };
        var failures = new List<string>();

        foreach (var row in cells)
        {
            var cellId = int.Parse(row[0]!);
            var businessCode = row[2]!;
            var tableCode = row[3]!;

            var coords = positionsByCell.TryGetValue(cellId, out var list)
                ? list.OrderBy(p => orientationOrder[p.Orientation]).Select(p => p.Code).ToList()
                : [];

            var reconstructed = "{" + tableCode + "," + string.Join(",", coords) + "}";

            if (!string.Equals(reconstructed, businessCode, StringComparison.Ordinal))
            {
                failures.Add($"CellID={cellId}: rebuilt='{reconstructed}' emitted='{businessCode}'");
            }
        }

        Assert.True(failures.Count == 0, $"{failures.Count} cells where BusinessCode and mCellPosition do not match:\n" + string.Join("\n", failures.Take(20)));
    }

    // ------------------------------------------------------------------
    // mTableCell
    // ------------------------------------------------------------------

    [DataFact]
    public void BusinessCode_IsNeverNullOrEmpty()
    {
        var bad = QueryHelpers.Scalar(
            _fixture.GeneratedConnection,
            "SELECT COUNT(*) FROM \"mTableCell\" WHERE \"BusinessCode\" IS NULL OR TRIM(\"BusinessCode\") = ''");
        Assert.Equal(0, bad);
    }

    [DataFact]
    public void TableCell_IsRowKey_IsConstant_AcrossAllRows()
    {
        var rows = QueryHelpers.Rows(_fixture.GeneratedConnection, "SELECT DISTINCT \"IsRowKey\" FROM \"mTableCell\"", 1);
        Assert.Single(rows);
        Assert.Equal("0", rows[0][0]);
    }

    [DataFact]
    public void TableCell_TableID_HasNoOrphans() =>
        QueryHelpers.AssertNoOrphans(_fixture.GeneratedConnection, "mTableCell", "TableID", "mTable", "TableID");

    [DataFact]
    public void DpsIsNull_IfAndOnlyIf_IsShaded()
    {
        // The exact invariant in the four references is DPS IS NULL <=> IsShaded = 1. It is
        // checked here WITHOUT looking at any reference, over the generated output, for both
        // signature columns of mTableCell.
        var total = QueryHelpers.Scalar(_fixture.GeneratedConnection, "SELECT COUNT(*) FROM \"mTableCell\"");
        Assert.True(total > 0);

        var violatingDps = QueryHelpers.Scalar(
            _fixture.GeneratedConnection,
            "SELECT COUNT(*) FROM \"mTableCell\" WHERE (\"DPS\" IS NULL) IS NOT (\"IsShaded\" = 1)");
        Assert.Equal(0, violatingDps);

        var violatingDatapointSignature = QueryHelpers.Scalar(
            _fixture.GeneratedConnection,
            "SELECT COUNT(*) FROM \"mTableCell\" WHERE (\"DatapointSignature\" IS NULL) IS NOT (\"IsShaded\" = 1)");
        Assert.Equal(0, violatingDatapointSignature);

        var shadedCount = QueryHelpers.Scalar(_fixture.GeneratedConnection, "SELECT COUNT(*) FROM \"mTableCell\" WHERE \"IsShaded\" = 1");
        var unshadedCount = QueryHelpers.Scalar(_fixture.GeneratedConnection, "SELECT COUNT(*) FROM \"mTableCell\" WHERE \"IsShaded\" = 0");
        Assert.True(shadedCount > 0, "There is no shaded cell: the invariant would be vacuous on that side.");
        Assert.True(unshadedCount > 0, "There is no unshaded cell: the invariant would be vacuous on that side.");
    }

    [DataFact]
    public void NoSignatureColumn_IsEverEmptyString()
    {
        // No empty signature ('') in any of the four columns: NULL is allowed, but never an
        // empty string. mOrdinateCategorisation.DPS/DimensionMemberSignature are never NULL
        // (every categorisation has a signature); mTableCell.DPS/DatapointSignature are NULL only
        // if IsShaded=1 (checked in DpsIsNull_IfAndOnlyIf_IsShaded).
        var emptyInCategorisation = QueryHelpers.Scalar(
            _fixture.GeneratedConnection,
            """
            SELECT COUNT(*) FROM "mOrdinateCategorisation"
            WHERE "DPS" = '' OR "DimensionMemberSignature" = '' OR "DPS" IS NULL OR "DimensionMemberSignature" IS NULL
            """);
        Assert.Equal(0, emptyInCategorisation);

        var emptyInCell = QueryHelpers.Scalar(
            _fixture.GeneratedConnection,
            "SELECT COUNT(*) FROM \"mTableCell\" WHERE \"DPS\" = '' OR \"DatapointSignature\" = ''");
        Assert.Equal(0, emptyInCell);
    }

    // ------------------------------------------------------------------
    // mOrdinateCategorisation
    // ------------------------------------------------------------------

    [DataFact]
    public void Source_IsAlwaysNull()
    {
        var total = QueryHelpers.Scalar(_fixture.GeneratedConnection, "SELECT COUNT(*) FROM \"mOrdinateCategorisation\"");
        var populated = QueryHelpers.Scalar(_fixture.GeneratedConnection, "SELECT COUNT(*) FROM \"mOrdinateCategorisation\" WHERE \"Source\" IS NOT NULL");
        Assert.True(total > 0);
        Assert.Equal(0, populated);
    }

    [DataFact]
    public void EveryOpenAxis_HasExactlyOneCategorisationRow_WithTheSentinelMember9999()
    {
        var openAxisCount = QueryHelpers.Scalar(_fixture.GeneratedConnection, "SELECT COUNT(*) FROM \"mAxis\" WHERE \"IsOpenAxis\" = 1");
        var sentinelRows = QueryHelpers.Scalar(_fixture.GeneratedConnection, "SELECT COUNT(*) FROM \"mOrdinateCategorisation\" WHERE \"MemberID\" = 9999");
        Assert.True(openAxisCount > 0);
        Assert.Equal(openAxisCount, sentinelRows);

        var openWithoutSentinel = QueryHelpers.Scalar(
            _fixture.GeneratedConnection,
            """
            SELECT COUNT(*) FROM "mAxis" a
            JOIN "mAxisOrdinate" o ON o."AxisID" = a."AxisID"
            WHERE a."IsOpenAxis" = 1
              AND NOT EXISTS (SELECT 1 FROM "mOrdinateCategorisation" oc WHERE oc."OrdinateID" = o."OrdinateID" AND oc."MemberID" = 9999)
            """);
        Assert.Equal(0, openWithoutSentinel);
    }

    [DataFact]
    public void NoCategorisationRow_UsesTheAccessSentinel_999()
    {
        var count = QueryHelpers.Scalar(_fixture.GeneratedConnection, "SELECT COUNT(*) FROM \"mOrdinateCategorisation\" WHERE \"MemberID\" = 999");
        Assert.Equal(0, count);
    }

    [DataFact]
    public void OrdinateCategorisation_HasNoOrphans()
    {
        QueryHelpers.AssertNoOrphans(_fixture.GeneratedConnection, "mOrdinateCategorisation", "OrdinateID", "mAxisOrdinate", "OrdinateID");
        QueryHelpers.AssertNoOrphans(_fixture.GeneratedConnection, "mOrdinateCategorisation", "DimensionID", "mDimension", "DimensionID");
        QueryHelpers.AssertNoOrphans(_fixture.GeneratedConnection, "mOrdinateCategorisation", "MemberID", "mMember", "MemberID");
        QueryHelpers.AssertNoOrphans(_fixture.GeneratedConnection, "mMember", "DomainID", "mDomain", "DomainID");
    }

    [DataFact]
    public void OrdinateCategorisation_PrimaryKey_NeverCollides()
    {
        var total = QueryHelpers.Scalar(_fixture.GeneratedConnection, "SELECT COUNT(*) FROM \"mOrdinateCategorisation\"");
        var distinctKeys = QueryHelpers.Scalar(
            _fixture.GeneratedConnection,
            "SELECT COUNT(*) FROM (SELECT DISTINCT \"OrdinateID\", \"DimensionID\" FROM \"mOrdinateCategorisation\")");
        Assert.Equal(total, distinctKeys);
    }

    // ------------------------------------------------------------------
    // Impossible default-member resets are skipped, counted and reported in the result. For THIS
    // universe (the same eight measured taxonomies) the exact figure 0 is asserted. It is not
    // generalised to other universes.
    // ------------------------------------------------------------------

    [DataFact]
    public void SkippedDefaultMemberResets_IsZero_OnThisMeasuredUniverse()
    {
        Assert.Equal(0, _fixture.AxisAndCellResult.SkippedDefaultMemberResets);
    }

    // ------------------------------------------------------------------
    // mOpenAxisValueRestriction
    // ------------------------------------------------------------------

    [DataFact]
    public void OpenAxisValueRestriction_HasAtMostOneRowPerAxis_AndNeverOnAClosedAxis()
    {
        var duplicated = QueryHelpers.Scalar(
            _fixture.GeneratedConnection,
            "SELECT COUNT(*) FROM (SELECT \"AxisID\" FROM \"mOpenAxisValueRestriction\" GROUP BY \"AxisID\" HAVING COUNT(*) > 1)");
        Assert.Equal(0, duplicated);

        var onClosedAxis = QueryHelpers.Scalar(
            _fixture.GeneratedConnection,
            """
            SELECT COUNT(*) FROM "mOpenAxisValueRestriction" r
            JOIN "mAxis" a ON a."AxisID" = r."AxisID"
            WHERE a."IsOpenAxis" = 0
            """);
        Assert.Equal(0, onClosedAxis);
    }

    [DataFact]
    public void OpenAxisRestriction_ExistsIfAndOnlyIfTheOpenDimensionIsUntyped()
    {
        // Restriction <=> UNTYPED domain (1,405/1,405 over the whole Access database). Reproduced
        // here over the generated output, via the open dimension of each open axis (the one in
        // the categorisation with MemberID = 9999).
        var rows = QueryHelpers.Rows(
            _fixture.GeneratedConnection,
            """
            SELECT a."AxisID", dom."IsTypedDomain",
                   EXISTS(SELECT 1 FROM "mOpenAxisValueRestriction" r WHERE r."AxisID" = a."AxisID") AS "HasRestriction"
            FROM "mAxis" a
            JOIN "mAxisOrdinate" o  ON o."AxisID" = a."AxisID"
            JOIN "mOrdinateCategorisation" oc ON oc."OrdinateID" = o."OrdinateID" AND oc."MemberID" = 9999
            JOIN "mDimension" dim ON dim."DimensionID" = oc."DimensionID"
            JOIN "mDomain" dom    ON dom."DomainID"     = dim."DomainID"
            WHERE a."IsOpenAxis" = 1
            """,
            3);
        Assert.True(rows.Count > 0);

        var failures = rows
            .Select(r => (AxisId: r[0], IsTyped: r[1] == "1", HasRestriction: r[2] == "1"))
            .Where(r => r.IsTyped == r.HasRestriction) // they must be opposite: typed -> NO restriction
            .ToList();

        Assert.True(failures.Count == 0, $"{failures.Count} open axes violate 'restriction <=> untyped domain'. Examples: {string.Join(",", failures.Take(10).Select(f => f.AxisId))}");
    }

    [DataFact]
    public void IsStartingMemberIncluded_IsNull_IfAndOnlyIf_HierarchyStartingMemberIdIsNull()
    {
        var bad = QueryHelpers.Scalar(
            _fixture.GeneratedConnection,
            """
            SELECT COUNT(*) FROM "mOpenAxisValueRestriction"
            WHERE ("HierarchyStartingMemberID" IS NULL) IS NOT ("IsStartingMemberIncluded" IS NULL)
            """);
        Assert.Equal(0, bad);
    }

    // ------------------------------------------------------------------
    // Type guard (compiles against the result record, to detect signature changes of
    // AxisAndCellLoader.Result at build time).
    // ------------------------------------------------------------------

    [DataFact]
    public void Result_ExposesAllExpectedCounters()
    {
        AxisAndCellLoader.Result result = _fixture.AxisAndCellResult;
        Assert.True(result.AxisRows >= 0);
        Assert.True(result.TableAxisRows >= 0);
        Assert.True(result.AxisOrdinateRows >= 0);
        Assert.True(result.OrdinateCategorisationRows >= 0);
        Assert.True(result.TableCellRows >= 0);
        Assert.True(result.CellPositionRows >= 0);
        Assert.True(result.OpenAxisValueRestrictionRows >= 0);
        Assert.True(result.SkippedDefaultMemberResets >= 0);
        Assert.True(result.MetricInheritedFromAncestorCellCount >= 0);
    }
}
