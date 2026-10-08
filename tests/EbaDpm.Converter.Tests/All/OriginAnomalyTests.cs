using EbaDpm.Converter.Tests.Dictionary;

namespace EbaDpm.Converter.Tests.All;

/// <summary>
/// Anomalies of the source: data in the Access database that VIOLATE an invariant of the
/// destination format and that are faithfully reproduced (verbatim). They are not defects of a
/// reference database (there is no evidence that the EBA is wrong) nor declared divergences
/// (there is no comparison to make): the problem lives INSIDE the source, and the census is
/// exact and must be pinned. If the census changes tomorrow, the test fails on its own -- this
/// is what replaces a nominal exception list for this kind of anomaly.
/// </summary>
[Collection("All")]
[Trait("Tier", "RealData")]
public sealed class OriginAnomalyTests
{
    private readonly AllFixture _fixture;

    public OriginAnomalyTests(AllFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// 50 members of the Access carry the namespace prefix INSIDE the <c>MemberCode</c> (e.g.
    /// <c>TI:x102</c>), measured directly on <c>Access.Member</c>, without going through the
    /// conversion.
    /// </summary>
    [DataFact]
    public void Access_HasExactly50MembersWithColonInMemberCode()
    {
        var members = _fixture.AccessReader.ReadMembers().ToList();
        var withColon = members.Where(m => m.MemberCode.Contains(':')).ToList();

        Assert.True(
            withColon.Count == 50,
            $"Access.Member with ':' in MemberCode: {withColon.Count} (exactly 50 expected). " +
            $"Examples: {string.Join(", ", withColon.Take(10).Select(m => $"MemberID={m.MemberId} MemberCode={m.MemberCode}"))}");
    }

    /// <summary>
    /// Measured consequence of the above -- 49 duplicated <c>MemberXbrlCode</c> values in the
    /// Access, always between two distinct domains.
    /// </summary>
    [DataFact]
    public void Access_HasExactly49DuplicatedMemberXbrlCodes()
    {
        var members = _fixture.AccessReader.ReadMembers().ToList();
        var duplicatedCodes = members
            .Where(m => m.MemberXbrlCode is not null)
            .GroupBy(m => m.MemberXbrlCode)
            .Where(g => g.Count() > 1)
            .ToList();

        Assert.True(
            duplicatedCodes.Count == 49,
            $"Duplicated Access.Member.MemberXbrlCode: {duplicatedCodes.Count} codes (exactly 49 " +
            $"expected). Examples: {string.Join(", ", duplicatedCodes.Take(10).Select(g => g.Key))}");

        foreach (var group in duplicatedCodes)
        {
            var domainIds = group.Select(m => m.DomainId).Distinct().ToList();
            Assert.True(
                domainIds.Count == group.Count(),
                $"MemberXbrlCode='{group.Key}' duplicated within the SAME domain (expected: always between " +
                $"two distinct domains). DomainIDs: {string.Join(",", group.Select(m => m.DomainId))}");
        }
    }

    /// <summary>
    /// Effect on the output: the census of the generated database must reproduce that of the
    /// Access, no more and no less, because the 50 members are emitted verbatim and not
    /// discarded (the only real alternative was to lose source data).
    /// </summary>
    [DataFact]
    public void Generated_HasExactly49DuplicatedMemberXbrlCodes_AndTheUnicityAssertionIsInformativeOnly()
    {
        var rows = QueryHelpers.Rows(
            _fixture.GeneratedConnection,
            """
            SELECT "MemberXBRLCode", COUNT(*) FROM "mMember"
            WHERE "MemberXBRLCode" IS NOT NULL
            GROUP BY "MemberXBRLCode"
            HAVING COUNT(*) > 1
            """,
            2);

        Assert.True(
            rows.Count == 49,
            $"Duplicated mMember.MemberXBRLCode in the generated output: {rows.Count} (exactly 49 expected " +
            $"- uniqueness drops to the informative layer BUT with the census pinned). Examples: " +
            $"{string.Join(", ", rows.Take(10).Select(r => r[0]))}");
    }

    /// <summary>
    /// SEVEN objects of the Access without a <c>ConceptID</c>, named one by one -- two domains,
    /// three members, two hierarchies. Measured directly on the Access, without going through the
    /// conversion.
    ///
    /// The <c>9999</c> sentinel does NOT enter this census: it does not live in the Access at all,
    /// it is synthesised in the DESTINATION by design. It is the EIGHTH object with a NULL
    /// <c>ConceptID</c>, but only in the generated output -- see
    /// <see cref="Generated_HasExactlyEightObjectsWithNullConceptId_IncludingTheSentinelDomain9999"/>,
    /// which ties the two figures together (7 from the source + 1 sentinel by design = 8 in the
    /// destination) so that they cannot drift apart separately again.
    /// </summary>
    [DataFact]
    public void Access_HasExactlySevenObjectsWithNullConceptId_NamedOneByOne()
    {
        var domainsWithoutConcept = _fixture.AccessReader.ReadDomains()
            .Where(d => d.ConceptId is null)
            .Select(d => d.DomainCode)
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToList();

        var membersWithoutConcept = _fixture.AccessReader.ReadMembers()
            .Where(m => m.ConceptId is null)
            .Select(m => m.MemberXbrlCode ?? m.MemberCode)
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToList();

        var hierarchiesWithoutConcept = _fixture.AccessReader.ReadHierarchies()
            .Where(h => h.ConceptId is null)
            .Select(h => h.HierarchyCode)
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToList();

        var dimensionsWithoutConcept = _fixture.AccessReader.ReadDimensions()
            .Count(d => d.ConceptId is null);

        Assert.True(
            domainsWithoutConcept.SequenceEqual(["INT_NA", "STR_NA"]),
            $"Access domains with a NULL ConceptID: [{string.Join(",", domainsWithoutConcept)}] " +
            "(exactly INT_NA and STR_NA expected).");

        Assert.True(
            membersWithoutConcept.Count == 3
            && membersWithoutConcept.Contains("eba_qEC:qx01")
            && membersWithoutConcept.Contains("eba_qAE:qx01")
            && membersWithoutConcept.Contains("eba_GA:qx2007"),
            $"Access members with a NULL ConceptID: [{string.Join(",", membersWithoutConcept)}] " +
            "(exactly eba_qEC:qx01, eba_qAE:qx01, eba_GA:qx2007 expected).");

        Assert.True(
            hierarchiesWithoutConcept.SequenceEqual(["AP29_QAP31", "CU4_CU4_REL_4"]),
            $"Access hierarchies with a NULL ConceptID: [{string.Join(",", hierarchiesWithoutConcept)}] " +
            "(exactly AP29_QAP31 and CU4_CU4_REL_4 expected).");

        Assert.True(
            dimensionsWithoutConcept == 0,
            $"Access dimensions with a NULL ConceptID: {dimensionsWithoutConcept} (0 expected).");

        var total = domainsWithoutConcept.Count + membersWithoutConcept.Count + hierarchiesWithoutConcept.Count + dimensionsWithoutConcept;
        Assert.True(
            total == 7,
            $"Total Access objects with a NULL ConceptID: {total} (exactly 7 expected -- " +
            "the 9999 sentinel does NOT live in the Access, so it does not count here; it is the eighth object, " +
            "but only in the generated output).");
    }

    /// <summary>
    /// Effect on the generated output: the same census of SEVEN, not one more -- plus the
    /// <c>9999</c> sentinel of <c>mDomain</c>, which carries a NULL <c>ConceptID</c> BY DESIGN and
    /// makes the total EIGHT only in the destination (never in the source).
    /// </summary>
    [DataFact]
    public void Generated_HasExactlySevenObjectsWithNullConceptId_ExcludingTheSentinelDomain9999()
    {
        var domainsWithoutConcept = QueryHelpers.Scalar(
            _fixture.GeneratedConnection,
            "SELECT COUNT(*) FROM \"mDomain\" WHERE \"ConceptID\" IS NULL AND \"DomainID\" <> 9999");
        var membersWithoutConcept = QueryHelpers.Scalar(
            _fixture.GeneratedConnection, "SELECT COUNT(*) FROM \"mMember\" WHERE \"ConceptID\" IS NULL");
        var hierarchiesWithoutConcept = QueryHelpers.Scalar(
            _fixture.GeneratedConnection, "SELECT COUNT(*) FROM \"mHierarchy\" WHERE \"ConceptID\" IS NULL");
        var dimensionsWithoutConcept = QueryHelpers.Scalar(
            _fixture.GeneratedConnection, "SELECT COUNT(*) FROM \"mDimension\" WHERE \"ConceptID\" IS NULL");

        Assert.True(domainsWithoutConcept == 2, $"mDomain with a NULL ConceptID (excluding 9999): {domainsWithoutConcept} (2 expected).");
        Assert.True(membersWithoutConcept == 3, $"mMember with a NULL ConceptID: {membersWithoutConcept} (3 expected).");
        Assert.True(hierarchiesWithoutConcept == 2, $"mHierarchy with a NULL ConceptID: {hierarchiesWithoutConcept} (2 expected).");
        Assert.True(dimensionsWithoutConcept == 0, $"mDimension with a NULL ConceptID: {dimensionsWithoutConcept} (0 expected).");

        var totalExcludingSentinel = domainsWithoutConcept + membersWithoutConcept + hierarchiesWithoutConcept + dimensionsWithoutConcept;
        Assert.True(
            totalExcludingSentinel == 7,
            $"Total generated objects with a NULL ConceptID, excluding the sentinel: {totalExcludingSentinel} " +
            "(exactly 7 expected).");

        // And the 9999 sentinel MUST carry a NULL ConceptID, by design (A-SEN-01).
        var sentinelConceptId = QueryHelpers.Rows(
            _fixture.GeneratedConnection, "SELECT \"ConceptID\" FROM \"mDomain\" WHERE \"DomainID\" = 9999", 1);
        Assert.True(sentinelConceptId.Count == 1, "The sentinel domain DomainID=9999 does not exist (A-SEN-01).");
        Assert.Null(sentinelConceptId[0][0]);
    }

    /// <summary>
    /// Ties the two figures together so that they cannot drift apart separately: the total with a
    /// NULL <c>ConceptID</c> in the generated output is EXACTLY 7 (those of the source) + 1 (the
    /// 9999 sentinel, by design) = <b>8</b>, counting across the four dictionary tables at once.
    /// </summary>
    [DataFact]
    public void Generated_HasExactlyEightObjectsWithNullConceptId_IncludingTheSentinelDomain9999()
    {
        var domainsWithoutConcept = QueryHelpers.Scalar(_fixture.GeneratedConnection, "SELECT COUNT(*) FROM \"mDomain\" WHERE \"ConceptID\" IS NULL");
        var membersWithoutConcept = QueryHelpers.Scalar(_fixture.GeneratedConnection, "SELECT COUNT(*) FROM \"mMember\" WHERE \"ConceptID\" IS NULL");
        var hierarchiesWithoutConcept = QueryHelpers.Scalar(_fixture.GeneratedConnection, "SELECT COUNT(*) FROM \"mHierarchy\" WHERE \"ConceptID\" IS NULL");
        var dimensionsWithoutConcept = QueryHelpers.Scalar(_fixture.GeneratedConnection, "SELECT COUNT(*) FROM \"mDimension\" WHERE \"ConceptID\" IS NULL");

        var total = domainsWithoutConcept + membersWithoutConcept + hierarchiesWithoutConcept + dimensionsWithoutConcept;
        Assert.True(
            total == 8,
            $"Total generated objects with a NULL ConceptID, COUNTING the 9999 sentinel: {total} " +
            "(exactly 8 expected = 7 from the source + 1 sentinel by design).");
    }
}
