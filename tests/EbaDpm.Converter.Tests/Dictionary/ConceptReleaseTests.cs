using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Tests.Dictionary;

/// <summary>
/// Guards (a) dimension and member XBRL codes are verbatim from the Access database; (b)/(c)
/// <c>mConcept.ReleaseID</c> is NULL except in three specific cases. Critical layer: these tests
/// fail the build.
///
/// The row-by-row comparison of (a) relies on the fact that, within THIS SAME generated
/// database, <c>DimensionID</c>/<c>MemberID</c> are literally the <c>DimensionID</c>/<c>MemberID</c>
/// of the Access (<c>DictionaryLoader.LoadDimensions</c>/<c>LoadMembers</c> do not re-synthesise
/// the ID): crossing by ID between the generated output and the Access is valid here, unlike the
/// comparison against the external references (IDs are not comparable between different databases).
/// </summary>
[Collection("Dictionary")]
public sealed class ConceptReleaseTests
{
    private readonly DictionaryFixture _fixture;

    public ConceptReleaseTests(DictionaryFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// Independent oracle of the release suffix of an XBRL code, deliberately reimplemented here
    /// instead of invoking <c>DictionaryLoader.ExtractReleaseCodeFromXbrlSuffix</c> (which is also
    /// <c>internal</c> without <c>InternalsVisibleTo</c>): a test that calls the function it
    /// claims to test does not detect an error shared by both.
    /// </summary>
    private static readonly Regex ReleaseSuffixPattern = new(@"_(\d+(?:\.\d+)*)$", RegexOptions.Compiled);

    private static string? ExtractReleaseSuffix(string? xbrlCode)
    {
        if (string.IsNullOrEmpty(xbrlCode))
        {
            return null;
        }

        var colonIndex = xbrlCode.IndexOf(':');
        if (colonIndex < 0)
        {
            return null;
        }

        var prefix = xbrlCode[..colonIndex];
        var match = ReleaseSuffixPattern.Match(prefix);
        return match.Success ? match.Groups[1].Value : null;
    }

    // ------------------------------------------------------------------
    // Verbatim XBRL code, row by row, for EVERYTHING emitted (no sampling)
    // ------------------------------------------------------------------

    /// <summary>
    /// The metrics dimension (<c>ATY</c> in the Access, <c>DimensionID</c> 100) is RENAMED when
    /// emitted -- its <c>DimensionCode</c> becomes <c>MET</c> and its <c>DimensionXBRLCode</c>
    /// goes from NULL to <c>MET</c> -- so by definition it cannot be compared "verbatim against
    /// the Access". It is excluded by real <c>DimensionID</c> (not by plain <c>DimensionCode</c>,
    /// which would leave the Access side un-excluded -- where that row is NEVER called <c>MET</c> --
    /// and would unbalance the 863 versus 864 census). Its exact content is checked by
    /// <see cref="MetDimensionTests"/>.
    /// </summary>
    [DataFact]
    public void DimensionXbrlCode_IsVerbatimFromAccess_RowByRow_WithoutSampling()
    {
        var metricsDimensionId = int.Parse(
            QueryHelpers.Rows(_fixture.GeneratedConnection, "SELECT \"DimensionID\" FROM \"mDimension\" WHERE \"DimensionCode\" = 'MET'", 1)
                .Single()[0]!);

        var accessByDimensionId = _fixture.AccessReader.ReadDimensions()
            .Where(d => d.DimensionId != metricsDimensionId)
            .ToDictionary(d => d.DimensionId, d => d.DimensionXbrlCode);

        var generatedRows = QueryHelpers.Rows(
            _fixture.GeneratedConnection,
            $"SELECT \"DimensionID\", \"DimensionXBRLCode\" FROM \"mDimension\" WHERE \"DimensionID\" <> {metricsDimensionId}",
            2);

        Assert.True(
            generatedRows.Count == accessByDimensionId.Count,
            $"mDimension has {generatedRows.Count} generated rows versus {accessByDimensionId.Count} " +
            "in Access.Dimension (both excluding the renamed metrics dimension): the " +
            "row-by-row comparison requires the same census.");

        var mismatches = new List<string>();
        foreach (var row in generatedRows)
        {
            var dimensionId = int.Parse(row[0]!);
            var generatedCode = row[1];

            if (!accessByDimensionId.TryGetValue(dimensionId, out var accessCode))
            {
                mismatches.Add($"DimensionID={dimensionId}: does not exist in Access.Dimension (unexpected)");
                continue;
            }

            if (!string.Equals(generatedCode, accessCode, StringComparison.Ordinal))
            {
                mismatches.Add($"DimensionID={dimensionId}: generated='{generatedCode ?? "NULL"}' vs Access='{accessCode ?? "NULL"}'");
            }
        }

        Assert.True(
            mismatches.Count == 0,
            $"{mismatches.Count} of {generatedRows.Count} mDimension.DimensionXBRLCode rows " +
            $"differ from the Access verbatim. Examples: {string.Join("; ", mismatches.Take(20))}");
    }

    /// <summary>
    /// <c>MemberID</c> is NOT always literally the Access <c>MemberID</c>. Two exceptions, both
    /// due to the "Open" sentinel: Access <c>MemberID</c> 999 (<c>x999</c>) is CONSUMED, not
    /// emitted; and the real occupant of 9999 (<c>C27_12</c>) is renumbered. Both are handled
    /// apart from the general row-by-row comparison (which stays by literal ID for the rest,
    /// 11,026 of 11,028 members); the sentinel and the renumbered occupant have their own
    /// dedicated guard in <see cref="MemberSentinelTests"/>.
    /// </summary>
    [DataFact]
    public void MemberXbrlCode_IsVerbatimFromAccess_RowByRow_WithoutSampling()
    {
        const int AccessSentinelMemberId = 999; // consumed, not emitted
        const int SentinelId = 9999; // "Open" sentinel, with no counterpart in the Access

        var renumbering = _fixture.DictionaryResult.MemberIdRenumbering;

        var accessByMemberId = _fixture.AccessReader.ReadMembers()
            .Where(m => m.MemberId != AccessSentinelMemberId)
            .ToDictionary(m => renumbering.Translate(m.MemberId), m => m.MemberXbrlCode);

        var generatedRows = QueryHelpers.Rows(
            _fixture.GeneratedConnection, "SELECT \"MemberID\", \"MemberXBRLCode\" FROM \"mMember\"", 2);

        // +1 over the Access census without the 999: it is the 9999 sentinel, which has no
        // counterpart in the Access and is verified separately.
        Assert.True(
            generatedRows.Count == accessByMemberId.Count + 1,
            $"mMember has {generatedRows.Count} generated rows versus {accessByMemberId.Count} " +
            "in Access.Member (after excluding the consumed 999) + 1 (the 9999 sentinel): the " +
            "row-by-row comparison requires the same census.");

        var mismatches = new List<string>();
        foreach (var row in generatedRows)
        {
            var memberId = int.Parse(row[0]!);
            if (memberId == SentinelId)
            {
                continue; // MemberSentinelTests checks it with its own values
            }

            var generatedCode = row[1];

            if (!accessByMemberId.TryGetValue(memberId, out var accessCode))
            {
                mismatches.Add($"MemberID={memberId}: does not exist in Access.Member (unexpected)");
                continue;
            }

            if (!string.Equals(generatedCode, accessCode, StringComparison.Ordinal))
            {
                mismatches.Add($"MemberID={memberId}: generated='{generatedCode ?? "NULL"}' vs Access='{accessCode ?? "NULL"}'");
            }
        }

        Assert.True(
            mismatches.Count == 0,
            $"{mismatches.Count} of {generatedRows.Count} mMember.MemberXBRLCode rows " +
            $"differ from the Access verbatim. Examples: {string.Join("; ", mismatches.Take(20))}");
    }

    // ------------------------------------------------------------------
    // mConcept.ReleaseID: NULL except Taxonomy / Dimension / Member
    // ------------------------------------------------------------------

    [DataFact]
    public void ConceptReleaseId_Taxonomy_MatchesOwnDpmPackageCode()
    {
        var taxonomyByConceptId = _fixture.SelectedTaxonomies
            .Where(t => t.ConceptId.HasValue)
            .ToDictionary(t => t.ConceptId!.Value, t => t.DpmPackageCode);

        var rows = QueryHelpers.Rows(
            _fixture.GeneratedConnection,
            """
            SELECT c."ConceptID", c."ReleaseID", r."ReleaseCode"
            FROM "mConcept" c
            LEFT JOIN "mRelease" r ON r."ReleaseID" = c."ReleaseID"
            WHERE c."ConceptType" = 'Taxonomy'
            """,
            3);

        Assert.True(
            rows.Count > 0,
            "There are no concepts with ConceptType='Taxonomy' in mConcept; at least the one for COREP 3.2 was expected.");

        var failures = new List<string>();
        foreach (var row in rows)
        {
            var conceptId = int.Parse(row[0]!);
            var releaseId = row[1];
            var releaseCode = row[2];

            if (releaseId is null)
            {
                failures.Add($"ConceptID={conceptId}: ReleaseID is NULL (every Taxonomy must have one)");
                continue;
            }

            if (!taxonomyByConceptId.TryGetValue(conceptId, out var expectedPackageCode))
            {
                failures.Add($"ConceptID={conceptId}: does not correspond to any selected taxonomy (unexpected)");
                continue;
            }

            if (!string.Equals(releaseCode, expectedPackageCode, StringComparison.OrdinalIgnoreCase))
            {
                failures.Add($"ConceptID={conceptId}: ReleaseCode='{releaseCode}' vs Taxonomy.DpmPackageCode='{expectedPackageCode}'");
            }
        }

        Assert.True(
            failures.Count == 0,
            $"{failures.Count} ReleaseID inconsistencies in Taxonomy concepts. Examples: " +
            string.Join("; ", failures.Take(10)));
    }

    [DataFact]
    public void ConceptReleaseId_DimensionAndMember_EquivalenceWithOwnXbrlSuffix_BothDirections()
    {
        var dimensionXbrlByConceptId = QueryHelpers.Rows(
                _fixture.GeneratedConnection,
                "SELECT \"ConceptID\", \"DimensionXBRLCode\" FROM \"mDimension\" WHERE \"ConceptID\" IS NOT NULL",
                2)
            .ToDictionary(r => int.Parse(r[0]!), r => r[1]);

        var memberXbrlByConceptId = QueryHelpers.Rows(
                _fixture.GeneratedConnection,
                "SELECT \"ConceptID\", \"MemberXBRLCode\" FROM \"mMember\" WHERE \"ConceptID\" IS NOT NULL",
                2)
            .ToDictionary(r => int.Parse(r[0]!), r => r[1]);

        var conceptRows = QueryHelpers.Rows(
            _fixture.GeneratedConnection,
            """
            SELECT c."ConceptID", c."ConceptType", c."ReleaseID", r."ReleaseCode"
            FROM "mConcept" c
            LEFT JOIN "mRelease" r ON r."ReleaseID" = c."ReleaseID"
            WHERE c."ConceptType" IN ('Dimension', 'Member')
            """,
            4);

        Assert.True(conceptRows.Count > 0, "There are no concepts with ConceptType IN ('Dimension','Member') in mConcept.");

        var failures = new List<string>();
        foreach (var row in conceptRows)
        {
            var conceptId = int.Parse(row[0]!);
            var conceptType = row[1]!;
            var releaseId = row[2];
            var releaseCode = row[3];

            string? xbrlCode;
            bool found;
            if (conceptType == "Dimension")
            {
                found = dimensionXbrlByConceptId.TryGetValue(conceptId, out xbrlCode);
            }
            else
            {
                found = memberXbrlByConceptId.TryGetValue(conceptId, out xbrlCode);
            }

            if (!found)
            {
                failures.Add($"ConceptID={conceptId} ({conceptType}): no corresponding row in m{conceptType}");
                continue;
            }

            var expectedSuffix = ExtractReleaseSuffix(xbrlCode);

            if (expectedSuffix is null && releaseId is not null)
            {
                failures.Add(
                    $"ConceptID={conceptId} ({conceptType}, XBRL='{xbrlCode}'): no suffix but " +
                    $"ReleaseID={releaseId} is not NULL (the 'no suffix => NULL' direction fails)");
            }
            else if (expectedSuffix is not null && releaseId is null)
            {
                failures.Add(
                    $"ConceptID={conceptId} ({conceptType}, XBRL='{xbrlCode}'): with suffix '{expectedSuffix}' " +
                    "but ReleaseID is NULL (the 'suffix => ReleaseID' direction fails)");
            }
            else if (expectedSuffix is not null && releaseId is not null
                && !string.Equals(releaseCode, expectedSuffix, StringComparison.Ordinal))
            {
                failures.Add(
                    $"ConceptID={conceptId} ({conceptType}, XBRL='{xbrlCode}'): suffix='{expectedSuffix}' " +
                    $"vs ReleaseCode='{releaseCode}' (the ReleaseCode pointed to IS NOT the suffix)");
            }
        }

        Assert.True(
            failures.Count == 0,
            $"{failures.Count} XBRL suffix <-> ReleaseID inconsistencies in Dimension/Member " +
            $"(equivalence in both directions). Examples: {string.Join("; ", failures.Take(20))}");
    }

    [DataFact]
    public void ConceptReleaseId_AllOtherConceptTypes_AreAlwaysNull()
    {
        var rows = QueryHelpers.Rows(
            _fixture.GeneratedConnection,
            """
            SELECT "ConceptType", COUNT(*) FROM "mConcept"
            WHERE "ConceptType" NOT IN ('Taxonomy', 'Dimension', 'Member') AND "ReleaseID" IS NOT NULL
            GROUP BY "ConceptType"
            """,
            2);

        Assert.True(
            rows.Count == 0,
            "ConceptType(s) with a non-null ReleaseID outside Taxonomy/Dimension/Member: " +
            string.Join(", ", rows.Select(r => $"{r[0]}={r[1]}")));
    }

    [DataFact]
    public void ConceptReleaseId_Hierarchy_IsAlwaysNull_ByDeliberateGap()
    {
        // The 4.x references set ReleaseID on about 2/3 of the hierarchies, but Hierarchy has no
        // XBRL code nor release column in the Access, so there is no derivable criterion. This
        // test pins that expectation EXPLICITLY: if a criterion ever appears, this test is
        // updated together with the decision, never the other way round.
        var count = QueryHelpers.Scalar(
            _fixture.GeneratedConnection,
            """SELECT COUNT(*) FROM "mConcept" WHERE "ConceptType" = 'Hierarchy' AND "ReleaseID" IS NOT NULL""");

        Assert.True(count == 0, $"{count} Hierarchy concepts with a non-null ReleaseID; this contradicts the ReleaseID rule.");
    }

    [DataFact]
    public void ConceptReleaseId_Domain_IsAlwaysNull()
    {
        var count = QueryHelpers.Scalar(
            _fixture.GeneratedConnection,
            """SELECT COUNT(*) FROM "mConcept" WHERE "ConceptType" = 'Domain' AND "ReleaseID" IS NOT NULL""");

        Assert.True(count == 0, $"{count} Domain concepts with a non-null ReleaseID; this contradicts the ReleaseID rule.");
    }

    [DataFact]
    public void ConceptReleaseId_HierarchyNode_IsAlwaysNull()
    {
        var count = QueryHelpers.Scalar(
            _fixture.GeneratedConnection,
            """SELECT COUNT(*) FROM "mConcept" WHERE "ConceptType" = 'HierarchyNode' AND "ReleaseID" IS NOT NULL""");

        Assert.True(count == 0, $"{count} HierarchyNode concepts with a non-null ReleaseID; this contradicts the ReleaseID rule.");
    }

    [DataFact]
    public void ConceptReleaseId_NonNull_HasRowInMRelease_NoOrphans()
    {
        // Redundant with IntegrityTests.NoOrphanForeignKeys(mConcept, ReleaseID, mRelease,
        // ReleaseID), but repeated here as an explicit part of the guard on this rule: "Every
        // non-null ReleaseID has a row in mRelease (no orphans)".
        QueryHelpers.AssertNoOrphans(_fixture.GeneratedConnection, "mConcept", "ReleaseID", "mRelease", "ReleaseID");
    }

    // ------------------------------------------------------------------
    // Cross-cutting invariant (measured in the 4.0 and 4.2 references themselves): evidence
    // that supports the rule against someone reverting it by intuition.
    // ------------------------------------------------------------------

    private void AssertSuffixReleaseIdInvariant(SqliteConnection connection, string dbLabel, string table, string xbrlColumn)
    {
        var rows = QueryHelpers.Rows(
            connection,
            $"""
            SELECT t."{xbrlColumn}", c."ReleaseID", r."ReleaseCode"
            FROM "{table}" t
            JOIN "mConcept" c ON c."ConceptID" = t."ConceptID"
            LEFT JOIN "mRelease" r ON r."ReleaseID" = c."ReleaseID"
            WHERE t."{xbrlColumn}" IS NOT NULL
            """,
            3);

        Assert.True(rows.Count > 0, $"{dbLabel}.{table}.{xbrlColumn}: 0 rows with a non-null XBRL code (unexpected).");

        var failures = new List<string>();
        foreach (var row in rows)
        {
            var xbrlCode = row[0]!;
            var releaseId = row[1];
            var releaseCode = row[2];
            var suffix = ExtractReleaseSuffix(xbrlCode);

            if (suffix is null && releaseId is not null)
            {
                failures.Add($"{xbrlCode}: no suffix but ReleaseID={releaseId} is non-null");
            }
            else if (suffix is not null && releaseId is null)
            {
                failures.Add($"{xbrlCode}: with suffix '{suffix}' but ReleaseID is NULL");
            }
            else if (suffix is not null && releaseId is not null && !string.Equals(releaseCode, suffix, StringComparison.Ordinal))
            {
                failures.Add($"{xbrlCode}: suffix='{suffix}' vs ReleaseCode='{releaseCode}'");
            }
        }

        Assert.True(
            failures.Count == 0,
            $"{dbLabel}.{table}.{xbrlColumn}: {failures.Count} suffix<->ReleaseID inconsistencies IN THE " +
            $"REFERENCE ITSELF (evidence for the rule). Examples: {string.Join("; ", failures.Take(20))}");
    }

    [DataFact]
    public void Reference40_DimensionXbrlSuffix_XorReleaseId()
        => AssertSuffixReleaseIdInvariant(_fixture.Reference40Connection, "EBA_4.0_ERRATA_5.db", "mDimension", "DimensionXBRLCode");

    [DataFact]
    public void Reference40_MemberXbrlSuffix_XorReleaseId()
        => AssertSuffixReleaseIdInvariant(_fixture.Reference40Connection, "EBA_4.0_ERRATA_5.db", "mMember", "MemberXBRLCode");

    [DataFact]
    public void Reference42_DimensionXbrlSuffix_XorReleaseId()
        => AssertSuffixReleaseIdInvariant(_fixture.Reference42Connection, "EBA_4.2_Hotfix.db", "mDimension", "DimensionXBRLCode");

    [DataFact]
    public void Reference42_MemberXbrlSuffix_XorReleaseId()
        => AssertSuffixReleaseIdInvariant(_fixture.Reference42Connection, "EBA_4.2_Hotfix.db", "mMember", "MemberXBRLCode");

    [DataFact]
    public void Reference32_HasNoReleaseIdColumn_IsNotAFailure()
    {
        // EBA_3.2_phase_1.db has no ReleaseID in mConcept. This is documented explicitly so that
        // a future change of the reference (that DID add the column) gets noticed, instead of
        // assuming forever that "3.2 does not apply".
        var columns = QueryHelpers.Rows(_fixture.Reference32Connection, "PRAGMA table_info('mConcept')", 6);
        var hasReleaseId = columns.Any(c => string.Equals(c[1], "ReleaseID", StringComparison.OrdinalIgnoreCase));

        Assert.False(
            hasReleaseId,
            "EBA_3.2_phase_1.db has a ReleaseID column in mConcept: this contradicts the assumption " +
            "that release 3.2 carries no ReleaseID. This is NOT a converter failure, but it requires " +
            "extending this test with the cross-cutting invariant over 3.2 as well, and revisiting the rule.");
    }
}
