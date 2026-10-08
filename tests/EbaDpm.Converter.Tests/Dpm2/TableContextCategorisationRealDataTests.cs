using EbaDpm.Converter.Tests.Dictionary;

namespace EbaDpm.Converter.Tests.Dpm2;

/// <summary>
/// Permanent test over the real <c>--all</c> DPM 2.0 4.2 corpus. <c>TableVersion.ContextID</c>
/// declares (dimension, member) pairs that hold for the WHOLE table, and <c>Dpm20AxisAndCellLoader</c>
/// used to read them ONLY as an axis arbiter (<c>DetermineDimensionOwnerAxes</c>), never as a source
/// of <c>mOrdinateCategorisation</c> pairs - a dead read. They are now emitted on ALL the CLOSED
/// ordinates of the owner axis that do not already have an OWN value for that dimension
/// (<c>TryAdd</c>).
///
/// The measured case: <c>eba_dim_3.4:CAL</c> (<c>DimensionCode='CAL'</c>), member
/// <c>eba_AP:x252</c> (<c>MemberCode='x252'</c>) in <c>irrbb 4.2</c> - the ONLY live pair of the 36
/// that remained in <c>B-OCA-01</c> after the earlier fixes (the other 124 were already closed).
/// The owner axis is X (columns) of <c>J_05.00.a</c>, <c>J_06.00.a</c> and <c>J_07.00.a</c>.
///
/// CAL/x252 is NOT exclusive to this rule: the axis already carried that pair on 27 of the 45
/// ordinates through its own source (cells with real data), and this rule only completes the
/// remaining 18 - those that are TOTALLY shaded (0 live cells). A test that asserted "0 live cells
/// on ANY ordinate with CAL/x252" would be FALSE (there are 3,564 live cells spread over those 27
/// per table, plus others in <c>J_08.00.b</c>/<c>J_09.00.b</c>, unrelated to this rule). The right
/// containment invariant is restricted to the population this rule can touch: the TOTALLY SHADED
/// ordinates, identified WITHOUT looking at CAL at all (totally shaded is a prior, independent
/// structural property) - by construction, that population cannot have a live cell, and that is
/// exactly where, and only where, this rule had something to fill in.
///
/// No publication census figures are pinned ("+255", "847"...) - PROPERTIES are asserted:
/// "it exists", "it covers 100 % of the owner axis", "every totally shaded ordinate carries the
/// pair". Positive control in each test: if the examined population were 0, the rest of the
/// assertion would prove nothing.
/// </summary>
[Collection("Dpm2Skeleton")]
[Trait("Tier", "RealData")]
public sealed class TableContextCategorisationRealDataTests(Dpm20SkeletonFixture fixture)
{
    private static readonly string[] OwnerAxisTableCodes = ["J_05.00.a", "J_06.00.a", "J_07.00.a"];

    /// <summary>
    /// A CLOSED ordinate without own cells of the owner axis carries the table context pair. The
    /// PROPERTY is asserted - the pair covers 100 % of the ordinates of the X axis (not a sample nor
    /// a specific ordinate) - in the THREE named tables, all located by BUSINESS KEY
    /// (<c>TaxonomyCode</c>/<c>TableCode</c>/<c>AxisOrientation</c>), never by ID. Coverage includes
    /// both the ordinates that ALREADY had the pair through their own cell (27 of 45) and those this
    /// rule completes (18 of 45, see the containment test below).
    /// </summary>
    [DataFact]
    public void CalX252_CoversOneHundredPercentOfTheXAxisOrdinatesInJ0500aJ0600aJ0700a()
    {
        var connection = fixture.GeneratedConnection;

        foreach (var tableCode in OwnerAxisTableCodes)
        {
            var axisId = SingleOwnerXAxisId(connection, tableCode);

            var totalOrdinates = QueryHelpers.Scalar(connection, $"SELECT COUNT(*) FROM \"mAxisOrdinate\" WHERE \"AxisID\" = {axisId}");
            Assert.True(totalOrdinates > 0, $"{tableCode}: 0 ordinates on the X axis - nothing to check.");

            var ordinatesWithCalX252 = QueryHelpers.Scalar(connection, CalX252CategorisationCountSql($"ao.\"AxisID\" = {axisId}"));

            Assert.Equal(totalOrdinates, ordinatesWithCalX252); // 100 % of the axis, not a sample.
        }
    }

    /// <summary>
    /// THE CONTAINMENT INVARIANT, restricted to the CORRECT population - the TOTALLY SHADED
    /// ordinates of the owner axis (0 live cells among theirs), identified WITHOUT looking at CAL at
    /// all (so that the selection of the population is not circular). It is precisely this
    /// population, and only it, where this rule could have something to add (the other 27 of 45
    /// already carried the pair through their own cell and may legitimately be live - see the class
    /// comment). Asserted: (a) positive control - the population is NOT empty; (b) the context pair
    /// covers 100 % of that population (containment: no structural "hole" is left uncategorised);
    /// and (c), by construction of the population itself, 0 live cells are positioned on those
    /// ordinates - if a TOTALLY SHADED ordinate ever starts covering a cell with data without this
    /// test noticing, it would be because someone changed what "shaded" means, and that is exactly
    /// what must be caught.
    /// </summary>
    [DataFact]
    public void CalX252_TotallyShadedOrdinates_CarryThePairAndNeverTouchALiveCell()
    {
        var connection = fixture.GeneratedConnection;

        foreach (var tableCode in OwnerAxisTableCodes)
        {
            var axisId = SingleOwnerXAxisId(connection, tableCode);

            // Population defined WITHOUT looking at CAL: every ordinate of the axis whose
            // positioned cells are ALL shaded - 0 among them with IsShaded = 0.
            var fullyShadedOrdinateIds = QueryHelpers.Rows(
                connection,
                $"""
                SELECT ao."OrdinateID"
                FROM "mAxisOrdinate" ao
                WHERE ao."AxisID" = {axisId}
                  AND NOT EXISTS (
                      SELECT 1 FROM "mCellPosition" cp
                      JOIN "mTableCell" tc ON tc."CellID" = cp."CellID"
                      WHERE cp."OrdinateID" = ao."OrdinateID" AND tc."IsShaded" = 0
                  )
                """,
                1).Select(r => r[0]).ToList();

            // Positive control: if this is 0, the rest of the test examines nothing.
            Assert.True(fullyShadedOrdinateIds.Count > 0, $"{tableCode}: 0 totally shaded ordinates on the X axis - nothing to check.");

            var idList = string.Join(",", fullyShadedOrdinateIds);

            var fullyShadedWithPair = QueryHelpers.Scalar(connection, CalX252CategorisationCountSql($"ao.\"OrdinateID\" IN ({idList})"));
            Assert.Equal(fullyShadedOrdinateIds.Count, fullyShadedWithPair); // (b) containment: 100 % of the dead population carries the pair.

            var liveCellsOnFullyShaded = QueryHelpers.Scalar(
                connection,
                $"""
                SELECT COUNT(*) FROM "mCellPosition" cp
                JOIN "mTableCell" tc ON tc."CellID" = cp."CellID"
                WHERE tc."IsShaded" = 0 AND cp."OrdinateID" IN ({idList})
                """);
            Assert.Equal(0, liveCellsOnFullyShaded); // (c) by construction of the population - the executable sentinel.
        }
    }

    private static int SingleOwnerXAxisId(Microsoft.Data.Sqlite.SqliteConnection connection, string tableCode)
    {
        var axisIds = QueryHelpers.Rows(
            connection,
            $"""
            SELECT a."AxisID"
            FROM "mAxis" a
            JOIN "mTableAxis" ta ON ta."AxisID" = a."AxisID"
            JOIN "mTable" t ON t."TableID" = ta."TableID"
            JOIN "mTaxonomyTable" tt ON tt."TableID" = t."TableID"
            JOIN "mTaxonomy" tax ON tax."TaxonomyID" = tt."TaxonomyID"
            WHERE t."TableCode" = '{tableCode}' AND tax."TaxonomyCode" = 'irrbb 4.2'
              AND a."AxisOrientation" = 'X' AND a."IsOpenAxis" = 0
            """,
            1);

        // Universe positive control: if the table/axis is not located, the rest of the calling test proves nothing.
        Assert.True(axisIds.Count == 1, $"{tableCode}/irrbb 4.2: exactly 1 closed X axis was expected, found {axisIds.Count}.");
        return int.Parse(axisIds[0][0]!);
    }

    private static string CalX252CategorisationCountSql(string ordinateFilter) =>
        $"""
        SELECT COUNT(*) FROM "mAxisOrdinate" ao
        JOIN "mOrdinateCategorisation" oc ON oc."OrdinateID" = ao."OrdinateID"
        JOIN "mDimension" d ON d."DimensionID" = oc."DimensionID" AND d."DimensionCode" = 'CAL'
        JOIN "mMember" m ON m."MemberID" = oc."MemberID" AND m."MemberCode" = 'x252'
        WHERE {ordinateFilter}
        """;
}
