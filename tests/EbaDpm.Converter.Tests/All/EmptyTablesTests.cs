using EbaDpm.Converter.Tests.Dictionary;

namespace EbaDpm.Converter.Tests.All;

/// <summary>
/// A-VAC-01/02/03, over <c>--all</c>: tables that must stay empty because they have no source in
/// the Access database or are deliberately out of scope (validation rules).
/// </summary>
[Collection("All")]
[Trait("Tier", "RealData")]
public sealed class EmptyTablesTests
{
    private readonly AllFixture _fixture;

    public EmptyTablesTests(AllFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>A-VAC-01: the 18 tables with no source in the Access (or out of scope) must have 0 rows, even when converting all 128 taxonomies.</summary>
    [DataTheory]
    [InlineData("vValidationRuleExpressions")]
    [InlineData("vValidationRuleTables")]
    [InlineData("mConceptReference")]
    [InlineData("mReference")]
    [InlineData("mReferencePart")]
    [InlineData("mReferenceValue")]
    [InlineData("mNamespacePrefix")]
    [InlineData("mResourceFile")]
    [InlineData("mRewriteURI")]
    [InlineData("mTaxonomyPackage")]
    [InlineData("mXbrlExportConfiguration")]
    [InlineData("aContainerInfo")]
    [InlineData("aDDSInfo")]
    [InlineData("aDatabaseProperties")]
    [InlineData("dInstance")]
    [InlineData("dFilingIndicator")]
    [InlineData("mCustomDataType")]
    [InlineData("mDomainUnion")]
    public void Table_IsEmpty_EvenOnAll(string tableName)
    {
        var count = QueryHelpers.Scalar(_fixture.GeneratedConnection, $"SELECT COUNT(*) FROM \"{tableName}\"");
        Assert.True(count == 0, $"{tableName} has {count} rows (0 expected, A-VAC-01, even over --all).");
    }

    /// <summary>A-VAC-02: mOwnerParent has exactly 1 row.</summary>
    [DataFact]
    public void MOwnerParent_HasExactlyOneRow()
    {
        var count = QueryHelpers.Scalar(_fixture.GeneratedConnection, "SELECT COUNT(*) FROM \"mOwnerParent\"");
        Assert.True(count == 1, $"mOwnerParent has {count} rows (exactly 1 expected, A-VAC-02).");
    }

    /// <summary>
    /// A-VAC-03: the schema has exactly 45 tables and 12 indexes (the <c>sqlite_autoindex_*</c> of
    /// the composite PKs). The 8 AUXILIARY indexes that <see cref="AllFixture"/> itself creates so
    /// that the invariant battery finishes in a reasonable time are filtered out: they are test
    /// hygiene, never part of the schema contract, and must not be counted here.
    /// </summary>
    [DataFact]
    public void Schema_Has45Tables_And12AutoIndexes_ExcludingTestOnlyIndexes()
    {
        var tableCount = QueryHelpers.Scalar(
            _fixture.GeneratedConnection,
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'");
        Assert.True(tableCount == 45, $"The schema has {tableCount} tables (exactly 45 expected, A-VAC-03).");

        var autoIndexCount = QueryHelpers.Scalar(
            _fixture.GeneratedConnection,
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name LIKE 'sqlite_autoindex_%'");
        Assert.True(autoIndexCount == 12, $"The schema has {autoIndexCount} sqlite_autoindex_* indexes (exactly 12 expected, A-VAC-03).");

        var nonContractIndexes = QueryHelpers.Scalar(
            _fixture.GeneratedConnection,
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name NOT LIKE 'sqlite_autoindex_%' AND name NOT LIKE 'ix_test_%'");
        Assert.True(nonContractIndexes == 0, $"{nonContractIndexes} indexes outside the schema contract and outside the test auxiliaries (review SchemaCreator).");
    }
}
