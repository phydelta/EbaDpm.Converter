using EbaDpm.Converter.Tests.Dictionary;

namespace EbaDpm.Converter.Tests.All;

/// <summary>
/// Permanent regression test over the real <c>--all</c> DPM 1.0 corpus. Before the fix,
/// <c>AxisAndCellLoader</c> removed the sentinel ordinate (<c>MemberID=9999</c>) of an open axis
/// and took its FIXED categorisations (<c>IsDefaultMember=False</c>, <c>MemberID != 9999</c>)
/// with it -- nobody relocated them. The rule is: an open-axis ordinate CAN carry fixed pairs IN
/// ADDITION to the sentinel, and when the sentinel is removed its non-sentinel categorisations
/// are transferred to the ordinate that inherits its position.
///
/// Witness case: the layout <c>321-P2-SBP 3.2.1</c>, sheet <c>C 106.00</c>, annotates
/// <c>(AP:x93) IRC Model</c> under <c>(MRW:AP) Methods to determine risk weights</c>. Verified
/// against the real corpus: the open-axis ordinate of <c>C_106.00</c>/taxonomy <c>sbp 3.2.1</c>
/// carries <c>PBE(*)</c> (the sentinel pair) PLUS <c>APR(eba_AP:x26)</c>, <c>TRI(eba_TR:x11)</c>
/// and <c>MRW(eba_AP:x93)</c> -- the three fixed pairs that used to be lost.
/// </summary>
[Collection("All")]
[Trait("Tier", "RealData")]
public sealed class OpenYAxisSentinelFixedPairsRealDataTests(AllFixture fixture)
{
    [DataFact]
    public void C10600_Sbp321_OpenAxisOrdinate_CarriesTheSentinelAndTheThreeFixedPairs()
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
            WHERE t.TableCode = 'C_106.00' AND a.IsOpenAxis = 1 AND tax.TaxonomyCode = 'sbp 3.2.1'
            """, 1);

        // Positive control on the universe: if this is 0, the witness case has disappeared from
        // the corpus and the rest of this test proves nothing.
        Assert.True(
            ordinateRows.Count > 0,
            "0 open-axis ordinates in C_106.00/sbp 3.2.1 -- the witness case is not in the corpus.");

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
        Assert.True(byDimension.TryGetValue("PBE", out var pbe), "The sentinel dimension PBE is missing from the open-axis ordinate.");
        Assert.Equal("9999", pbe.MemberId);
        Assert.Contains("PBE(*)", pbe.Dps, StringComparison.Ordinal);

        // The three FIXED pairs that used to be lost and are now relocated.
        Assert.True(byDimension.TryGetValue("APR", out var apr), "APR is missing -- the fixed pair was not relocated to the target ordinate.");
        Assert.Contains("APR(eba_AP:x26)", apr.Dps, StringComparison.Ordinal);

        Assert.True(byDimension.TryGetValue("TRI", out var tri), "TRI is missing -- the fixed pair was not relocated to the target ordinate.");
        Assert.Contains("TRI(eba_TR:x11)", tri.Dps, StringComparison.Ordinal);

        Assert.True(byDimension.TryGetValue("MRW", out var mrw), "MRW is missing -- the fixed pair was not relocated to the target ordinate.");
        Assert.Contains("MRW(eba_AP:x93)", mrw.Dps, StringComparison.Ordinal);
    }
}
