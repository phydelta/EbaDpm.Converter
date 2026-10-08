using EbaDpm.Converter.Tests.Dictionary;

namespace EbaDpm.Converter.Tests.All;

/// <summary>
/// Sentinel invariants A-SEN-01/02/03/04/05/07, over <c>--all</c>. They were already covered by
/// <see cref="Dictionary.MemberSentinelTests"/>, but ONLY over the 8-taxonomy fixture: they are
/// repeated here at the scale of all 128, which is the one that really matters (the <c>9999</c>
/// sentinel is also used by taxonomies outside the 8 that have a reference).
/// </summary>
[Collection("All")]
[Trait("Tier", "RealData")]
public sealed class SentinelOnAllTests
{
    private readonly AllFixture _fixture;

    public SentinelOnAllTests(AllFixture fixture)
    {
        _fixture = fixture;
    }

    [DataFact]
    public void ExactlyOneOpenDomain_9999_WithTheFixedShape()
    {
        var rows = QueryHelpers.Rows(
            _fixture.GeneratedConnection,
            "SELECT \"DomainLabel\", \"IsTypedDomain\", \"DataType\", \"ConceptID\" FROM \"mDomain\" WHERE \"DomainID\" = 9999",
            4);

        Assert.True(rows.Count == 1, $"mDomain with DomainID=9999: {rows.Count} rows (exactly 1 expected, A-SEN-01).");
        Assert.Equal("Open", rows[0][0]);
        Assert.Equal("0", rows[0][1]);
        Assert.Null(rows[0][2]);
        Assert.Null(rows[0][3]);
    }

    [DataFact]
    public void ExactlyOneOpenMember_9999_WithTheFixedShape()
    {
        var rows = QueryHelpers.Rows(
            _fixture.GeneratedConnection,
            "SELECT \"DomainID\", \"MemberLabel\", \"MemberXBRLCode\", \"IsDefaultMember\" FROM \"mMember\" WHERE \"MemberID\" = 9999",
            4);

        Assert.True(rows.Count == 1, $"mMember with MemberID=9999: {rows.Count} rows (exactly 1 expected, A-SEN-02).");
        Assert.Equal("9999", rows[0][0]);
        Assert.Equal("Open", rows[0][1]);
        Assert.Null(rows[0][2]);
        Assert.Equal("0", rows[0][3]);
    }

    /// <summary>A-SEN-03: the original occupant in the Access (C27_12) is renumbered and emitted intact; no Path retains the 9999 segment.</summary>
    [DataFact]
    public void RenumberedOriginalOccupant_IsEmittedIntact_AndNoPathRetainsTheSegment9999()
    {
        var occupantCount = QueryHelpers.Scalar(
            _fixture.GeneratedConnection, "SELECT COUNT(*) FROM \"mMember\" WHERE \"MemberCode\" = 'C27_12'");
        Assert.True(occupantCount == 1, $"mMember with MemberCode='C27_12': {occupantCount} rows (exactly 1 expected, A-SEN-03).");

        var pathsWithSentinel = QueryHelpers.Scalar(
            _fixture.GeneratedConnection,
            "SELECT COUNT(*) FROM \"mHierarchyNode\" WHERE \"Path\" LIKE '%.9999.%' OR \"Path\" LIKE '9999.%' OR \"Path\" = '9999.'");
        Assert.True(pathsWithSentinel == 0, $"{pathsWithSentinel} hierarchy nodes whose Path retains the 9999 segment (A-SEN-03).");
    }

    [DataFact]
    public void NoRow_UsesTheAccessSentinel_999()
    {
        var count = QueryHelpers.Scalar(_fixture.GeneratedConnection, "SELECT COUNT(*) FROM \"mOrdinateCategorisation\" WHERE \"MemberID\" = 999");
        Assert.True(count == 0, $"{count} mOrdinateCategorisation rows with MemberID=999 (SOURCE sentinel, A-SEN-04).");
    }

    [DataFact]
    public void NoOrdinateCode_IsTheOpenSentinel_999()
    {
        var count = QueryHelpers.Scalar(
            _fixture.GeneratedConnection, "SELECT COUNT(*) FROM \"mAxisOrdinate\" WHERE \"OrdinateCode\" = '999' OR \"OrdinateCode\" = '999 '");
        Assert.True(count == 0, $"{count} ordinates with OrdinateCode='999' or '999 ' (A-SEN-05).");
    }

    [DataFact]
    public void EveryOpenAxis_HasExactlyOneCategorisationRow_WithTheSentinelMember9999_OnAll()
    {
        var openAxisCount = QueryHelpers.Scalar(_fixture.GeneratedConnection, "SELECT COUNT(*) FROM \"mAxis\" WHERE \"IsOpenAxis\" = 1");
        var sentinelCategorisationCount = QueryHelpers.Scalar(_fixture.GeneratedConnection, "SELECT COUNT(*) FROM \"mOrdinateCategorisation\" WHERE \"MemberID\" = 9999");

        Assert.True(
            openAxisCount == sentinelCategorisationCount,
            $"Open axes: {openAxisCount} versus categorisation rows with MemberID=9999: {sentinelCategorisationCount} (A-SEN-07, should match).");
    }
}
