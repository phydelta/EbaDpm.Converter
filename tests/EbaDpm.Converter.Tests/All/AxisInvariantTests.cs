using EbaDpm.Converter.Tests.Dictionary;
using EbaDpm.Converter.Tests.Infrastructure;

namespace EbaDpm.Converter.Tests.All;

/// <summary>Axis invariants A-AXE-04 and A-AXE-16, over <c>--all</c>.</summary>
[Collection("All")]
[Trait("Tier", "RealData")]
public sealed class AxisInvariantTests
{
    private readonly AllFixture _fixture;

    public AxisInvariantTests(AllFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// A-AXE-04: every table has EXACTLY one closed X axis and AT MOST one closed Y axis.
    /// A table may have SEVERAL open Y axes and some tables have no closed Y axis at all. Both
    /// figures are pinned (<see cref="ReleasePins.Dpm10All41"/>) because they are the evidence
    /// that the rule is NOT "every table has a closed Y axis".
    /// </summary>
    [DataFact]
    public void EveryTable_HasExactlyOneClosedXAxis_AndAtMostOneClosedYAxis()
    {
        var tablesWithoutExactlyOneClosedX = QueryHelpers.Scalar(
            _fixture.GeneratedConnection,
            """
            SELECT COUNT(*) FROM (
                SELECT ta."TableID", COUNT(*) AS Cnt
                FROM "mTableAxis" ta
                JOIN "mAxis" a ON a."AxisID" = ta."AxisID"
                WHERE a."AxisOrientation" = 'X' AND a."IsOpenAxis" = 0
                GROUP BY ta."TableID"
                HAVING COUNT(*) <> 1
            )
            """);
        var tablesWithZeroClosedX = QueryHelpers.Scalar(
            _fixture.GeneratedConnection,
            """
            SELECT COUNT(*) FROM "mTable" t
            WHERE NOT EXISTS (
                SELECT 1 FROM "mTableAxis" ta JOIN "mAxis" a ON a."AxisID" = ta."AxisID"
                WHERE ta."TableID" = t."TableID" AND a."AxisOrientation" = 'X' AND a."IsOpenAxis" = 0
            )
            """);

        Assert.True(tablesWithoutExactlyOneClosedX == 0, $"{tablesWithoutExactlyOneClosedX} tables with a number of closed X axes other than 1 (A-AXE-04).");
        Assert.True(tablesWithZeroClosedX == 0, $"{tablesWithZeroClosedX} tables with NO closed X axis (A-AXE-04).");

        var tablesWithMoreThanOneClosedY = QueryHelpers.Scalar(
            _fixture.GeneratedConnection,
            """
            SELECT COUNT(*) FROM (
                SELECT ta."TableID", COUNT(*) AS Cnt
                FROM "mTableAxis" ta
                JOIN "mAxis" a ON a."AxisID" = ta."AxisID"
                WHERE a."AxisOrientation" = 'Y' AND a."IsOpenAxis" = 0
                GROUP BY ta."TableID"
                HAVING COUNT(*) > 1
            )
            """);
        Assert.True(tablesWithMoreThanOneClosedY == 0, $"{tablesWithMoreThanOneClosedY} tables with more than one closed Y axis (A-AXE-04).");
    }

    /// <summary>Control figures pinned over --all (A-AXE-04, <see cref="ReleasePins.Dpm10All41"/>): tables with several open Y axes, and tables without a closed Y axis.</summary>
    [DataFact]
    public void OpenYAxes_And_MissingClosedYAxis_MatchThePinnedCensus()
    {
        var tablesWithSeveralOpenY = QueryHelpers.Scalar(
            _fixture.GeneratedConnection,
            """
            SELECT COUNT(*) FROM (
                SELECT ta."TableID", COUNT(*) AS Cnt
                FROM "mTableAxis" ta
                JOIN "mAxis" a ON a."AxisID" = ta."AxisID"
                WHERE a."AxisOrientation" = 'Y' AND a."IsOpenAxis" = 1
                GROUP BY ta."TableID"
                HAVING COUNT(*) > 1
            )
            """);

        var tablesWithoutClosedY = QueryHelpers.Scalar(
            _fixture.GeneratedConnection,
            """
            SELECT COUNT(*) FROM "mTable" t
            WHERE NOT EXISTS (
                SELECT 1 FROM "mTableAxis" ta JOIN "mAxis" a ON a."AxisID" = ta."AxisID"
                WHERE ta."TableID" = t."TableID" AND a."AxisOrientation" = 'Y' AND a."IsOpenAxis" = 0
            )
            """);

        ReleasePins.AssertPinned(
            ReleasePins.Dpm10All41, "tables with several open Y axes (A-AXE-04)", ReleasePins.Dpm10All41TablesWithSeveralOpenYAxes, tablesWithSeveralOpenY);
        ReleasePins.AssertPinned(
            ReleasePins.Dpm10All41, "tables without a closed Y axis (A-AXE-04)", ReleasePins.Dpm10All41TablesWithoutClosedYAxis, tablesWithoutClosedY);
    }

    /// <summary>A-AXE-16 (second half): no NON-abstract ordinate is left without an OrdinateCode.</summary>
    [DataFact]
    public void NoNonAbstractOrdinate_IsWithoutACode()
    {
        var withoutCode = QueryHelpers.Scalar(
            _fixture.GeneratedConnection,
            "SELECT COUNT(*) FROM \"mAxisOrdinate\" WHERE \"IsAbstractHeader\" = 0 AND (\"OrdinateCode\" IS NULL OR TRIM(\"OrdinateCode\") = '')");

        Assert.True(withoutCode == 0, $"{withoutCode} NON-abstract ordinates without an OrdinateCode (A-AXE-16).");
    }
}
