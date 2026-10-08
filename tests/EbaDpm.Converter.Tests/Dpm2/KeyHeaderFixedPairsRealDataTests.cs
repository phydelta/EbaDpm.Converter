using EbaDpm.Converter.Tests.Dictionary;

namespace EbaDpm.Converter.Tests.Dpm2;

/// <summary>
/// Permanent test over the real <c>--all</c> DPM 2.0 4.2 corpus, on the KEY HEADER side: besides
/// the <c>PropertyID</c> that opens the dimension, a key header may carry a
/// <c>HeaderVersion.ContextID</c> with FIXED pairs - <c>HeaderNode.ContextId</c> used to be read in
/// <c>Dpm20AxisAndCellLoader</c> and never consulted (a dead read). The export criterion is the
/// Access database, not visibility in the layout or in the reference.
///
/// Witness case: <c>corep 4.2/C_08.05</c> (<c>TableVID=6965</c>), the only ordinate of the OPEN Z
/// axis - <c>HeaderID=964</c>. In the previous edition of the 4.2 database its HeaderVersion carried
/// <c>ContextID=759675</c> and the ordinate had its open pair (<c>qEEA</c>, sentinel
/// <c>MemberID=9999</c>) PLUS <c>APR(eba_AP:x66)</c> and <c>EXC(eba_qEC:qx4)</c>. In
/// <c>DPM2 Database_v 4_2_1.accdb</c> the EBA set that ContextID to NULL, so the test now asserts the
/// ABSENCE of the fixed pairs (the open pair stays). OPEN ISSUE: the mechanism has no real-data
/// instance any more (key headers with a ContextID: 0 of 406 in force at 4.2 in the 4.2.1 file, 0 of
/// 429 in the 4.3 file), so its positive coverage must come from a synthetic test.
///
/// No census of <c>mOrdinateCategorisation</c> is asserted: what this test protects is that those
/// two CONCRETE pairs are in the ordinate, not a global count.
/// </summary>
[Collection("Dpm2Skeleton")]
[Trait("Tier", "RealData")]
public sealed class KeyHeaderFixedPairsRealDataTests(Dpm20SkeletonFixture fixture)
{
    [DataFact]
    public void Corep42_C0805_OpenZAxisOrdinate_CarriesOnlyTheOpenPair_TheEbaClearedTheFixedPairsIn421()
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
            WHERE t.TableCode = 'C_08.05' AND a.AxisOrientation = 'Z' AND a.IsOpenAxis = 1
              AND tax.TaxonomyCode = 'corep 4.2'
            """, 1);

        // Universe positive control: if this is 0, the witness case has disappeared from the
        // corpus and the rest of this test proves nothing.
        Assert.True(
            ordinateRows.Count > 0,
            "0 open Z axis ordinates in C_08.05/corep 4.2 - the witness case is not in the corpus.");

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

        // The open pair is still there - the fix does not touch it, it only repairs the fixed
        // pairs that accompanied it.
        Assert.True(byDimension.TryGetValue("qEEA", out var qeea), "The open pair qEEA is missing from the Z axis ordinate.");
        Assert.Equal("9999", qeea.MemberId);
        Assert.Contains("qEEA(*", qeea.Dps, StringComparison.Ordinal);

        // NEGATIVE assertion (EBA edit in "DPM2 Database_v 4_2_1.accdb"): HeaderVersion 235107
        // (HeaderID 964, StartReleaseID 3, still in force at 4.2) had ContextID=759675 with the two
        // fixed pairs APR(eba_AP:x66) and EXC(eba_qEC:qx4) in the previous edition of the 4.2
        // database; the 4.2.1 edition has ContextID NULL on that same row (measured on both Access
        // files). The converter emits what the source declares, so the ordinate carries ONLY the
        // open pair.
        Assert.False(byDimension.ContainsKey("APR"), "APR is present although HeaderVersion 235107 has ContextID NULL in the 4.2.1 source.");
        Assert.False(byDimension.ContainsKey("EXC"), "EXC is present although HeaderVersion 235107 has ContextID NULL in the 4.2.1 source.");
        Assert.Single(byDimension);
    }
}
