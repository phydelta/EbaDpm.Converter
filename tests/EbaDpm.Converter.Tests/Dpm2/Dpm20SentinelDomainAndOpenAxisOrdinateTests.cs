using EbaDpm.Converter.Tests.Dictionary;

namespace EbaDpm.Converter.Tests.Dpm2;

/// <summary>
/// Two fixes of <c>--validate</c> without a reference, one by one. (a) <c>A-SEN-01</c>: the
/// sentinel domain 9999 carries NO concept, but the sentinel MEMBER DOES - the asymmetry measured
/// against the reference. (b) <c>A-AXE-10</c>: the single ordinate of an open axis carries
/// <c>Order</c> = 0, not 1. (c) <c>A-CEL-01</c>/<c>A-CEL-02</c> are restated by scope in
/// <c>CellChecks.cs</c> and are covered by <see cref="Dpm20ValidateSuiteTests"/> through
/// <c>--validate</c> itself; they are not repeated here as an independent SQL assertion because
/// the right way to check them is against the <c>Validator</c> itself, not a parallel
/// reconstruction.
///
/// (a) is also checked in detail, column by column, in <c>Dpm20ConceptTests</c>; this is the
/// minimal, self-contained version that names the asymmetry on its own.
/// </summary>
[Collection("Dpm2Skeleton")]
[Trait("Tier", "RealData")]
public sealed class Dpm20SentinelDomainAndOpenAxisOrdinateTests(Dpm20SkeletonFixture fixture)
{
    // ------------------------------------------------------------------
    // The sentinel domain 9999 ("", 'Open') carries NO concept; the sentinel MEMBER DOES. The
    // asymmetry is real and measured against the reference - it is not relaxed to "both without
    // concept" nor "both with concept".
    // ------------------------------------------------------------------

    [DataFact]
    public void SentinelDomain_HasNoConcept_ButTheSentinelMember_Does()
    {
        var domainRow = QueryHelpers.Rows(
                fixture.GeneratedConnection,
                "SELECT \"DomainID\", \"ConceptID\" FROM \"mDomain\" WHERE \"DomainCode\" = '' AND \"DomainLabel\" = 'Open'",
                2)
            .Single();
        var domainId = int.Parse(domainRow[0]!);
        Assert.Null(domainRow[1]); // the sentinel domain: ConceptID NULL.

        var memberRow = QueryHelpers.Rows(
                fixture.GeneratedConnection,
                $"SELECT \"MemberID\", \"ConceptID\" FROM \"mMember\" WHERE \"DomainID\" = {domainId} AND \"MemberCode\" = ''",
                2)
            .Single();
        Assert.NotNull(memberRow[1]); // the sentinel member: ConceptID NOT NULL - the asymmetry.
    }

    // ------------------------------------------------------------------
    // A-AXE-10: the single ordinate of EACH open axis carries Order = 0, never 1. Measured: 0
    // violations in the reference, all 389 with 0.
    // ------------------------------------------------------------------

    [DataFact]
    public void TheSingleOrdinateOfEachOpenAxis_HasOrderZero_NeverOne_AAxe10()
    {
        var rows = QueryHelpers.Rows(
            fixture.GeneratedConnection,
            """
            SELECT o."OrdinateID", o."Order"
            FROM "mAxisOrdinate" o
            JOIN "mAxis" a ON a."AxisID" = o."AxisID"
            WHERE a."IsOpenAxis" = 1
            """,
            2);

        Assert.Equal(389, rows.Count); // 389 open axes, measured.

        var violations = rows.Where(r => r[1] != "0").ToList();
        Assert.True(
            violations.Count == 0,
            $"{violations.Count}/389 open-axis ordinates do NOT have Order=0: "
            + string.Join(", ", violations.Take(20).Select(r => $"OrdinateID={r[0]}, Order={r[1]}")));
    }
}
