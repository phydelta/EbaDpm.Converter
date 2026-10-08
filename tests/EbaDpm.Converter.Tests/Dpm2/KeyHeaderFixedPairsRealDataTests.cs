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
/// axis - <c>HeaderID=964</c>, <c>ContextID=759675</c>. Verified against the real corpus: the
/// ordinate carries its open pair (<c>qEEA</c>, sentinel <c>MemberID=9999</c>) PLUS
/// <c>APR(eba_AP:x66)</c> and <c>EXC(eba_qEC:qx4)</c> - the two fixed pairs that the dead read lost.
///
/// No census of <c>mOrdinateCategorisation</c> is asserted: what this test protects is that those
/// two CONCRETE pairs are in the ordinate, not a global count.
/// The case does NOT repeat in 4.3 - the same <c>HeaderVID=235107</c> exists there with a NULL
/// <c>ContextID</c> (the source itself cleared it between releases). That is a property of that
/// publication, not of the model, so a "0 cases in 4.3" is not pinned here.
/// </summary>
[Collection("Dpm2Skeleton")]
[Trait("Tier", "RealData")]
public sealed class KeyHeaderFixedPairsRealDataTests(Dpm20SkeletonFixture fixture)
{
    [DataFact]
    public void Corep42_C0805_OpenZAxisOrdinate_CarriesTheOpenPairAndTheTwoFixedPairs()
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

        // The two FIXED pairs that the dead read of HeaderNode.ContextId lost.
        Assert.True(byDimension.TryGetValue("APR", out var apr), "APR is missing - the key header's fixed pair was not projected onto the ordinate.");
        Assert.Contains("APR(eba_AP:x66)", apr.Dps, StringComparison.Ordinal);

        Assert.True(byDimension.TryGetValue("EXC", out var exc), "EXC is missing - the key header's fixed pair was not projected onto the ordinate.");
        Assert.Contains("EXC(eba_qEC:qx4)", exc.Dps, StringComparison.Ordinal);
    }
}
