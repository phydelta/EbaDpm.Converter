namespace EbaDpm.Converter.Tests.Dictionary;

/// <summary>
/// Guards the "open value" sentinel emitted by the dictionary -- <c>mDomain</c> 9999 and
/// <c>mMember</c> 9999, both labelled <c>Open</c> -- and the renumbering of the only real
/// occupant of the Access database that lives at that ID today (<c>C27_12</c>, NACE, domain 280).
/// Critical layer: the absence of this sentinel leaves all the open-axis categorisations orphaned.
/// </summary>
[Collection("Dictionary")]
public sealed class MemberSentinelTests
{
    private const int SentinelId = 9999;

    /// <summary>MemberID of the real occupant in the Access v4.1: it equals SentinelId itself,
    /// because it is precisely the ID that the sentinel reserves and that had to be freed.</summary>
    private const int AccessOccupantId = 9999;

    private readonly DictionaryFixture _fixture;

    public MemberSentinelTests(DictionaryFixture fixture)
    {
        _fixture = fixture;
    }

    [DataFact]
    public void DomainSentinel_ExistsExactlyOnce_WithTheFixedValues()
    {
        var totalWithId = QueryHelpers.Scalar(_fixture.GeneratedConnection, $"SELECT COUNT(*) FROM \"mDomain\" WHERE \"DomainID\" = {SentinelId}");
        Assert.True(totalWithId == 1, $"Exactly 1 row mDomain.DomainID={SentinelId} was expected; there are {totalWithId}.");

        var rows = QueryHelpers.Rows(
            _fixture.GeneratedConnection,
            $"SELECT \"DomainCode\", \"DomainLabel\", \"DomainXBRLCode\", \"DataType\", \"ConceptID\" FROM \"mDomain\" WHERE \"DomainID\" = {SentinelId}",
            5);
        var row = rows[0];

        Assert.Equal(string.Empty, row[0]); // DomainCode = '' (follows 4.0/4.2/EIOPA, not the NULL of 3.2)
        Assert.Equal("Open", row[1]); // DomainLabel
        Assert.Equal(string.Empty, row[2]); // DomainXBRLCode = ''
        Assert.Null(row[3]); // DataType NULL
        Assert.Null(row[4]); // ConceptID NULL: the sentinel domain carries no concept of its own (unlike the member)

        var isTypedDomain = QueryHelpers.Scalar(_fixture.GeneratedConnection, $"SELECT \"IsTypedDomain\" FROM \"mDomain\" WHERE \"DomainID\" = {SentinelId}");
        Assert.Equal(0, isTypedDomain);
    }

    [DataFact]
    public void MemberSentinel_ExistsExactlyOnce_WithTheFixedValues()
    {
        var totalWithId = QueryHelpers.Scalar(_fixture.GeneratedConnection, $"SELECT COUNT(*) FROM \"mMember\" WHERE \"MemberID\" = {SentinelId}");
        Assert.True(totalWithId == 1, $"Exactly 1 row mMember.MemberID={SentinelId} was expected; there are {totalWithId}.");

        var rows = QueryHelpers.Rows(
            _fixture.GeneratedConnection,
            $"SELECT \"DomainID\", \"MemberCode\", \"MemberLabel\", \"MemberXBRLCode\", \"ConceptID\" FROM \"mMember\" WHERE \"MemberID\" = {SentinelId}",
            5);
        var row = rows[0];

        Assert.Equal(SentinelId.ToString(), row[0]); // DomainID: also 9999, the sentinel domain
        Assert.Equal(string.Empty, row[1]); // MemberCode = '' (not NULL)
        Assert.Equal("Open", row[2]); // MemberLabel
        Assert.Null(row[3]); // MemberXBRLCode NULL (matches in all four references, unlike the code)
        Assert.NotNull(row[4]); // synthetic ConceptID, NOT NULL

        var isDefaultMember = QueryHelpers.Scalar(_fixture.GeneratedConnection, $"SELECT \"IsDefaultMember\" FROM \"mMember\" WHERE \"MemberID\" = {SentinelId}");
        Assert.Equal(0, isDefaultMember);
    }

    [DataFact]
    public void NoOtherMemberRow_CarriesTheSentinelId()
    {
        // Redundant with the SQLite PK (which guarantees at most one row per MemberID), but it
        // records the business property explicitly: 9999 is EXCLUSIVE to the sentinel.
        var withSentinelLabel = QueryHelpers.Scalar(
            _fixture.GeneratedConnection,
            $"SELECT COUNT(*) FROM \"mMember\" WHERE \"MemberID\" = {SentinelId} AND \"MemberLabel\" = 'Open'");
        Assert.Equal(1, withSentinelLabel);
    }

    [DataFact]
    public void AccessOccupant_C27_12_IsStillEmitted_UnderARenumberedId_WithCodeAndLabelIntact()
    {
        var accessOccupant = _fixture.AccessReader.ReadMembers().Single(m => m.MemberId == AccessOccupantId);

        // Guard: if the source Access database changed its occupant at 9999, this test must fail
        // and warn instead of silently comparing against a different member.
        Assert.Equal("C27_12", accessOccupant.MemberCode);

        var renumbering = _fixture.DictionaryResult.MemberIdRenumbering;
        var renumberedId = renumbering.Translate(AccessOccupantId);

        Assert.NotEqual(AccessOccupantId, renumberedId); // the occupant has moved, it did not stay at 9999

        var rows = QueryHelpers.Rows(
            _fixture.GeneratedConnection,
            $"SELECT \"DomainID\", \"MemberCode\", \"MemberLabel\", \"MemberXBRLCode\" FROM \"mMember\" WHERE \"MemberID\" = {renumberedId}",
            4);

        Assert.True(rows.Count == 1, $"C27_12 should still be emitted under the renumbered ID {renumberedId}; there are {rows.Count} rows.");

        var row = rows[0];
        Assert.Equal(accessOccupant.DomainId?.ToString(), row[0]);
        Assert.Equal(accessOccupant.MemberCode, row[1]); // code intact
        Assert.Equal(accessOccupant.MemberLabel, row[2]); // label intact
        Assert.Equal(accessOccupant.MemberXbrlCode, row[3]); // XBRL code intact, verbatim
    }

    [DataFact]
    public void MMember_HasNoOrphansTowardsMDomain() =>
        QueryHelpers.AssertNoOrphans(_fixture.GeneratedConnection, "mMember", "DomainID", "mDomain", "DomainID");

    [DataFact]
    public void MHierarchyNode_MemberID_HasNoOrphansTowardsMMember() =>
        QueryHelpers.AssertNoOrphans(_fixture.GeneratedConnection, "mHierarchyNode", "MemberID", "mMember", "MemberID");

    [DataFact]
    public void MHierarchyNode_ParentMemberID_HasNoOrphansTowardsMMember() =>
        QueryHelpers.AssertNoOrphans(_fixture.GeneratedConnection, "mHierarchyNode", "ParentMemberID", "mMember", "MemberID");

    [DataFact]
    public void NoHierarchyNodePath_RetainsTheUntranslatedOccupantSegment()
    {
        var renumbering = _fixture.DictionaryResult.MemberIdRenumbering;
        var renumberedId = renumbering.Translate(AccessOccupantId);

        if (renumberedId == AccessOccupantId)
        {
            return; // Access without an occupant at 9999: there was nothing to renumber or to check here.
        }

        // The sentinel has no HierarchyNode of its own (it takes part in no tree), so no Path
        // should contain the "9999" segment after renumbering -- neither at the start, in the
        // middle, nor at the end. A residue here is exactly the "silent failure" to avoid: the
        // signature of a hierarchy that dragged that node along would point to the wrong member.
        var rows = QueryHelpers.Rows(
            _fixture.GeneratedConnection,
            """
            SELECT "HierarchyID", "MemberID", "Path" FROM "mHierarchyNode"
            WHERE "Path" = '9999.' OR "Path" LIKE '9999.%' OR "Path" LIKE '%.9999.' OR "Path" LIKE '%.9999.%'
            """,
            3);

        Assert.True(
            rows.Count == 0,
            $"{rows.Count} mHierarchyNode.Path rows keep the untranslated 9999 segment. " +
            $"Examples: {string.Join("; ", rows.Take(10).Select(r => string.Join("|", r)))}");
    }
}
