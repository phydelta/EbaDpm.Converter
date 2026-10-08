using EbaDpm.Converter.Tests.Dictionary;

namespace EbaDpm.Converter.Tests.Dpm2;

/// <summary>
/// Permanent test over the real <c>--all</c> DPM 2.0 4.2 corpus. <c>ProjectVariableContextOntoOrdinates</c>
/// projects the variable's context onto each ordinate looking ONLY at NON-shaded cells - an
/// ordinate that is TOTALLY shaded (all its cells <c>IsShaded=1</c>) would be left with only its
/// metric, without any of the dimensions that the Access database does declare in
/// <c>HeaderVersion.ContextID</c>. The fix: when no own cell of the header carries a resolved
/// context, that <c>ContextID</c> is used - the source that Access declares in 88.63 % of the
/// headers with a parent.
///
/// Witness case: <c>corep 4.2/C_07.00.c</c>, ordinate <c>0060</c> of the X axis. Verified against
/// the real data: the 4 cells of that ordinate are ALL shaded, and it goes from carrying only its
/// <c>MET</c> to its 6 complete pairs (<c>MET</c>, <c>qZZX</c>, <c>qMMM</c>, <c>qLHL</c>,
/// <c>qJMM</c>, <c>qAZP</c>).
/// </summary>
[Collection("Dpm2Skeleton")]
[Trait("Tier", "RealData")]
public sealed class ShadedOrdinateContextRealDataTests(Dpm20SkeletonFixture fixture)
{
    [DataFact]
    public void Corep42_C0700c_Ordinate0060OfTheXAxis_TotallyShaded_GetsItsSixPairs()
    {
        var connection = fixture.GeneratedConnection;

        var ordinateRows = QueryHelpers.Rows(connection,
            """
            SELECT ao.OrdinateID
            FROM mAxisOrdinate ao
            JOIN mAxis a ON a.AxisID = ao.AxisID
            JOIN mTableAxis ta ON ta.AxisID = a.AxisID
            JOIN mTable t ON t.TableID = ta.TableID
            JOIN mTaxonomyTable tt ON tt.TableID = t.TableID
            JOIN mTaxonomy tax ON tax.TaxonomyID = tt.TaxonomyID
            WHERE t.TableCode = 'C_07.00.c' AND ao.OrdinateCode = '0060' AND a.AxisOrientation = 'X'
              AND tax.TaxonomyCode = 'corep 4.2'
            """, 1);

        // Universe positive control: if this is 0, the witness case has disappeared from the
        // corpus and the rest of this test proves nothing.
        Assert.True(
            ordinateRows.Count > 0,
            "0 ordinates corep 4.2/C_07.00.c·0060(X) - the witness case is not in the corpus.");

        var ordinateId = ordinateRows[0][0];

        // Positive control of the case itself: it is still TOTALLY shaded - if it stopped being so,
        // it would no longer be the case described (the rule only acts on that population).
        var cellShading = QueryHelpers.Rows(connection,
            $"""
            SELECT tc.IsShaded
            FROM mCellPosition cp
            JOIN mTableCell tc ON tc.CellID = cp.CellID
            WHERE cp.OrdinateID = {ordinateId}
            """, 1);

        Assert.True(cellShading.Count > 0, "0 cells for the witness ordinate - empty universe.");
        Assert.All(cellShading, r => Assert.Equal("1", r[0]));

        var dimensionCodes = QueryHelpers.Rows(connection,
            $"""
            SELECT d.DimensionCode
            FROM mOrdinateCategorisation oc
            JOIN mDimension d ON d.DimensionID = oc.DimensionID
            WHERE oc.OrdinateID = {ordinateId}
            """, 1)
            .Select(r => r[0])
            .ToList();

        // Before the fix, a totally shaded ordinate carried only its MET. Now it carries its 6
        // pairs - the 5 dimensional ones plus the metric.
        Assert.Equal(6, dimensionCodes.Count);
        Assert.Contains("MET", dimensionCodes);
        Assert.Contains("qZZX", dimensionCodes);
        Assert.Contains("qMMM", dimensionCodes);
        Assert.Contains("qLHL", dimensionCodes);
        Assert.Contains("qJMM", dimensionCodes);
        Assert.Contains("qAZP", dimensionCodes);
    }
}
