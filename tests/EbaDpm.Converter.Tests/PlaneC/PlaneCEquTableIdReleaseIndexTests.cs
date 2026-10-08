using EbaDpm.Converter.Core.Sqlite;
using EbaDpm.Converter.Core.Validation;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Tests.PlaneC;

/// <summary>
/// Proves that the RESTRICTION by <c>TableID</c> of the ordinate index -- not just the release
/// guard, which filters the RULE -- is what produces the result, and not an accident. Minimal
/// fixture with TWO <c>TableID</c>s under the SAME <c>TableCode</c> ("A_01.01"), in different
/// releases (4.2 / 4.3): without the restriction, <c>PlaneCEqualityChecker</c> joined the
/// <c>OrdinateID</c>s of the two <c>TableID</c>s without filtering by release when resolving the
/// cell, so even though the release GUARD already let the rule through (its source sheet is 4.2),
/// the intersection of candidates found TWO cells -- one per <c>TableID</c> -- and the rule fell
/// into "ambiguous cell" (exactly the defect measured against the real 4.3).
///
/// With the index by <c>(TableID, Orientation, OrdinateCode)</c> and the release filter BEFORE
/// joining <c>OrdinateID</c>s, the term resolves a single cell -- that of the <c>TableID</c> whose
/// release matches the rule's sheet -- and the signature it compares is that of THAT cell, not that
/// of the <c>TableID</c> of the OTHER release: this is verified by giving each <c>TableID</c> a
/// DIFFERENT signature ("MATCH" for the one that matches, "WRONG" for the one that does not) and
/// checking that the rule comes out without divergence (it compares MATCH==MATCH, never sees WRONG).
/// </summary>
public sealed class PlaneCEquTableIdReleaseIndexTests : IDisposable
{
    private const string Framework = "TEST";
    private const string MatchingRelease = "4.2";
    private const string OtherRelease = "4.3";
    // Neither exact nor REL.n -> REL of either table release ("4.2." or "4.3." as a prefix would
    // activate the point-release tolerance; "4.9" is not a prefix of either).
    private const string UnmatchedRuleRelease = "4.9";

    private readonly string _tempDirectory;

    public PlaneCEquTableIdReleaseIndexTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"EbaDpm.EquTableIdReleaseIndexTests_{Environment.ProcessId}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort.
        }
    }

    /// <summary>
    /// POSITIVE control: the rule's sheet is release 4.2. Of the two <c>TableID</c>s with
    /// <c>TableCode</c> "A_01.01" (one 4.2 with signature "MATCH", another 4.3 with signature
    /// "WRONG"), only the 4.2 one is a candidate -- the rule resolves "MATCH" and comes out
    /// CHECKABLE and WITHOUT divergence. If the index still joined blindly by <c>TableCode</c>, the
    /// intersection of candidates would find two cells (one per <c>TableID</c>) and the rule would
    /// fall into "ambiguous cell" (Examined=0, Skipped).
    /// </summary>
    [Fact]
    public void CEqu01_WithTwoTableIdsOfTheSameCodeInDifferentReleases_ResolvesOnlyTheMatchingTableId()
    {
        var generatedPath = Path.Combine(_tempDirectory, "generated.db");
        SchemaCreator.Create(generatedPath, overwrite: false);
        InsertFixture(generatedPath);

        var layoutsPath = Path.Combine(_tempDirectory, "layouts.db");
        CreateLayoutsWithEqualityRule(layoutsPath, ruleRelease: MatchingRelease);

        SqliteConnection.ClearAllPools();
        var result = Validator.Run(generatedPath, referencePath: null, layoutsPath);

        var cEqu01 = Assert.Single(result.Report.Checks, c => c.Id == "C-EQU-01");
        var cEqu02 = Assert.Single(result.Report.Checks, c => c.Id == "C-EQU-02");

        Assert.NotEqual("skipped", cEqu01.Status);
        Assert.Equal(1, cEqu01.Examined);
        Assert.Equal(0, cEqu01.Failed);

        // Neither of the two causes of the historical defect is counted.
        Assert.DoesNotContain(cEqu02.Samples, s => s.BusinessKey == "ambiguous cell");
        Assert.DoesNotContain(cEqu02.Samples, s => s.BusinessKey == "different release");
    }

    /// <summary>
    /// NEGATIVE control: if the rule's sheet does NOT match the release of EITHER of the two
    /// <c>TableID</c>s of the code -- not even with the <c>REL.n -> REL</c> tolerance --, it still
    /// comes out "different release", never "ambiguous cell" nor a false "all ok". The restriction
    /// by <c>TableID</c> does not weaken the release guard: the guard still bites first.
    /// </summary>
    [Fact]
    public void CEqu01_WithTwoTableIdsOfTheSameCode_IfTheSheetMatchesNeither_IsStillDifferentRelease()
    {
        var generatedPath = Path.Combine(_tempDirectory, "generated.db");
        SchemaCreator.Create(generatedPath, overwrite: false);
        InsertFixture(generatedPath);

        var layoutsPath = Path.Combine(_tempDirectory, "layouts.db");
        CreateLayoutsWithEqualityRule(layoutsPath, ruleRelease: UnmatchedRuleRelease);

        SqliteConnection.ClearAllPools();
        var result = Validator.Run(generatedPath, referencePath: null, layoutsPath);

        var cEqu01 = Assert.Single(result.Report.Checks, c => c.Id == "C-EQU-01");
        var cEqu02 = Assert.Single(result.Report.Checks, c => c.Id == "C-EQU-02");

        Assert.Equal("skipped", cEqu01.Status);
        Assert.Equal(0, cEqu01.Examined);
        Assert.Equal(0, cEqu01.Failed);
        Assert.Contains("0 checkable rules", cEqu01.SkipReason);

        Assert.Contains(cEqu02.Samples, s => s.BusinessKey == "different release" && s.Generated == "1");
        Assert.DoesNotContain(cEqu02.Samples, s => s.BusinessKey == "ambiguous cell");
    }

    /// <summary>
    /// Two <c>TableID</c>s (1 and 3) under the SAME <c>TableCode</c> "A_01.01" -- 1 in release 4.2
    /// with a cell of signature "MATCH", 3 in release 4.3 with signature "WRONG" -- plus a third
    /// <c>TableID</c> (2) "B_02.01" in release 4.2 with signature "MATCH", needed so that the
    /// two-term rule is checkable.
    /// </summary>
    private static void InsertFixture(string generatedPath)
    {
        using var connection = OpenReadWrite(generatedPath);
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            $"""
            -- TableID 1: A_01.01, release 4.2 (matches), signature MATCH.
            INSERT INTO "mTable" ("TableID", "TableCode") VALUES (1, 'A_01.01');
            INSERT INTO "mAxis" ("AxisID", "AxisOrientation", "IsOpenAxis") VALUES (10, 'Y', 0);
            INSERT INTO "mAxis" ("AxisID", "AxisOrientation", "IsOpenAxis") VALUES (11, 'X', 0);
            INSERT INTO "mTableAxis" ("AxisID", "TableID", "Order") VALUES (10, 1, 0);
            INSERT INTO "mTableAxis" ("AxisID", "TableID", "Order") VALUES (11, 1, 1);
            INSERT INTO "mAxisOrdinate" ("OrdinateID", "AxisID", "OrdinateCode", "Order") VALUES (100, 10, '0010', 0);
            INSERT INTO "mAxisOrdinate" ("OrdinateID", "AxisID", "OrdinateCode", "Order") VALUES (101, 11, '0020', 0);
            INSERT INTO "mCellPosition" ("CellID", "OrdinateID") VALUES (1000, 100);
            INSERT INTO "mCellPosition" ("CellID", "OrdinateID") VALUES (1000, 101);
            INSERT INTO "mTableCell" ("CellID", "TableID", "IsShaded", "DatapointSignature") VALUES (1000, 1, 0, 'MATCH');
            INSERT INTO "mReportingFramework" ("FrameworkID", "FrameworkCode") VALUES (9001, '{Framework}');
            INSERT INTO "mTaxonomy" ("TaxonomyID", "TaxonomyCode", "FrameworkID") VALUES (9001, '{Framework.ToLowerInvariant()} {MatchingRelease}', 9001);
            INSERT INTO "mTaxonomyTable" ("TaxonomyID", "TableID", "AnnotatedTableID") VALUES (9001, 1, 1);

            -- TableID 3: SAME TableCode "A_01.01", release 4.3 (does NOT match), signature WRONG.
            INSERT INTO "mTable" ("TableID", "TableCode") VALUES (3, 'A_01.01');
            INSERT INTO "mAxis" ("AxisID", "AxisOrientation", "IsOpenAxis") VALUES (30, 'Y', 0);
            INSERT INTO "mAxis" ("AxisID", "AxisOrientation", "IsOpenAxis") VALUES (31, 'X', 0);
            INSERT INTO "mTableAxis" ("AxisID", "TableID", "Order") VALUES (30, 3, 0);
            INSERT INTO "mTableAxis" ("AxisID", "TableID", "Order") VALUES (31, 3, 1);
            INSERT INTO "mAxisOrdinate" ("OrdinateID", "AxisID", "OrdinateCode", "Order") VALUES (300, 30, '0010', 0);
            INSERT INTO "mAxisOrdinate" ("OrdinateID", "AxisID", "OrdinateCode", "Order") VALUES (301, 31, '0020', 0);
            INSERT INTO "mCellPosition" ("CellID", "OrdinateID") VALUES (3000, 300);
            INSERT INTO "mCellPosition" ("CellID", "OrdinateID") VALUES (3000, 301);
            INSERT INTO "mTableCell" ("CellID", "TableID", "IsShaded", "DatapointSignature") VALUES (3000, 3, 0, 'WRONG');
            INSERT INTO "mReportingFramework" ("FrameworkID", "FrameworkCode") VALUES (9003, '{Framework}');
            INSERT INTO "mTaxonomy" ("TaxonomyID", "TaxonomyCode", "FrameworkID") VALUES (9003, '{Framework.ToLowerInvariant()} {OtherRelease}', 9003);
            INSERT INTO "mTaxonomyTable" ("TaxonomyID", "TableID", "AnnotatedTableID") VALUES (9003, 3, 3);

            -- TableID 2: B_02.01, release 4.2 (matches), signature MATCH -- so that the two-term
            -- rule is genuinely checkable.
            INSERT INTO "mTable" ("TableID", "TableCode") VALUES (2, 'B_02.01');
            INSERT INTO "mAxis" ("AxisID", "AxisOrientation", "IsOpenAxis") VALUES (20, 'Y', 0);
            INSERT INTO "mAxis" ("AxisID", "AxisOrientation", "IsOpenAxis") VALUES (21, 'X', 0);
            INSERT INTO "mTableAxis" ("AxisID", "TableID", "Order") VALUES (20, 2, 0);
            INSERT INTO "mTableAxis" ("AxisID", "TableID", "Order") VALUES (21, 2, 1);
            INSERT INTO "mAxisOrdinate" ("OrdinateID", "AxisID", "OrdinateCode", "Order") VALUES (200, 20, '0030', 0);
            INSERT INTO "mAxisOrdinate" ("OrdinateID", "AxisID", "OrdinateCode", "Order") VALUES (201, 21, '0040', 0);
            INSERT INTO "mCellPosition" ("CellID", "OrdinateID") VALUES (2000, 200);
            INSERT INTO "mCellPosition" ("CellID", "OrdinateID") VALUES (2000, 201);
            INSERT INTO "mTableCell" ("CellID", "TableID", "IsShaded", "DatapointSignature") VALUES (2000, 2, 0, 'MATCH');
            INSERT INTO "mReportingFramework" ("FrameworkID", "FrameworkCode") VALUES (9002, '{Framework}');
            INSERT INTO "mTaxonomy" ("TaxonomyID", "TaxonomyCode", "FrameworkID") VALUES (9002, '{Framework.ToLowerInvariant()} {MatchingRelease}', 9002);
            INSERT INTO "mTaxonomyTable" ("TaxonomyID", "TableID", "AnnotatedTableID") VALUES (9002, 2, 2);
            """;
        cmd.ExecuteNonQuery();
    }

    private static void CreateLayoutsWithEqualityRule(string path, string ruleRelease)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        connection.Open();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText =
                """
                CREATE TABLE "LayoutFile" ("FileId" INTEGER PRIMARY KEY, "FrameworkCode" TEXT, "ReleaseLabel" TEXT);
                CREATE TABLE "LayoutSheet" ("SheetId" INTEGER PRIMARY KEY, "FileId" INTEGER, "TableCode" TEXT);
                CREATE TABLE "LayoutDeclaration" (
                    "DeclId" INTEGER PRIMARY KEY, "SheetId" INTEGER, "DimensionCode" TEXT, "DomainCode" TEXT, "IsKey" INTEGER);
                CREATE TABLE "LayoutValue" (
                    "ValueId" INTEGER PRIMARY KEY, "SheetId" INTEGER, "DeclId" INTEGER, "OrdinateId" INTEGER, "MemberCode" TEXT);
                CREATE TABLE "LayoutCell" (
                    "CellId" INTEGER PRIMARY KEY, "SheetId" INTEGER, "RowOrdinateId" INTEGER,
                    "ColumnOrdinateId" INTEGER, "IsShaded" INTEGER);
                CREATE TABLE "LayoutEqualityRule" (
                    "RuleId" INTEGER, "Position" INTEGER, "Term" TEXT,
                    "TableCode" TEXT, "RowCode" TEXT, "ColumnCode" TEXT, "ZCode" TEXT, "SheetId" INTEGER
                );
                """;
            cmd.ExecuteNonQuery();
        }

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText =
                $"""
                INSERT INTO "LayoutFile" ("FileId", "FrameworkCode", "ReleaseLabel") VALUES (1, '{Framework}', '{ruleRelease}');
                INSERT INTO "LayoutSheet" ("SheetId", "FileId", "TableCode") VALUES (1, 1, 'A_01.01');
                INSERT INTO "LayoutEqualityRule" VALUES (1, 0, 'A_01.01, r0010, c0020', 'A_01.01', '0010', '0020', NULL, 1);
                INSERT INTO "LayoutEqualityRule" VALUES (1, 1, 'B_02.01, r0030, c0040', 'B_02.01', '0030', '0040', NULL, 1);
                """;
            cmd.ExecuteNonQuery();
        }
    }

    private static SqliteConnection OpenReadWrite(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite }.ToString());
        connection.Open();

        // Same criterion as SchemaCreator.Create: no foreign_keys, to insert synthetic rows without
        // fabricating ALL the mConcept/mTemplateOrTable rows that the declared FKs would require.
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = OFF;";
            pragma.ExecuteNonQuery();
        }

        return connection;
    }
}
