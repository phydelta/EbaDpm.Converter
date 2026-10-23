using EbaDpm.Converter.Tests.Dictionary;
using EbaDpm.Converter.Tests.Infrastructure;
using EbaDpm.Converter.Tests.Schema;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Tests.All;

/// <summary>Hierarchy invariants A-HIE-06/A-HIE-07 and related concept-sharing censuses, over <c>--all</c>.</summary>
[Collection("All")]
[Trait("Tier", "RealData")]
public sealed class HierarchyInvariantTests
{
    private readonly AllFixture _fixture;

    public HierarchyInvariantTests(AllFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// A-HIE-06: every member of a hierarchy node belongs to the DOMAIN of its own hierarchy.
    ///
    /// Note on <c>&lt;&gt;</c>: both <c>mMember.DomainID</c> (<c>AccessMemberRow.DomainId</c>,
    /// <c>int?</c>) and <c>mHierarchy.DomainID</c> (<c>AccessHierarchyRow.DomainId</c>, <c>int?</c>)
    /// are <c>int?</c> in the source and are written verbatim
    /// (<c>DictionaryLoader.LoadMembers</c>/<c>LoadHierarchies</c>): both sides CAN be NULL. With
    /// <c>&lt;&gt;</c>, a row with either side NULL would yield NULL in the comparison and would NOT
    /// be counted as a violation (false green). <c>IS NOT</c> (NULL-safe) is used: if either is
    /// missing, the member cannot be asserted to belong to the domain of its hierarchy, so it
    /// MUST count.
    /// </summary>
    [DataFact]
    public void EveryHierarchyNodeMember_BelongsToTheDomainOfItsOwnHierarchy()
    {
        var violations = QueryHelpers.Scalar(
            _fixture.GeneratedConnection,
            """
            SELECT COUNT(*) FROM "mHierarchyNode" n
            JOIN "mHierarchy" h ON h."HierarchyID" = n."HierarchyID"
            JOIN "mMember" m ON m."MemberID" = n."MemberID"
            WHERE m."DomainID" IS NOT h."DomainID"
            """);

        Assert.True(violations == 0, $"{violations} nodes whose member belongs to a domain different from that of their hierarchy (A-HIE-06).");
    }

    /// <summary>
    /// A-HIE-07. <c>mHierarchyNode</c> has no group of duplicated <c>ConceptID</c>, and its rows
    /// with a NULL <c>ConceptID</c> are pinned (<see cref="ReleasePins.Dpm10All41"/>): rows with a
    /// NULL <c>ConceptID</c> are not a sharing of concepts.
    /// <c>DictionaryLoader.LoadHierarchyNodes</c> emits <c>node.ConceptId</c> verbatim from the
    /// Access, without synthesising anything.
    ///
    /// The REAL case where several rows share a ConceptID (accepted and declared, no new concepts
    /// are synthesised) is <c>mTemplateOrTable</c> (genuine reuse of templates between
    /// taxonomies), not <c>mHierarchyNode</c>. See
    /// <see cref="TemplateOrTable_ConceptId_HasTheMeasuredCensus_AndItsDecomposition"/> and
    /// <see cref="TemplateOrTable_DistinctConceptId_EqualsTableGroupPlusTemplatePlusTableVersionCensus"/>.
    /// </summary>
    [DataFact]
    public void HierarchyNode_ConceptId_HasZeroDuplicates_AndThePinnedNullRows()
    {
        var duplicateGroups = QueryHelpers.Scalar(
            _fixture.GeneratedConnection,
            """
            SELECT COUNT(*) FROM (
                SELECT "ConceptID" FROM "mHierarchyNode"
                WHERE "ConceptID" IS NOT NULL
                GROUP BY "ConceptID" HAVING COUNT(*) > 1
            )
            """);
        Assert.True(duplicateGroups == 0, $"{duplicateGroups} groups of duplicated ConceptID in mHierarchyNode (0 expected, A-HIE-07).");

        var nullConceptIdRows = QueryHelpers.Scalar(_fixture.GeneratedConnection, "SELECT COUNT(*) FROM \"mHierarchyNode\" WHERE \"ConceptID\" IS NULL");
        ReleasePins.AssertPinned(
            ReleasePins.Dpm10All41, "mHierarchyNode rows with a NULL ConceptID", ReleasePins.Dpm10All41HierarchyNodeNullConceptIdRows, nullConceptIdRows);
    }

    /// <summary>
    /// Census of shared <c>ConceptID</c> groups in <c>mTemplateOrTable</c>. Some groups are purely
    /// L1/L2 (<c>TableGroup</c> reused between taxonomies through a shared <c>Template</c>). The
    /// <c>BusinessTable</c> node is not deduplicated: one node is emitted for every
    /// <c>(TaxonomyID, TableVID)</c> association, and all the nodes of the same <c>TableVID</c>
    /// share the <c>ConceptID</c> of <c>TableVersion</c>. That adds one group of repeated
    /// <c>ConceptID</c> for every <c>TableVID</c> shared by several taxonomies.
    ///
    /// Asserted over <see cref="AllFixture"/>: the BusinessTable groups equal the <c>TableVID</c>
    /// shared by more than one taxonomy in <c>Access.TaxonomyTableVersion</c> (derived); the L1/L2
    /// groups and rows and the totals are pinned (<see cref="ReleasePins.Dpm10All41"/>); and the
    /// total equals the L1/L2 half plus the BusinessTable half, so the halves cannot drift apart
    /// from the total by chance.
    /// </summary>
    [DataFact]
    public void TemplateOrTable_ConceptId_HasTheMeasuredCensus_AndItsDecomposition()
    {
        var sharedConcepts = QueryHelpers.Scalar(
            _fixture.GeneratedConnection,
            """
            SELECT COUNT(*) FROM (
                SELECT "ConceptID" FROM "mTemplateOrTable"
                WHERE "ConceptID" IS NOT NULL
                GROUP BY "ConceptID" HAVING COUNT(*) > 1
            )
            """);
        var rowsInSharedGroups = QueryHelpers.Scalar(
            _fixture.GeneratedConnection,
            """
            SELECT COUNT(*) FROM "mTemplateOrTable"
            WHERE "ConceptID" IN (
                SELECT "ConceptID" FROM "mTemplateOrTable"
                WHERE "ConceptID" IS NOT NULL
                GROUP BY "ConceptID" HAVING COUNT(*) > 1
            )
            """);
        var nullConceptIdRows = QueryHelpers.Scalar(_fixture.GeneratedConnection, "SELECT COUNT(*) FROM \"mTemplateOrTable\" WHERE \"ConceptID\" IS NULL");

        var pin = ReleasePins.Dpm10All41;
        ReleasePins.AssertPinned(pin, "mTemplateOrTable groups of shared ConceptID", ReleasePins.Dpm10All41TemplateOrTableSharedConceptGroups, sharedConcepts);
        ReleasePins.AssertPinned(pin, "mTemplateOrTable rows in shared ConceptID groups", ReleasePins.Dpm10All41TemplateOrTableRowsInSharedGroups, rowsInSharedGroups);
        Assert.True(nullConceptIdRows == 0, $"{nullConceptIdRows} mTemplateOrTable rows with a NULL ConceptID (0 expected).");

        // Decomposition: isolates each half so that a regression there is not masked by chance
        // with the total.
        var sharedConceptsBusinessTableOnly = QueryHelpers.Scalar(
            _fixture.GeneratedConnection,
            """
            SELECT COUNT(*) FROM (
                SELECT "ConceptID" FROM "mTemplateOrTable"
                WHERE "TemplateOrTableType" = 'BusinessTable'
                GROUP BY "ConceptID" HAVING COUNT(*) > 1
            )
            """);
        var rowsInSharedGroupsBusinessTableOnly = QueryHelpers.Scalar(
            _fixture.GeneratedConnection,
            """
            SELECT COUNT(*) FROM "mTemplateOrTable"
            WHERE "TemplateOrTableType" = 'BusinessTable' AND "ConceptID" IN (
                SELECT "ConceptID" FROM "mTemplateOrTable"
                WHERE "TemplateOrTableType" = 'BusinessTable'
                GROUP BY "ConceptID" HAVING COUNT(*) > 1
            )
            """);
        var sharedConceptsLevels12Only = QueryHelpers.Scalar(
            _fixture.GeneratedConnection,
            """
            SELECT COUNT(*) FROM (
                SELECT "ConceptID" FROM "mTemplateOrTable"
                WHERE "TemplateOrTableType" <> 'BusinessTable' AND "ConceptID" IS NOT NULL
                GROUP BY "ConceptID" HAVING COUNT(*) > 1
            )
            """);
        var rowsInSharedGroupsLevels12Only = QueryHelpers.Scalar(
            _fixture.GeneratedConnection,
            """
            SELECT COUNT(*) FROM "mTemplateOrTable"
            WHERE "TemplateOrTableType" <> 'BusinessTable' AND "ConceptID" IN (
                SELECT "ConceptID" FROM "mTemplateOrTable"
                WHERE "TemplateOrTableType" <> 'BusinessTable' AND "ConceptID" IS NOT NULL
                GROUP BY "ConceptID" HAVING COUNT(*) > 1
            )
            """);

        // The BusinessTable groups are the TableVID shared by several taxonomies in the Access.
        var accessSharedTableVidCount = AccessSharedTableVidCount();
        Assert.True(
            sharedConceptsBusinessTableOnly == accessSharedTableVidCount,
            $"{sharedConceptsBusinessTableOnly} BusinessTable ConceptID groups shared in mTemplateOrTable versus " +
            $"{accessSharedTableVidCount} TableVID shared by more than one taxonomy in Access.TaxonomyTableVersion " +
            $"(derived from '{RepoPaths.AccessDatabaseFileName}').");
        ReleasePins.AssertPinned(pin, "BusinessTable rows in shared ConceptID groups", ReleasePins.Dpm10All41BusinessTableRowsInSharedGroups, rowsInSharedGroupsBusinessTableOnly);
        ReleasePins.AssertPinned(pin, "L1/L2 groups of shared ConceptID", ReleasePins.Dpm10All41Level12SharedConceptGroups, sharedConceptsLevels12Only);
        ReleasePins.AssertPinned(pin, "L1/L2 rows in shared ConceptID groups", ReleasePins.Dpm10All41Level12RowsInSharedGroups, rowsInSharedGroupsLevels12Only);

        // Identities between the halves and the total (a second measurement of the same census).
        Assert.True(
            sharedConcepts == sharedConceptsLevels12Only + sharedConceptsBusinessTableOnly,
            $"Shared ConceptID groups: total {sharedConcepts} versus L1/L2 {sharedConceptsLevels12Only} + BusinessTable {sharedConceptsBusinessTableOnly}.");
        Assert.True(
            rowsInSharedGroups == rowsInSharedGroupsLevels12Only + rowsInSharedGroupsBusinessTableOnly,
            $"Rows in shared ConceptID groups: total {rowsInSharedGroups} versus L1/L2 {rowsInSharedGroupsLevels12Only} + BusinessTable {rowsInSharedGroupsBusinessTableOnly}.");
    }

    /// <summary>
    /// "No <c>TableID</c> hangs from more than one taxonomy" is verified against the THREE
    /// references -- it describes how THEY are, not our output (for ours, see
    /// <see cref="TaxonomyTable_SharedTableIdCensus_MatchesAccessSharedTableVidCensus_InOurOutput"/>
    /// below). <c>SELECT TableID FROM mTaxonomyTable GROUP BY TableID HAVING COUNT(DISTINCT
    /// TaxonomyID) &gt; 1</c> yields 0 rows in all three (none shares a <c>TableID</c> between
    /// taxonomies; all of them duplicate it).
    ///
    /// Besides "0 mismatches", the CENSUS of rows actually examined in each reference is checked
    /// (that <c>mTaxonomyTable</c> is not empty) -- if it were empty, "0 shared rows" would pass
    /// green without having compared anything.
    /// </summary>
    [DataFact]
    public void TaxonomyTable_TableId_NeverHangsFromMoreThanOneTaxonomy_InTheThreeReferences()
    {
        const string sharedTableIdSql =
            """
            SELECT COUNT(*) FROM (
                SELECT "TableID" FROM "mTaxonomyTable" GROUP BY "TableID" HAVING COUNT(DISTINCT "TaxonomyID") > 1
            )
            """;
        const string totalRowsSql = "SELECT COUNT(*) FROM \"mTaxonomyTable\"";

        RepoPaths.EnsureReference32DatabaseExists();
        RepoPaths.EnsureReference40DatabaseExists();
        RepoPaths.EnsureReferenceDatabaseExists();

        using var reference32 = OpenReadOnly(RepoPaths.Reference32Path);
        using var reference40 = OpenReadOnly(RepoPaths.Reference40Path);
        using var reference42 = OpenReadOnly(RepoPaths.ReferenceDatabasePath);

        var totalRows32 = QueryHelpers.Scalar(reference32, totalRowsSql);
        var totalRows40 = QueryHelpers.Scalar(reference40, totalRowsSql);
        var totalRows42 = QueryHelpers.Scalar(reference42, totalRowsSql);

        Assert.True(
            totalRows32 > 0 && totalRows40 > 0 && totalRows42 > 0,
            $"mTaxonomyTable is empty in some reference (3.2={totalRows32}, 4.0={totalRows40}, " +
            $"4.2={totalRows42}): the comparison would prove nothing.");

        var shared32 = QueryHelpers.Scalar(reference32, sharedTableIdSql);
        var shared40 = QueryHelpers.Scalar(reference40, sharedTableIdSql);
        var shared42 = QueryHelpers.Scalar(reference42, sharedTableIdSql);

        Assert.True(shared32 == 0, $"{shared32} TableID shared between taxonomies in EBA_3.2_phase_1.db (0 expected, over {totalRows32} mTaxonomyTable rows).");
        Assert.True(shared40 == 0, $"{shared40} TableID shared between taxonomies in EBA_4.0_ERRATA_5.db (0 expected, over {totalRows40} mTaxonomyTable rows).");
        Assert.True(shared42 == 0, $"{shared42} TableID shared between taxonomies in EBA_4.2_Hotfix.db (0 expected, over {totalRows42} mTaxonomyTable rows).");
    }

    /// <summary><c>TableVID</c> of <c>Access.TaxonomyTableVersion</c> shared by more than one of the selected taxonomies.</summary>
    private int AccessSharedTableVidCount()
    {
        var selectedTaxonomyIds = _fixture.SelectedTaxonomies.Select(t => t.TaxonomyId).ToHashSet();

        return _fixture.AccessReader.ReadTaxonomyTableVersions()
            .Where(v => selectedTaxonomyIds.Contains(v.TaxonomyId))
            .GroupBy(v => v.TableVId)
            .Count(g => g.Select(v => v.TaxonomyId).Distinct().Count() > 1);
    }

    private static SqliteConnection OpenReadOnly(string path)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString();

        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }

    /// <summary>
    /// "No <c>TableID</c> hangs from more than one taxonomy" is NOT an invariant of the FORMAT --
    /// it is a convention of the reference (whose <c>mTable</c> never reuses and instead duplicates
    /// the whole node with a new <c>ConceptID</c>). In OUR output, <c>mTable</c> stays deduplicated
    /// by <c>TableVID</c> (no <c>ConceptID</c> is invented per copy), so the same <c>TableID</c> DOES
    /// appear in several rows of <c>mTaxonomyTable</c> when several taxonomies share that Access
    /// table version.
    ///
    /// What can be verified is that the CENSUS of shared <c>TableID</c> in our output MATCHES
    /// EXACTLY the census, computed LIVE against the Access (not as a constant), of
    /// <c>TableVID</c> of <c>TaxonomyTableVersion</c> shared by more than one of the selected
    /// taxonomies: "everything the Access says is shared, and only that, appears shared in the
    /// output" -- without looking at any reference.
    /// </summary>
    [DataFact]
    public void TaxonomyTable_SharedTableIdCensus_MatchesAccessSharedTableVidCensus_InOurOutput()
    {
        var accessSharedTableVidCount = AccessSharedTableVidCount();

        var outputSharedTableIdCount = QueryHelpers.Scalar(
            _fixture.GeneratedConnection,
            """
            SELECT COUNT(*) FROM (
                SELECT "TableID" FROM "mTaxonomyTable" GROUP BY "TableID" HAVING COUNT(DISTINCT "TaxonomyID") > 1
            )
            """);

        // Census of mTaxonomyTable rows actually examined, so that "0 == 0" cannot slip through
        // as a false green if the underlying join matched nothing.
        var totalTaxonomyTableRows = QueryHelpers.Scalar(_fixture.GeneratedConnection, "SELECT COUNT(*) FROM \"mTaxonomyTable\"");
        Assert.True(totalTaxonomyTableRows > 0, "mTaxonomyTable is empty over --all: the comparison would be empty.");
        Assert.True(outputSharedTableIdCount > 0, $"0 shared TableID in the output over --all: suspiciously low against the Access ({accessSharedTableVidCount} shared TableVID), review before accepting.");

        Assert.True(
            outputSharedTableIdCount == accessSharedTableVidCount,
            $"{outputSharedTableIdCount} TableID shared by more than one taxonomy in mTaxonomyTable " +
            $"(our output, --all) versus {accessSharedTableVidCount} TableVID of " +
            "Access.TaxonomyTableVersion shared by more than one of the selected taxonomies " +
            "(computed live): they should match exactly, because mTable does not duplicate " +
            "and one mTaxonomyTable row exists for every (TaxonomyID, TableVID) of the Access.");
    }

    /// <summary>
    /// <c>COUNT(DISTINCT ConceptID)</c> in <c>mTemplateOrTable</c> must be EXACTLY the sum of the
    /// three SOURCE censuses -- <c>TableGroup</c> (L1, one concept per row, no sharing) + the
    /// DISTINCT <c>Template</c> actually referenced (L2, shared between several taxonomies/groups)
    /// + the DISTINCT <c>TableVersion</c> emitted (L3, deduplicated by <c>TableVID</c>) -- not one
    /// invented, not one lost. It is checked WITHOUT looking at any reference and detects both
    /// extra and missing concepts.
    ///
    /// The BusinessTable nodes are not deduplicated: that changes HOW MANY ROWS of
    /// mTemplateOrTable share each ConceptID (the test above), not HOW MANY DISTINCT ConceptIDs
    /// there are -- the duplicated nodes keep pointing to the SAME TableVersion ConceptID.
    ///
    /// The censuses on the right-hand side are recomputed here against the Access itself
    /// (reproducing the SAME selection filter used by <c>TemplateOrTableLoader</c>: taxonomy in
    /// <see cref="AllFixture.SelectedTaxonomies"/>), never as hand-written constants.
    /// </summary>
    [DataFact]
    public void TemplateOrTable_DistinctConceptId_EqualsTableGroupPlusTemplatePlusTableVersionCensus()
    {
        var selectedTaxonomyIds = _fixture.SelectedTaxonomies.Select(t => t.TaxonomyId).ToHashSet();

        // L1: one concept per Access.TableGroup of the selected taxonomies (TableGroupId is the
        // PK of that table in the Access, so there are no duplicates to collapse here).
        var tableGroupCensus = _fixture.AccessReader.ReadTableGroups()
            .Count(g => g.TaxonomyId.HasValue && selectedTaxonomyIds.Contains(g.TaxonomyId.Value));

        // L2: DISTINCT Templates actually linked to an emitted TableGroup, through
        // Access.TaxonomyTableVersion -- the exact criterion of TemplateOrTableLoader.LoadLevel2Templates.
        var selectedTaxonomyTableVersions = _fixture.AccessReader.ReadTaxonomyTableVersions()
            .Where(v => selectedTaxonomyIds.Contains(v.TaxonomyId))
            .ToList();

        var linkedTableGroupIds = _fixture.AccessReader.ReadTableGroups()
            .Where(g => g.TaxonomyId.HasValue && selectedTaxonomyIds.Contains(g.TaxonomyId.Value))
            .Select(g => g.TableGroupId)
            .ToHashSet();

        var accessTemplateIds = _fixture.AccessReader.ReadTemplates().Select(t => t.TemplateId).ToHashSet();

        var templateCensus = selectedTaxonomyTableVersions
            .Where(v => v.TemplateId.HasValue && v.TableGroupId.HasValue)
            .Where(v => linkedTableGroupIds.Contains(v.TableGroupId!.Value))
            .Select(v => v.TemplateId!.Value)
            .Where(accessTemplateIds.Contains)
            .Distinct()
            .Count();

        // L3: DISTINCT table versions emitted (deduplicated by TableVID), already computed by the
        // loader itself while loading this fixture -- no need to recompute it, it is the same
        // data that AxisAndCellLoader consumes to resolve TableID.
        var tableVersionCensus = _fixture.TemplateOrTableResult.TableIdByTableVId.Count;

        var expectedDistinctConceptIds = tableGroupCensus + templateCensus + tableVersionCensus;

        var actualDistinctConceptIds = QueryHelpers.Scalar(
            _fixture.GeneratedConnection, "SELECT COUNT(DISTINCT \"ConceptID\") FROM \"mTemplateOrTable\"");

        Assert.True(
            actualDistinctConceptIds == expectedDistinctConceptIds,
            $"mTemplateOrTable has {actualDistinctConceptIds} distinct ConceptID versus " +
            $"{expectedDistinctConceptIds} expected (TableGroup={tableGroupCensus} + Template={templateCensus} " +
            $"+ TableVersion={tableVersionCensus}, censuses measured against the Access, not hand-written constants).");
    }
}
