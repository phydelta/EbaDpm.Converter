using EbaDpm.Converter.Tests.Dictionary;

namespace EbaDpm.Converter.Tests.All;

/// <summary>
/// Second witness case for the relocation of fixed pairs when the sentinel ordinate is removed:
/// it covers the <c>AxisAndCellLoader</c> branch for an **open Z axis**, which
/// <see cref="OpenYAxisSentinelFixedPairsRealDataTests"/> does NOT exercise (that one covers the
/// open Y axis of <c>C_106.00</c>). Without this test, half of the code that relocates fixed pairs
/// has no permanent witness over real data.
///
/// Case, measured over a <c>--all</c> conversion of DPM 1.0: the taxonomy
/// <c>sbp/cir-2070-2016/2023-07-31</c> (<c>TechnicalStandard='CIR-2070-2016'</c> +
/// <c>PublicationDate='2023-07-31'</c>, i.e. <c>SBP 3.3.1</c>), table <c>C_110.03</c>, open Z
/// axis: BEFORE the fix, that axis carried ONLY the open pair <c>PBE</c>; AFTER it carries
/// <c>HYV=eba_BT:x10</c> IN ADDITION to <c>PBE</c> -- the fixed pair that used to be lost,
/// relocated to the ordinate that inherits the position of the removed sentinel.
/// </summary>
[Collection("All")]
[Trait("Tier", "RealData")]
public sealed class OpenZAxisSentinelFixedPairsRealDataTests(AllFixture fixture)
{
    [DataFact]
    public void C11003_Sbp331_OpenZAxisOrdinate_CarriesTheSentinelAndTheFixedPairHyv()
    {
        var connection = fixture.GeneratedConnection;

        // Positive control on the universe: locate the table and the axis by pure business key --
        // TableCode + TechnicalStandard/PublicationDate of the taxonomy (never TaxonomyID or
        // TableID: IDs are not stable between releases) -- and prove that exactly one table and
        // one open Z axis are found before looking at their categorisations.
        var tableRows = QueryHelpers.Rows(connection,
            """
            SELECT t.TableID
            FROM mTable t
            JOIN mTaxonomyTable tt ON tt.TableID = t.TableID
            JOIN mTaxonomy tax ON tax.TaxonomyID = tt.TaxonomyID
            WHERE t.TableCode = 'C_110.03'
              AND tax.TechnicalStandard = 'CIR-2070-2016'
              AND date(tax.PublicationDate) = '2023-07-31'
            """, 1);

        Assert.True(
            tableRows.Count == 1,
            $"Exactly 1 table C_110.03 was expected in sbp/cir-2070-2016/2023-07-31, {tableRows.Count} found -- the witness case is not in the corpus as measured.");

        var tableId = tableRows[0][0];

        var axisRows = QueryHelpers.Rows(connection,
            $"""
            SELECT a.AxisID
            FROM mAxis a
            JOIN mTableAxis ta ON ta.AxisID = a.AxisID
            WHERE ta.TableID = {tableId} AND a.AxisOrientation = 'Z' AND a.IsOpenAxis = 1
            """, 1);

        Assert.True(
            axisRows.Count == 1,
            $"Exactly 1 open Z axis was expected in C_110.03/sbp 3.3.1, {axisRows.Count} found.");

        var axisId = axisRows[0][0];

        var ordinateRows = QueryHelpers.Rows(connection,
            $"SELECT OrdinateID FROM mAxisOrdinate WHERE AxisID = {axisId}", 1);

        // Positive control on the universe: if this is 0, the open Z axis has no ordinates and
        // the rest of the test proves nothing.
        Assert.True(
            ordinateRows.Count > 0,
            "0 ordinates on the open Z axis of C_110.03/sbp 3.3.1 -- the witness case is not in the corpus.");

        var ordinateId = ordinateRows[0][0];

        var categorisations = QueryHelpers.Rows(connection,
            $"""
            SELECT d.DimensionCode, oc.MemberID, oc.DPS
            FROM mOrdinateCategorisation oc
            JOIN mDimension d ON d.DimensionID = oc.DimensionID
            WHERE oc.OrdinateID = {ordinateId}
            """, 3);

        var byDimension = categorisations
            .Where(r => r[0] is not null)
            .ToDictionary(r => r[0]!, r => (MemberId: r[1], Dps: r[2] ?? string.Empty), StringComparer.Ordinal);

        // The sentinel: it is removed from the open axis, and that STILL happens -- the fix does not touch it.
        Assert.True(byDimension.TryGetValue("PBE", out var pbe), "The sentinel dimension PBE is missing from the open Z axis ordinate.");
        Assert.Equal("9999", pbe.MemberId);
        Assert.Contains("PBE(*)", pbe.Dps, StringComparison.Ordinal);

        // The FIXED pair that used to be lost and is now relocated -- Z axis, the branch that
        // OpenYAxisSentinelFixedPairsRealDataTests (Y axis) does not cover.
        Assert.True(byDimension.TryGetValue("HYV", out var hyv), "HYV is missing -- the fixed pair was not relocated to the target ordinate of the Z axis.");
        Assert.Contains("HYV(eba_BT:x10)", hyv.Dps, StringComparison.Ordinal);
    }
}
