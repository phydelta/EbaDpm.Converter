using EbaDpm.Converter.Tests.Dictionary;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Tests.All;

/// <summary>
/// A-INT-01 and A-UNQ-01..12, over the <c>--all</c> conversion (128 taxonomies). These are
/// integrity and uniqueness invariants that only show up (or stop showing up) at full scale.
/// </summary>
[Collection("All")]
[Trait("Tier", "RealData")]
public sealed class IntegrityAndUniquenessTests
{
    private readonly AllFixture _fixture;

    public IntegrityAndUniquenessTests(AllFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// A-INT-01: <c>PRAGMA foreign_key_check</c> returns EXACTLY the rows of
    /// <c>mTemplateOrTable</c> with <c>TemplateOrTableType='TableGroup'</c> and <c>Level=1</c> --
    /// those carry <c>ParentTemplateOrTableID = 0</c> by the convention of the 4.x references,
    /// which is a DELIBERATE and documented violation of the declared FK, not a real orphan.
    /// No other violation must appear.
    /// </summary>
    [DataFact]
    public void ForeignKeyCheck_OnlyReportsTheDeliberateRootSentinelViolation()
    {
        using var command = _fixture.GeneratedConnection.CreateCommand();
        command.CommandText = "PRAGMA foreign_key_check";
        using var reader = command.ExecuteReader();

        var violations = new List<(string Table, string? RowId, string Parent, string FkId)>();
        while (reader.Read())
        {
            violations.Add((
                reader.IsDBNull(0) ? "?" : reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetValue(1).ToString(),
                reader.IsDBNull(2) ? "?" : reader.GetString(2),
                reader.IsDBNull(3) ? "?" : reader.GetValue(3).ToString()!));
        }

        var expectedCount = QueryHelpers.Scalar(
            _fixture.GeneratedConnection,
            "SELECT COUNT(*) FROM \"mTemplateOrTable\" WHERE \"TemplateOrTableType\" = 'TableGroup' AND \"Level\" = 1");

        Assert.True(
            violations.Count == expectedCount,
            $"PRAGMA foreign_key_check returns {violations.Count} violations versus {expectedCount} expected " +
            "TableGroup/Level=1 rows (A-INT-01). Tables involved: " +
            $"{string.Join(",", violations.Select(v => v.Table).Distinct())}");

        Assert.True(
            violations.All(v => v.Table == "mTemplateOrTable"),
            $"PRAGMA foreign_key_check reports violations outside mTemplateOrTable (real orphans, not the " +
            $"deliberate sentinel): {string.Join(",", violations.Where(v => v.Table != "mTemplateOrTable").Select(v => v.Table).Distinct())}");
    }

    // ------------------------------------------------------------------
    // A-UNQ-01..02, 04..11: uniqueness of business keys. Critical layer, 0 expected in all.
    // ------------------------------------------------------------------

    [DataFact]
    public void DomainCode_And_DomainXbrlCode_AreUnique()
    {
        AssertNoDuplicateGroups("mDomain", ["DomainCode"], "A-UNQ-01");
        AssertNoDuplicateGroups("mDomain", ["DomainXBRLCode"], "A-UNQ-01", whereNotNull: "DomainXBRLCode");
    }

    [DataFact]
    public void DimensionXbrlCode_IsUnique()
    {
        // We do not emit the ad-hoc versioning of 4.x, so there cannot be two rows with the same
        // DimensionXBRLCode (unlike release 4.0, which does duplicate: 1,319 rows/831 codes).
        AssertNoDuplicateGroups("mDimension", ["DimensionXBRLCode"], "A-UNQ-02", whereNotNull: "DimensionXBRLCode");
    }

    [DataFact]
    public void DomainId_MemberCode_IsUnique()
    {
        AssertNoDuplicateGroups("mMember", ["DomainID", "MemberCode"], "A-UNQ-04");
    }

    [DataFact]
    public void DomainId_HierarchyCode_IsUnique()
    {
        AssertNoDuplicateGroups("mHierarchy", ["DomainID", "HierarchyCode"], "A-UNQ-05");
    }

    [DataFact]
    public void TaxonomyCode_FrameworkCode_ReleaseCode_AreUnique()
    {
        AssertNoDuplicateGroups("mTaxonomy", ["TaxonomyCode"], "A-UNQ-06");
        AssertNoDuplicateGroups("mReportingFramework", ["FrameworkCode"], "A-UNQ-06");
        AssertNoDuplicateGroups("mRelease", ["ReleaseCode"], "A-UNQ-06");
    }

    /// <summary>
    /// A-UNQ-07: <c>mTableCell.BusinessCode</c> is unique WITHIN its <c>TableID</c>. NOT
    /// globally: the same cell code repeats between tables reused by different taxonomies
    /// (286,526 globally duplicated groups when measured, and that is correct).
    /// </summary>
    [DataFact]
    public void BusinessCode_IsUniquePerTable_ButNotGlobally()
    {
        var perTableDuplicates = QueryHelpers.Scalar(
            _fixture.GeneratedConnection,
            """
            SELECT COUNT(*) FROM (
                SELECT "TableID", "BusinessCode" FROM "mTableCell"
                GROUP BY "TableID", "BusinessCode"
                HAVING COUNT(*) > 1
            )
            """);

        Assert.True(perTableDuplicates == 0, $"{perTableDuplicates} duplicated (TableID, BusinessCode) groups (A-UNQ-07, critical layer).");

        var globalDuplicates = QueryHelpers.Scalar(
            _fixture.GeneratedConnection,
            """
            SELECT COUNT(*) FROM (
                SELECT "BusinessCode" FROM "mTableCell"
                GROUP BY "BusinessCode"
                HAVING COUNT(*) > 1
            )
            """);

        // Documented anti-invariant: global uniqueness is NOT required. It is recorded that
        // duplicates exist (table reuse between taxonomies), without pinning a threshold: if it
        // ever dropped to 0 it would not be a failure, just a change in the source data.
        Assert.True(globalDuplicates > 0, "Global BusinessCode has no duplicates: unexpected given that mTable is reused between taxonomies (A-UNQ-07).");
    }

    [DataFact]
    public void AxisId_OrdinateCode_IsUniquePerAxis()
    {
        // NOT (TableID, OrdinateCode): X and Y deliberately share codes.
        AssertNoDuplicateGroups("mAxisOrdinate", ["AxisID", "OrdinateCode"], "A-UNQ-08");
    }

    [DataFact]
    public void OrdinateId_DimensionId_NeverCollides()
    {
        AssertNoDuplicateGroups("mOrdinateCategorisation", ["OrdinateID", "DimensionID"], "A-UNQ-09");
    }

    [DataFact]
    public void TaxonomyId_TemplateOrTableCode_Level_IsUnique()
    {
        AssertNoDuplicateGroups("mTemplateOrTable", ["TaxonomyID", "TemplateOrTableCode", "Level"], "A-UNQ-10");
    }

    [DataFact]
    public void ModuleCode_IsUniquePerTaxonomy_ButNotGlobally()
    {
        AssertNoDuplicateGroups("mModule", ["TaxonomyID", "ModuleCode"], "A-UNQ-11");

        var globalDuplicates = QueryHelpers.Scalar(
            _fixture.GeneratedConnection,
            """
            SELECT COUNT(*) FROM (SELECT "ModuleCode" FROM "mModule" GROUP BY "ModuleCode" HAVING COUNT(*) > 1)
            """);
        Assert.True(globalDuplicates > 0, "Global ModuleCode has no duplicates: unexpected (106 distinct codes repeated over 415 rows, measured).");
    }

    /// <summary>
    /// A-UNQ-12: ANTI-INVARIANT. <c>mTable.TableCode</c> is NOT unique (681 repeated codes,
    /// measured). A test requiring uniqueness would fail by design; this test records positively
    /// that the reuse still exists, without pinning a threshold.
    /// </summary>
    [DataFact]
    public void TableCode_IsDeliberatelyNotUnique()
    {
        var duplicates = QueryHelpers.Scalar(
            _fixture.GeneratedConnection,
            """
            SELECT COUNT(*) FROM (SELECT "TableCode" FROM "mTable" GROUP BY "TableCode" HAVING COUNT(*) > 1)
            """);

        Assert.True(duplicates > 0, "mTable.TableCode has no repeated code: unexpected given that tables are reused between taxonomies (A-UNQ-12, anti-invariant).");
    }

    private void AssertNoDuplicateGroups(string table, string[] columns, string invariantId, string? whereNotNull = null)
    {
        var columnList = string.Join(", ", columns.Select(c => $"\"{c}\""));
        var where = whereNotNull is null ? string.Empty : $"WHERE \"{whereNotNull}\" IS NOT NULL";

        var sql =
            $"""
            SELECT COUNT(*) FROM (
                SELECT {columnList} FROM "{table}"
                {where}
                GROUP BY {columnList}
                HAVING COUNT(*) > 1
            )
            """;

        var duplicateGroups = QueryHelpers.Scalar(_fixture.GeneratedConnection, sql);

        Assert.True(
            duplicateGroups == 0,
            $"{duplicateGroups} duplicated groups ({columnList}) in {table} ({invariantId}, critical layer).");
    }
}
