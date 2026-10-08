using System.Text.RegularExpressions;
using EbaDpm.Converter.Tests.Dictionary;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Tests.Dpm2;

/// <summary>
/// Verification of <c>mConcept</c> and <c>mConceptTranslation</c> for the DPM 2.0 source. Reuses
/// <see cref="Dpm20SkeletonFixture"/> (collection <c>Dpm2Skeleton</c>): the same <c>--all</c>
/// conversion always runs <c>Dpm20ConceptLoader.Load</c>, as the final phase after the six
/// loaders, so the large DPM 2.0 Access database does not need to be read again.
///
/// There is no golden comparison applicable to these two tables (DPM 2.0 has no <c>Concept</c>
/// table to copy, and the reference IDs are not comparable): what is checked here are the INTERNAL
/// INVARIANTS - integrity, census by type, fidelity of the <c>label</c>-role translation against
/// the label ALREADY EMITTED by each table, the 7 concepts without a translation named one by one,
/// <c>Role='description'</c> in zero rows (declared reference divergence DD-22) and the
/// <c>ReleaseID</c> derivation.
/// </summary>
[Collection("Dpm2Skeleton")]
[Trait("Tier", "RealData")]
public sealed class Dpm20ConceptTests(Dpm20SkeletonFixture fixture)
{
    private SqliteConnection Connection => fixture.GeneratedConnection;

    /// <summary>The thirteen tables that feed <c>mConcept</c>, with their PK and their <c>ConceptType</c>.</summary>
    public static IEnumerable<object[]> SourceTables() =>
    [
        ["mRelease", "ReleaseID", "Release"],
        ["mReportingFramework", "FrameworkID", "ReportingFramework"],
        ["mTaxonomy", "TaxonomyID", "Taxonomy"],
        ["mDomain", "DomainID", "Domain"],
        ["mMember", "MemberID", "Member"],
        ["mDimension", "DimensionID", "Dimension"],
        ["mHierarchy", "HierarchyID", "Hierarchy"],
        ["mHierarchyNode", "HierarchyNodeID", "HierarchyNode"],
        ["mAxis", "AxisID", "Axis"],
        ["mAxisOrdinate", "OrdinateID", "Ordinate"],
        ["mTemplateOrTable", "TemplateOrTableID", "TemplateOrTable"],
        ["mTable", "TableID", "Table"],
        ["mModule", "ModuleID", "Module"],
    ];

    // ------------------------------------------------------------------
    // 1 - Counts: 57,077 mConcept, 57,070 mConceptTranslation, census by type.
    // ------------------------------------------------------------------

    // The sentinel domain 9999 ("", 'Open') carries NO concept - the reference leaves it NULL in
    // mDomain (the only such row of the whole table) while it DOES give a concept to the sentinel
    // member. It is a measured, real asymmetry, not a defect: mConcept is 1 below the sum of the
    // thirteen tables, and mDomain stays at 150 rows with only 149 concepts.
    //
    // After the abstract-closure rule: 57,039 -&gt; 57,077 and 57,032 -&gt; 57,070 (+38 in both, the
    // same figure: the new mTable/mAxis/mAxisOrdinate rows that the rule adds are translated like any
    // other, with no extra concepts without a translation). Re-measured against the real output
    // after the change.
    // Dated/scoped to "DPM2 Database_v 4_2_1.accdb" converted with cutoff 4.2: 57,077 -> 57,081 (+4:
    // the release concept 4.2.1 and 3 members introduced by 4.2.1, qTR:qx2065..qx2067; the dictionary tables
    // are not versioned, so a cutoff-4.2 conversion keeps them). Measured on the Access source.
    [DataFact]
    public void MConcept_Has57081Rows_57082Minus1ForTheSentinelDomain() => Assert.Equal(57081, Scalar("SELECT COUNT(*) FROM \"mConcept\""));

    [DataFact]
    // 57,070 -> 57,073 on "DPM2 Database_v 4_2_1.accdb" (cutoff 4.2): +3 labelled members of 4.2.1 (the 4.2.1 release concept has no label).
    public void MConceptTranslation_Has57073Rows_57081Minus8() =>
        Assert.Equal(57073, Scalar("SELECT COUNT(*) FROM \"mConceptTranslation\""));

    public static IEnumerable<object[]> ExpectedConceptTypeCounts() =>
    [
        ["HierarchyNode", 16538],
        ["Ordinate", 20712], // 20,679 before the abstract-closure rule (+33: the ordinates of C_34.02.b/if 4.2)
        ["Member", 12952], // 12,949 before the 4.2.1 file: +3 members (qTR:qx2065..qx2067) of "DPM2 Database_v 4_2_1.accdb"
        ["Axis", 1993], // 1,990 before the abstract-closure rule (+3: the axes of C_34.02.b/if 4.2)
        ["Hierarchy", 1154],
        ["TemplateOrTable", 1530], // 1,529 before the abstract-closure rule (+1: the BusinessTable of C_34.02.b/if 4.2)
        ["Dimension", 1100],
        ["Table", 847], // 846 before the abstract-closure rule (+1: the pair (if 4.2, C_34.02.b))
        ["Domain", 149], // mDomain has 150 rows, but the sentinel 9999 carries no concept.
        ["Module", 50],
        ["Taxonomy", 32],
        ["ReportingFramework", 18],
        ["Release", 6], // 5 before the 4.2.1 file: [Release] now also declares 4.2.1 ("DPM2 Database_v 4_2_1.accdb")
    ];

    [DataTheory]
    [MemberData(nameof(ExpectedConceptTypeCounts))]
    public void MConcept_ConceptType_HasTheMeasuredCount(string conceptType, int expected)
    {
        var actual = Scalar($"SELECT COUNT(*) FROM \"mConcept\" WHERE \"ConceptType\" = '{conceptType}'");
        Assert.Equal(expected, actual);
    }

    [DataFact]
    public void MConcept_UsesExactlyThirteenConceptTypeValues_ValidationRuleIsOutOfScope()
    {
        Assert.Equal(13, Scalar("SELECT COUNT(DISTINCT \"ConceptType\") FROM \"mConcept\""));
        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM \"mConcept\" WHERE \"ConceptType\" = 'ValidationRule'"));
    }

    /// <summary>
    /// One concept per emitted entity, checked TYPE BY TYPE (not as a total): a total that adds up
    /// can hide two types that compensate each other. Thirteen assertions, one per source table.
    /// Named exception by business key: <c>mDomain</c> is the only one of the thirteen where table
    /// and concept do NOT match 1:1 - the sentinel domain 9999 ("", 'Open') carries no concept, so
    /// its 150 rows give 149 concepts of type <c>Domain</c>.
    /// </summary>
    [DataTheory]
    [MemberData(nameof(SourceTables))]
    public void MConcept_CensusByType_MatchesExactlyTheCountOfItsSourceTable(
        string table, string idColumn, string conceptType)
    {
        _ = idColumn;
        var tableCount = Scalar($"SELECT COUNT(*) FROM \"{table}\"");
        var conceptCount = Scalar($"SELECT COUNT(*) FROM \"mConcept\" WHERE \"ConceptType\" = '{conceptType}'");

        Assert.True(tableCount > 0, $"{table} is empty: the check would be meaningless.");

        var expectedConceptCount = table == "mDomain" ? tableCount - 1 : tableCount;
        Assert.Equal(expectedConceptCount, conceptCount);
    }

    // ------------------------------------------------------------------
    // 2 - Integrity: ConceptID never NULL in the thirteen tables, no orphans in any direction,
    // ConceptID unique in mConcept and minted by position (contiguous 1..N).
    // ------------------------------------------------------------------

    /// <summary>
    /// Named exception by business key: in <c>mDomain</c> there is EXACTLY one row with a NULL
    /// <c>ConceptID</c> - the sentinel domain 9999 ("", 'Open'), which carries no concept. The other
    /// twelve tables, zero, without exception.
    /// </summary>
    [DataTheory]
    [MemberData(nameof(SourceTables))]
    public void ConceptID_IsNeverNull_ExceptForTheSentinelDomain(string table, string idColumn, string conceptType)
    {
        _ = idColumn;
        _ = conceptType;
        var nullCount = Scalar($"SELECT COUNT(*) FROM \"{table}\" WHERE \"ConceptID\" IS NULL");
        var expectedNullCount = table == "mDomain" ? 1 : 0;
        Assert.True(nullCount == expectedNullCount, $"{table}: {nullCount} rows with NULL ConceptID, {expectedNullCount} expected.");
    }

    [DataTheory]
    [MemberData(nameof(SourceTables))]
    public void ConceptID_OfTheThirteenTables_HasNoOrphansTowardsMConcept(string table, string idColumn, string conceptType)
    {
        _ = idColumn;
        _ = conceptType;
        QueryHelpers.AssertNoOrphans(Connection, table, "ConceptID", "mConcept", "ConceptID");
    }

    [DataFact]
    public void MConceptTranslation_ConceptID_HasNoOrphansTowardsMConcept()
        => QueryHelpers.AssertNoOrphans(Connection, "mConceptTranslation", "ConceptID", "mConcept", "ConceptID");

    [DataFact]
    public void ConceptID_IsUniqueInMConcept()
    {
        var total = Scalar("SELECT COUNT(*) FROM \"mConcept\"");
        var distinct = Scalar("SELECT COUNT(DISTINCT \"ConceptID\") FROM \"mConcept\"");
        Assert.Equal(total, distinct);
    }

    /// <summary>
    /// Minted BY POSITION - 1..N without gaps. It is checked with MIN/MAX/SUM instead of reading N
    /// rows into memory: if there are N unique IDs between 1 and N whose sum is N(N+1)/2, they are
    /// exactly 1..N - a gap cannot be compensated by a repetition because uniqueness was already
    /// checked separately.
    /// </summary>
    [DataFact]
    public void ConceptID_IsContiguousFrom1ToN_MintedByPosition()
    {
        var count = Scalar("SELECT COUNT(*) FROM \"mConcept\"");
        var min = Scalar("SELECT MIN(\"ConceptID\") FROM \"mConcept\"");
        var max = Scalar("SELECT MAX(\"ConceptID\") FROM \"mConcept\"");
        var sum = Scalar("SELECT SUM(\"ConceptID\") FROM \"mConcept\"");

        Assert.Equal(1, min);
        Assert.Equal(count, max);
        Assert.Equal(count * (count + 1) / 2, sum);
    }

    // ------------------------------------------------------------------
    // 3 - mConceptTranslation, role 'label': the text is the label ALREADY EMITTED, row by row.
    // Explicitly for domains, members, axes, tables and modules, and as a bonus the rest of the
    // types with a label column.
    // ------------------------------------------------------------------

    /// <summary>
    /// The sentinel domain 9999 ("", 'Open') carries no concept, so it has no translation either -
    /// it is excluded, just as it is excluded from <c>mMember</c> below.
    /// </summary>
    [DataFact]
    public void MConceptTranslation_Label_Domain_IsTheAlreadyEmittedLabel_RowByRow_149Of149_ExcludingTheSentinel()
    {
        var sentinelDomainId = Scalar("SELECT \"DomainID\" FROM \"mDomain\" WHERE \"DomainCode\" = '' AND \"DomainLabel\" = 'Open'");
        AssertLabelMatchesEmittedValue(
            "mDomain", "DomainLabel", conceptCountExpected: 150 - 1, extraWhere: $"t.\"DomainID\" <> {sentinelDomainId}");
    }

    [DataFact]
    public void MConceptTranslation_Label_Member_IsTheAlreadyEmittedLabel_RowByRow_ExcludingTheSentinel()
    {
        var sentinelDomainId = Scalar("SELECT \"DomainID\" FROM \"mDomain\" WHERE \"DomainCode\" = '' AND \"DomainLabel\" = 'Open'");
        AssertLabelMatchesEmittedValue(
            "mMember", "MemberLabel", conceptCountExpected: 12952 - 1, extraWhere: $"t.\"DomainID\" <> {sentinelDomainId}");
    }

    // Axis 1,990 -> 1,993, Table 846 -> 847 after the abstract-closure rule - re-measured after the
    // change, see the comment of ExpectedConceptTypeCounts above.
    [DataFact]
    public void MConceptTranslation_Label_Axis_IsTheAlreadyEmittedLabel_RowByRow_1993Of1993()
        => AssertLabelMatchesEmittedValue("mAxis", "AxisLabel", conceptCountExpected: 1993);

    [DataFact]
    public void MConceptTranslation_Label_Table_IsTheAlreadyEmittedLabel_RowByRow_847Of847()
        => AssertLabelMatchesEmittedValue("mTable", "TableLabel", conceptCountExpected: 847);

    [DataFact]
    public void MConceptTranslation_Label_Module_IsTheAlreadyEmittedLabel_RowByRow_50Of50()
        => AssertLabelMatchesEmittedValue("mModule", "ModuleLabel", conceptCountExpected: 50);

    [DataFact]
    public void MConceptTranslation_Label_Dimension_IsTheAlreadyEmittedLabel_RowByRow_ExcludingMET()
        => AssertLabelMatchesEmittedValue(
            "mDimension", "DimensionLabel", conceptCountExpected: 1100 - 1, extraWhere: "t.\"DimensionID\" <> 9999");

    [DataFact]
    public void MConceptTranslation_Label_Hierarchy_IsTheAlreadyEmittedLabel_RowByRow_1154Of1154()
        => AssertLabelMatchesEmittedValue("mHierarchy", "HierarchyLabel", conceptCountExpected: 1154);

    [DataFact]
    public void MConceptTranslation_Label_HierarchyNode_IsTheAlreadyEmittedLabel_RowByRow_16538Of16538()
        => AssertLabelMatchesEmittedValue("mHierarchyNode", "HierarchyNodeLabel", conceptCountExpected: 16538);

    // Ordinate 20,679 -> 20,712, TemplateOrTable 1,529 -> 1,530 after the abstract-closure rule.
    [DataFact]
    public void MConceptTranslation_Label_Ordinate_IsTheAlreadyEmittedLabel_RowByRow_20712Of20712()
        => AssertLabelMatchesEmittedValue("mAxisOrdinate", "OrdinateLabel", conceptCountExpected: 20712);

    [DataFact]
    public void MConceptTranslation_Label_TemplateOrTable_IsTheAlreadyEmittedLabel_RowByRow_1530Of1530()
        => AssertLabelMatchesEmittedValue("mTemplateOrTable", "TemplateOrTableLabel", conceptCountExpected: 1530);

    [DataFact]
    public void MConceptTranslation_Label_ReportingFramework_IsTheAlreadyEmittedLabel_RowByRow_18Of18()
        => AssertLabelMatchesEmittedValue("mReportingFramework", "FrameworkLabel", conceptCountExpected: 18);

    private void AssertLabelMatchesEmittedValue(
        string table, string labelColumn, int conceptCountExpected, string? extraWhere = null)
    {
        var where = extraWhere is null ? string.Empty : $" AND {extraWhere}";

        var expectedRowCount = Scalar($"SELECT COUNT(*) FROM \"{table}\" t WHERE 1 = 1{where}");
        Assert.Equal(conceptCountExpected, expectedRowCount);

        var joinedCount = Scalar(
            $"""
            SELECT COUNT(*) FROM "{table}" t
            JOIN "mConceptTranslation" ct ON ct."ConceptID" = t."ConceptID" AND ct."Role" = 'label'
            WHERE 1 = 1{where}
            """);
        Assert.True(
            joinedCount == expectedRowCount,
            $"{table}: {expectedRowCount - joinedCount} rows without a 'label'-role translation (beyond the 7 named ones).");

        var mismatches = Scalar(
            $"""
            SELECT COUNT(*) FROM "{table}" t
            JOIN "mConceptTranslation" ct ON ct."ConceptID" = t."ConceptID" AND ct."Role" = 'label'
            WHERE ct."Text" IS NOT t."{labelColumn}"{where}
            """);
        Assert.True(mismatches == 0, $"{table}.{labelColumn}: {mismatches} translations that do NOT match the emitted label.");
    }

    // ------------------------------------------------------------------
    // 4 - The 7 concepts without a translation, NAMED ONE BY ONE, not just counted.
    // ------------------------------------------------------------------

    [DataFact]
    public void MConceptTranslation_TheSevenConceptsWithoutALabel_AreExactlyTheNamedOnes()
    {
        var unlabeled = QueryHelpers.Rows(
            Connection,
            """
            SELECT c."ConceptID", c."ConceptType"
            FROM "mConcept" c
            WHERE NOT EXISTS (SELECT 1 FROM "mConceptTranslation" ct WHERE ct."ConceptID" = c."ConceptID")
            ORDER BY c."ConceptID"
            """,
            2);

        // 8 on "DPM2 Database_v 4_2_1.accdb" (7 before: [Release] now declares 6 releases).
        Assert.Equal(8, unlabeled.Count);

        // (1) to (6): the 6 releases - mRelease has no label column, there is no row to emit.
        var releaseConceptIds = unlabeled.Where(r => r[1] == "Release").Select(r => r[0]).ToHashSet();
        Assert.Equal(6, releaseConceptIds.Count);

        var actualReleaseConceptIdsFromMRelease = QueryHelpers.Rows(Connection, "SELECT \"ConceptID\" FROM \"mRelease\" ORDER BY \"ReleaseID\"", 1)
            .Select(r => r[0]).ToHashSet();
        Assert.Equal(actualReleaseConceptIdsFromMRelease, releaseConceptIds);

        // (6): the MET dimension, DimensionID = 9999.
        var metConceptId = ScalarString("SELECT \"ConceptID\" FROM \"mDimension\" WHERE \"DimensionID\" = 9999");
        Assert.NotNull(metConceptId);
        Assert.Contains(unlabeled, r => r[0] == metConceptId && r[1] == "Dimension");

        // (7): the "Open" sentinel member: DomainCode='', DomainLabel='Open'.
        var sentinelConceptId = ScalarString(
            """
            SELECT m."ConceptID" FROM "mMember" m
            JOIN "mDomain" d ON d."DomainID" = m."DomainID"
            WHERE d."DomainCode" = '' AND d."DomainLabel" = 'Open'
            """);
        Assert.NotNull(sentinelConceptId);
        Assert.Contains(unlabeled, r => r[0] == sentinelConceptId && r[1] == "Member");

        // No other type appears among the 7.
        Assert.All(unlabeled, r => Assert.True(
            r[1] is "Release" or "Dimension" or "Member",
            $"ConceptID={r[0]} of type {r[1]} has no translation and is NOT one of the 7 named ones."));
    }

    // ------------------------------------------------------------------
    // 5 - DD-22: Role='description' in ZERO rows (the 70 of the reference are not emitted).
    // ------------------------------------------------------------------

    [DataFact]
    public void MConceptTranslation_Role_Description_HasZeroRows_DD22()
        => Assert.Equal(0, Scalar("SELECT COUNT(*) FROM \"mConceptTranslation\" WHERE \"Role\" = 'description'"));

    [DataFact]
    public void MConceptTranslation_Role_TakesOnlyTheValueLabel()
    {
        var roles = QueryHelpers.Rows(Connection, "SELECT DISTINCT \"Role\" FROM \"mConceptTranslation\"", 1)
            .Select(r => r[0]).ToList();
        Assert.Equal(["label"], roles);
    }

    // ------------------------------------------------------------------
    // 6 - ReleaseID: NULL in the ten non-versioned types (with Hierarchy and Release checked
    // EXPLICITLY), NULL for the MET members, and derived from the suffix of the entity's own code
    // for Dimension/Member/Taxonomy - checked row by row with an independent oracle (a regex over
    // the emitted code itself, not the production function).
    // ------------------------------------------------------------------

    public static IEnumerable<object[]> TypesAlwaysNullReleaseId() =>
    [
        ["Release"],
        ["ReportingFramework"],
        ["Domain"],
        ["Hierarchy"],
        ["HierarchyNode"],
        ["Axis"],
        ["Ordinate"],
        ["TemplateOrTable"],
        ["Table"],
        ["Module"],
    ];

    [DataTheory]
    [MemberData(nameof(TypesAlwaysNullReleaseId))]
    public void MConcept_ReleaseID_IsAlwaysNull_ForTheTenNonVersionedTypes(string conceptType)
    {
        var nonNull = Scalar($"SELECT COUNT(*) FROM \"mConcept\" WHERE \"ConceptType\" = '{conceptType}' AND \"ReleaseID\" IS NOT NULL");
        Assert.Equal(0, nonNull);
    }

    /// <summary>Explicit: Hierarchy goes to NULL (the field has no counterpart in our output).</summary>
    [DataFact]
    public void MConcept_ReleaseID_Hierarchy_IsAlwaysNull()
    {
        var total = Scalar("SELECT COUNT(*) FROM \"mConcept\" WHERE \"ConceptType\" = 'Hierarchy'");
        Assert.Equal(1154, total);
        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM \"mConcept\" WHERE \"ConceptType\" = 'Hierarchy' AND \"ReleaseID\" IS NOT NULL"));
    }

    /// <summary>Explicit: Release goes to NULL (a release does not point to itself).</summary>
    [DataFact]
    public void MConcept_ReleaseID_Release_IsAlwaysNull()
    {
        var total = Scalar("SELECT COUNT(*) FROM \"mConcept\" WHERE \"ConceptType\" = 'Release'");
        Assert.Equal(6, total); // 5 before "DPM2 Database_v 4_2_1.accdb" ([Release] now also declares 4.2.1)
        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM \"mConcept\" WHERE \"ConceptType\" = 'Release' AND \"ReleaseID\" IS NOT NULL"));
    }

    /// <summary>Explicit: the MET members go to NULL (a deliberate consequence of how the metric domain is modelled).</summary>
    [DataFact]
    public void MConcept_ReleaseID_MetMembers_AreAlwaysNull()
    {
        var metMemberCount = Scalar(
            "SELECT COUNT(*) FROM \"mMember\" m JOIN \"mDomain\" d ON d.\"DomainID\" = m.\"DomainID\" WHERE d.\"DomainCode\" = 'MET'");
        Assert.True(metMemberCount > 0, "There are no members of the MET domain: the check would be meaningless.");

        var nonNull = Scalar(
            """
            SELECT COUNT(*) FROM "mMember" m
            JOIN "mDomain" d ON d."DomainID" = m."DomainID"
            JOIN "mConcept" c ON c."ConceptID" = m."ConceptID"
            WHERE d."DomainCode" = 'MET' AND c."ReleaseID" IS NOT NULL
            """);
        Assert.Equal(0, nonNull);
    }

    /// <summary>
    /// Independent oracle (a regex, not the production function) of the <c>_{release}:</c> suffix
    /// of <c>DimensionXBRLCode</c>/<c>MemberXBRLCode</c>. The captured group is resolved against
    /// <c>mRelease.ReleaseCode</c> as it is IN THIS SAME generated database.
    /// </summary>
    private static readonly Regex XbrlColonSuffixPattern = new(@"_([^_:]+):", RegexOptions.Compiled);

    private static string? ExtractCandidateReleaseCode(string? xbrlCode)
    {
        if (string.IsNullOrEmpty(xbrlCode))
        {
            return null;
        }

        var match = XbrlColonSuffixPattern.Match(xbrlCode);
        return match.Success ? match.Groups[1].Value : null;
    }

    private Dictionary<string, int> ReadReleaseIdByCode() =>
        QueryHelpers.Rows(Connection, "SELECT \"ReleaseCode\", \"ReleaseID\" FROM \"mRelease\"", 2)
            .ToDictionary(r => r[0]!, r => int.Parse(r[1]!), StringComparer.Ordinal);

    [DataFact]
    public void MConcept_ReleaseID_Dimension_ComesFromTheSuffixOfItsOwnCode_RowByRow_ExcludingMET()
    {
        var releaseIdByCode = ReadReleaseIdByCode();
        var rows = QueryHelpers.Rows(
            Connection,
            """
            SELECT d."DimensionID", d."DimensionXBRLCode", c."ReleaseID"
            FROM "mDimension" d JOIN "mConcept" c ON c."ConceptID" = d."ConceptID"
            WHERE d."DimensionID" <> 9999
            """,
            3);

        Assert.Equal(1099, rows.Count);
        AssertReleaseIdMatchesColonSuffixOracle(rows, releaseIdByCode, "DimensionID");
    }

    [DataFact]
    public void MConcept_ReleaseID_Member_ComesFromTheSuffixOfItsOwnCode_RowByRow_ExcludingTheSentinel()
    {
        var releaseIdByCode = ReadReleaseIdByCode();
        var sentinelDomainId = Scalar("SELECT \"DomainID\" FROM \"mDomain\" WHERE \"DomainCode\" = '' AND \"DomainLabel\" = 'Open'");
        var rows = QueryHelpers.Rows(
            Connection,
            $"""
            SELECT m."MemberID", m."MemberXBRLCode", c."ReleaseID"
            FROM "mMember" m JOIN "mConcept" c ON c."ConceptID" = m."ConceptID"
            WHERE m."DomainID" <> {sentinelDomainId}
            """,
            3);

        Assert.Equal(12951, rows.Count); // 12,948 before "DPM2 Database_v 4_2_1.accdb" (+3 members of 4.2.1)
        AssertReleaseIdMatchesColonSuffixOracle(rows, releaseIdByCode, "MemberID");
    }

    private static void AssertReleaseIdMatchesColonSuffixOracle(
        List<string?[]> rows, Dictionary<string, int> releaseIdByCode, string idLabel)
    {
        var mismatches = new List<string>();
        foreach (var row in rows)
        {
            var candidate = ExtractCandidateReleaseCode(row[1]);
            int? expected = candidate is not null && releaseIdByCode.TryGetValue(candidate, out var rid) ? rid : null;
            int? actual = row[2] is null ? null : int.Parse(row[2]!);

            if (expected != actual)
            {
                mismatches.Add($"{idLabel}={row[0]}: code='{row[1]}', expected={expected?.ToString() ?? "NULL"}, got={actual?.ToString() ?? "NULL"}");
            }
        }

        Assert.True(mismatches.Count == 0, string.Join(" | ", mismatches.Take(20)));
    }

    /// <summary>Independent oracle for <c>Taxonomy</c>: <c>TaxonomyCode</c> = "{framework} {release}" (last space).</summary>
    [DataFact]
    public void MConcept_ReleaseID_Taxonomy_ComesFromTheSuffixOfTheTaxonomyCode_RowByRow()
    {
        var releaseIdByCode = ReadReleaseIdByCode();
        var rows = QueryHelpers.Rows(
            Connection,
            """
            SELECT t."TaxonomyID", t."TaxonomyCode", c."ReleaseID"
            FROM "mTaxonomy" t JOIN "mConcept" c ON c."ConceptID" = t."ConceptID"
            """,
            3);

        Assert.Equal(32, rows.Count);

        var mismatches = new List<string>();
        foreach (var row in rows)
        {
            var taxonomyCode = row[1];
            string? candidate = null;
            if (!string.IsNullOrEmpty(taxonomyCode))
            {
                var lastSpace = taxonomyCode.LastIndexOf(' ');
                if (lastSpace >= 0 && lastSpace < taxonomyCode.Length - 1)
                {
                    candidate = taxonomyCode[(lastSpace + 1)..];
                }
            }

            int? expected = candidate is not null && releaseIdByCode.TryGetValue(candidate, out var rid) ? rid : null;
            int? actual = row[2] is null ? null : int.Parse(row[2]!);

            if (expected != actual)
            {
                mismatches.Add($"TaxonomyID={row[0]}: code='{taxonomyCode}', expected={expected?.ToString() ?? "NULL"}, got={actual?.ToString() ?? "NULL"}");
            }
        }

        Assert.True(mismatches.Count == 0, string.Join(" | ", mismatches.Take(20)));
    }

    // ------------------------------------------------------------------
    // 7 - The 32 taxonomies DO carry a translation row, with a NULL Text (it keeps the shape "one
    // concept, one translation" without inventing text). Reference: 31/31/31 with a NULL Text - ours
    // has 32 because we emit one more taxonomy than the reference (pay 4.1).
    // ------------------------------------------------------------------

    [DataFact]
    public void MConceptTranslation_Taxonomy_All32CarryARowWithNullText()
    {
        Assert.Equal(32, Scalar("SELECT COUNT(*) FROM \"mConcept\" WHERE \"ConceptType\" = 'Taxonomy'"));

        var translationCount = Scalar(
            """
            SELECT COUNT(*) FROM "mConceptTranslation" ct
            JOIN "mConcept" c ON c."ConceptID" = ct."ConceptID"
            WHERE c."ConceptType" = 'Taxonomy'
            """);
        Assert.Equal(32, translationCount);

        var nonNullText = Scalar(
            """
            SELECT COUNT(*) FROM "mConceptTranslation" ct
            JOIN "mConcept" c ON c."ConceptID" = ct."ConceptID"
            WHERE c."ConceptType" = 'Taxonomy' AND ct."Text" IS NOT NULL
            """);
        Assert.Equal(0, nonNullText);

        var nonLabelRole = Scalar(
            """
            SELECT COUNT(*) FROM "mConceptTranslation" ct
            JOIN "mConcept" c ON c."ConceptID" = ct."ConceptID"
            WHERE c."ConceptType" = 'Taxonomy' AND ct."Role" <> 'label'
            """);
        Assert.Equal(0, nonLabelRole);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private long Scalar(string sql) => QueryHelpers.Scalar(Connection, sql);

    private string? ScalarString(string sql)
    {
        using var command = Connection.CreateCommand();
        command.CommandText = sql;
        var result = command.ExecuteScalar();
        return result is null or DBNull ? null : result.ToString();
    }
}
