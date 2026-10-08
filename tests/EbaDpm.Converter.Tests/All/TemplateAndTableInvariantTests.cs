using EbaDpm.Converter.Tests.Dictionary;

namespace EbaDpm.Converter.Tests.All;

/// <summary>Template and table invariants A-TPL-01/08/11/14/15, over <c>--all</c>.</summary>
[Collection("All")]
[Trait("Tier", "RealData")]
public sealed class TemplateAndTableInvariantTests
{
    private readonly AllFixture _fixture;

    public TemplateAndTableInvariantTests(AllFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// A-TPL-01: <c>TemplateOrTableType</c> is in {TableGroup, BusinessTable}; <c>Level</c> is in {1,2}.
    /// Trap: <c>Level</c> is PER TYPE, not depth -- a <c>BusinessTable</c> is <c>Level=1</c>, not 3,
    /// even if its effective parent is a level-2 <c>TableGroup</c>.
    /// </summary>
    [DataFact]
    public void TemplateOrTableType_And_Level_AreWithinTheClosedVocabulary()
    {
        var outsideType = QueryHelpers.Scalar(
            _fixture.GeneratedConnection,
            "SELECT COUNT(*) FROM \"mTemplateOrTable\" WHERE \"TemplateOrTableType\" NOT IN ('TableGroup', 'BusinessTable')");
        Assert.True(outsideType == 0, $"{outsideType} mTemplateOrTable rows with a TemplateOrTableType outside {{TableGroup, BusinessTable}} (A-TPL-01).");

        var outsideLevel = QueryHelpers.Scalar(
            _fixture.GeneratedConnection, "SELECT COUNT(*) FROM \"mTemplateOrTable\" WHERE \"Level\" NOT IN (1, 2)");
        Assert.True(outsideLevel == 0, $"{outsideLevel} mTemplateOrTable rows with a Level outside {{1,2}} (A-TPL-01).");
    }

    /// <summary>A-TPL-08: IsTableGroupSource = 1 in 100 % of mTemplateOrTable.</summary>
    [DataFact]
    public void IsTableGroupSource_IsAlwaysTrue()
    {
        var total = QueryHelpers.Scalar(_fixture.GeneratedConnection, "SELECT COUNT(*) FROM \"mTemplateOrTable\"");
        var trueCount = QueryHelpers.Scalar(_fixture.GeneratedConnection, "SELECT COUNT(*) FROM \"mTemplateOrTable\" WHERE \"IsTableGroupSource\" = 1");

        Assert.True(trueCount == total, $"IsTableGroupSource=1 in {trueCount} of {total} rows (ALL expected, A-TPL-08).");
    }

    /// <summary>A-TPL-11: no mTable table is left without mTaxonomyTable; no taxonomy is left without tables.</summary>
    [DataFact]
    public void NoTable_IsWithoutTaxonomyTable_AndNoTaxonomy_IsWithoutTables()
    {
        var tablesWithoutTaxonomyTable = QueryHelpers.Scalar(
            _fixture.GeneratedConnection,
            """
            SELECT COUNT(*) FROM "mTable" t
            WHERE NOT EXISTS (SELECT 1 FROM "mTaxonomyTable" tt WHERE tt."TableID" = t."TableID")
            """);
        Assert.True(tablesWithoutTaxonomyTable == 0, $"{tablesWithoutTaxonomyTable} mTable tables without any row in mTaxonomyTable (A-TPL-11).");

        var taxonomiesWithoutTables = QueryHelpers.Scalar(
            _fixture.GeneratedConnection,
            """
            SELECT COUNT(*) FROM "mTaxonomy" x
            WHERE NOT EXISTS (SELECT 1 FROM "mTaxonomyTable" tt WHERE tt."TaxonomyID" = x."TaxonomyID")
            """);
        Assert.True(taxonomiesWithoutTables == 0, $"{taxonomiesWithoutTables} taxonomies without any table in mTaxonomyTable (A-TPL-11).");
    }

    /// <summary>
    /// A-TPL-12: ANTI-INVARIANT. <c>mTaxonomyTable</c> is NOT 1:1 with <c>mTable</c> in our
    /// output (there is real reuse between taxonomies). Equality is not required; it is recorded
    /// that the reuse still exists (links &gt; tables).
    /// </summary>
    [DataFact]
    public void TaxonomyTable_IsDeliberatelyNotOneToOne_WithTable()
    {
        var links = QueryHelpers.Scalar(_fixture.GeneratedConnection, "SELECT COUNT(*) FROM \"mTaxonomyTable\"");
        var tables = QueryHelpers.Scalar(_fixture.GeneratedConnection, "SELECT COUNT(*) FROM \"mTable\"");

        Assert.True(
            links > tables,
            $"mTaxonomyTable ({links}) is not greater than mTable ({tables}): real reuse between taxonomies was expected (A-TPL-12, anti-invariant).");
    }

    /// <summary>
    /// Two DISTINCT censuses, measured independently against the Access, without looking at any
    /// reference. <c>mTable</c> stays deduplicated by <c>TableVID</c> (its PK is a single column);
    /// <c>mTemplateOrTable(BusinessTable)</c> has one node for EACH <c>(TaxonomyID, TableVID)</c>
    /// association of <c>Access.TaxonomyTableVersion</c>, no longer deduplicated. It complements
    /// <see cref="TaxonomyTable_IsDeliberatelyNotOneToOne_WithTable"/> (A-TPL-12, which only
    /// checks <c>mTaxonomyTable &gt; mTable</c>) by pinning the two exact figures, not just the
    /// inequality.
    /// </summary>
    [DataFact]
    public void MTable_IsDeduplicatedByTableVId_AndBusinessTableNode_IsOnePerTaxonomyTableVIdPair()
    {
        var selectedTaxonomyIds = _fixture.SelectedTaxonomies.Select(t => t.TaxonomyId).ToHashSet();

        var selectedTaxonomyTableVersions = _fixture.AccessReader.ReadTaxonomyTableVersions()
            .Where(v => selectedTaxonomyIds.Contains(v.TaxonomyId))
            .ToList();

        var expectedTableRows = selectedTaxonomyTableVersions.Select(v => v.TableVId).Distinct().Count();
        var expectedBusinessTableRows = selectedTaxonomyTableVersions
            .Select(v => (v.TaxonomyId, v.TableVId))
            .Distinct()
            .Count();

        // Access.TaxonomyTableVersion does not have a single duplicated (TaxonomyID, TableVID)
        // pair (it would be a data anomaly, not something the mapping should absorb): if this
        // does not add up, the assumption that "one TaxonomyTableVersion row = one association"
        // does not hold.
        Assert.True(
            expectedBusinessTableRows == selectedTaxonomyTableVersions.Count,
            $"Access.TaxonomyTableVersion has {selectedTaxonomyTableVersions.Count} rows but only " +
            $"{expectedBusinessTableRows} distinct (TaxonomyID, TableVID) pairs: there are duplicated pairs " +
            "(unexpected, review the assumption of this invariant).");

        var actualTableRows = QueryHelpers.Scalar(_fixture.GeneratedConnection, "SELECT COUNT(*) FROM \"mTable\"");
        var actualBusinessTableRows = QueryHelpers.Scalar(
            _fixture.GeneratedConnection,
            "SELECT COUNT(*) FROM \"mTemplateOrTable\" WHERE \"TemplateOrTableType\" = 'BusinessTable'");

        Assert.True(
            actualTableRows == expectedTableRows,
            $"mTable has {actualTableRows} rows versus {expectedTableRows} distinct TableVID in " +
            "Access.TaxonomyTableVersion of the selected taxonomies (mTable must stay deduplicated).");
        Assert.True(
            actualBusinessTableRows == expectedBusinessTableRows,
            $"mTemplateOrTable(BusinessTable) has {actualBusinessTableRows} rows versus " +
            $"{expectedBusinessTableRows} distinct (TaxonomyID, TableVID) pairs in Access.TaxonomyTableVersion " +
            "(one BusinessTable node per association, without deduplication, was expected).");
    }

    /// <summary>A-TPL-14: no orphaned mReportingFramework -- every framework is used by at least 1 mTaxonomy.</summary>
    [DataFact]
    public void NoReportingFramework_IsOrphaned_ByAnyTaxonomy()
    {
        var orphanFrameworks = QueryHelpers.Scalar(
            _fixture.GeneratedConnection,
            """
            SELECT COUNT(*) FROM "mReportingFramework" f
            WHERE NOT EXISTS (SELECT 1 FROM "mTaxonomy" t WHERE t."FrameworkID" = f."FrameworkID")
            """);

        Assert.True(orphanFrameworks == 0, $"{orphanFrameworks} of {QueryHelpers.Scalar(_fixture.GeneratedConnection, "SELECT COUNT(*) FROM \"mReportingFramework\"")} frameworks without any taxonomy (A-TPL-14, critical layer).");
    }

    /// <summary>A-TPL-15: every mTaxonomy has an existing FrameworkID.</summary>
    [DataFact]
    public void EveryTaxonomy_HasAnExistingFrameworkId() =>
        QueryHelpers.AssertNoOrphans(_fixture.GeneratedConnection, "mTaxonomy", "FrameworkID", "mReportingFramework", "FrameworkID");
}
