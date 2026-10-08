using EbaDpm.Converter.Core.Sqlite;
using EbaDpm.Converter.Core.Validation;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Tests.PlaneC;

/// <summary>
/// <c>C-EQU-01</c> (<c>PlaneCChecks.BuildCEqu01</c>) carries no release-of-cut guard and no
/// exception census: <c>KnownExceptions</c> declares no exception with <c>Check == "C-EQU-01"</c>,
/// so <c>ApplyOriginAnomalyExceptions</c> has nothing to apply and the result is direct:
/// <c>Failed</c> are the RAW violations, unfiltered.
///
/// What remains is the zero-checkable-rules guard: ZERO checkable rules -&gt; <c>Skipped</c>, never
/// "0 violations" (which would be indistinguishable from "0 violations over a real universe").
/// This file tests that guard and its negative control -- with checkable rules, <c>C-EQU-01</c> is
/// really evaluated.
/// </summary>
public sealed class PlaneCEquGuardTests : IDisposable
{
    private readonly string _tempDirectory;

    public PlaneCEquGuardTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"EbaDpm.EquGuardTests_{Environment.ProcessId}_{Guid.NewGuid():N}");
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
    /// Zero-checkable-rules guard: with 0 checkable rules (here, the ONLY rule of the layout
    /// repository names a table that does not exist in the output -- "table absent"),
    /// <c>C-EQU-01</c> is emitted <c>Skipped</c> -- NEVER <c>Pass</c> with <c>Failed=0</c>, which
    /// would be indistinguishable from "0 violations over a real universe".
    /// </summary>
    [Fact]
    public void CEqu01_WithZeroCheckableRules_ComesOutSkippedNotPass()
    {
        var generatedPath = Path.Combine(_tempDirectory, "generated.db");
        SchemaCreator.Create(generatedPath, overwrite: false);

        var layoutsPath = Path.Combine(_tempDirectory, "layouts.db");
        CreateLayoutsWithEqualityRule(layoutsPath, ruleId: 1, position: 0, term: "Z_99.99, r0001, c0001",
            tableCode: "Z_99.99", rowCode: "0001", columnCode: "0001", zCode: null);
        AddEqualityRuleTerm(layoutsPath, ruleId: 1, position: 1, term: "Z_88.88, r0001, c0001",
            tableCode: "Z_88.88", rowCode: "0001", columnCode: "0001", zCode: null); // does not exist either -> still "table absent".

        SqliteConnection.ClearAllPools();
        var result = Validator.Run(generatedPath, referencePath: null, layoutsPath);

        var cEqu01 = Assert.Single(result.Report.Checks, c => c.Id == "C-EQU-01");
        Assert.Equal("skipped", cEqu01.Status);
        Assert.Contains("0 checkable rules", cEqu01.SkipReason);
        Assert.Equal(0, cEqu01.Examined);
        Assert.Equal(0, cEqu01.Failed);

        // Negative control of the guard: with no exceptions declared for this check, nothing to apply.
        Assert.DoesNotContain(result.Report.Exceptions, e => e.Id == "DD-24");
    }

    /// <summary>
    /// Negative control of the guard: WITH checkable rules, <c>C-EQU-01</c> is really evaluated --
    /// never <c>Skipped</c> -- and, with no exception to filter, a rule whose two cells share a
    /// signature comes out without any violation.
    /// </summary>
    [Fact]
    public void CEqu01_WithCheckableRules_IsReallyEvaluated_DoesNotComeOutSkipped()
    {
        var generatedPath = Path.Combine(_tempDirectory, "generated.db");
        SchemaCreator.Create(generatedPath, overwrite: false);
        InsertCheckableTablesAndRule(generatedPath, signatureA: "SIG1", signatureB: "SIG1");

        var layoutsPath = Path.Combine(_tempDirectory, "layouts.db");
        CreateLayoutsWithEqualityRule(layoutsPath, ruleId: 1, position: 0, term: "A_01.01, r0010, c0020",
            tableCode: "A_01.01", rowCode: "0010", columnCode: "0020", zCode: null);
        AddEqualityRuleTerm(layoutsPath, ruleId: 1, position: 1, term: "B_02.01, r0030, c0040",
            tableCode: "B_02.01", rowCode: "0030", columnCode: "0040", zCode: null);

        SqliteConnection.ClearAllPools();
        var result = Validator.Run(generatedPath, referencePath: null, layoutsPath);

        var cEqu01 = Assert.Single(result.Report.Checks, c => c.Id == "C-EQU-01");
        Assert.NotEqual("skipped", cEqu01.Status);
        Assert.Equal(1, cEqu01.Examined);
        Assert.Equal(0, cEqu01.Failed);
    }

    /// <summary>
    /// Positive control, complementary to the previous one: a checkable rule whose two cells carry
    /// GENUINELY different signatures (not just the open-axis bracket) still produces a real
    /// violation -- <c>C-EQU-01</c> has no census that could absorb it, so <c>Failed</c> reflects
    /// the raw violation.
    /// </summary>
    [Fact]
    public void CEqu01_WithCellsOfGenuinelyDifferentSignature_Fails()
    {
        var generatedPath = Path.Combine(_tempDirectory, "generated.db");
        SchemaCreator.Create(generatedPath, overwrite: false);
        InsertCheckableTablesAndRule(generatedPath, signatureA: "SIG1", signatureB: "SIG2-DIFFERENT");

        var layoutsPath = Path.Combine(_tempDirectory, "layouts.db");
        CreateLayoutsWithEqualityRule(layoutsPath, ruleId: 1, position: 0, term: "A_01.01, r0010, c0020",
            tableCode: "A_01.01", rowCode: "0010", columnCode: "0020", zCode: null);
        AddEqualityRuleTerm(layoutsPath, ruleId: 1, position: 1, term: "B_02.01, r0030, c0040",
            tableCode: "B_02.01", rowCode: "0030", columnCode: "0040", zCode: null);

        SqliteConnection.ClearAllPools();
        var result = Validator.Run(generatedPath, referencePath: null, layoutsPath);

        var cEqu01 = Assert.Single(result.Report.Checks, c => c.Id == "C-EQU-01");
        Assert.NotEqual("skipped", cEqu01.Status);
        Assert.Equal(1, cEqu01.Examined);
        Assert.Equal(1, cEqu01.Failed);
    }

    /// <summary>
    /// Negative control of the release guard: ONE of the two tables named by the rule exists
    /// (never "table absent") but its own release (<c>mTaxonomyTable</c>/<c>mTaxonomy</c>/
    /// <c>mReportingFramework</c>) does NOT match that of the rule's source sheet
    /// (<c>LayoutFile.ReleaseLabel</c>, not even with the <c>REL.n -> REL</c> tolerance) --
    /// <c>C-EQU-01</c> comes out <c>Skipped</c>, <b>never <c>pass</c> nor <c>fail</c></b>, with the
    /// reason <c>"different release"</c> written and counted in <c>C-EQU-02</c>. A <c>Skipped</c>
    /// that happens by accident (for example, because of an "ambiguous cell") is not a guard.
    /// </summary>
    [Fact]
    public void CEqu01_WithATableOfADifferentReleaseThanTheSourceSheet_ComesOutSkipped_ReasonDifferentRelease_NeverPassNorFail()
    {
        var generatedPath = Path.Combine(_tempDirectory, "generated.db");
        SchemaCreator.Create(generatedPath, overwrite: false);
        InsertCheckableTablesAndRule(
            generatedPath, signatureA: "SIG1", signatureB: "SIG1",
            releaseA: "4.3"); // DIFFERENT from TableRelease ("4.2") -- not even REL.n of it (4.3 does not start with "4.2.").

        var layoutsPath = Path.Combine(_tempDirectory, "layouts.db");
        CreateLayoutsWithEqualityRule(layoutsPath, ruleId: 1, position: 0, term: "A_01.01, r0010, c0020",
            tableCode: "A_01.01", rowCode: "0010", columnCode: "0020", zCode: null);
        AddEqualityRuleTerm(layoutsPath, ruleId: 1, position: 1, term: "B_02.01, r0030, c0040",
            tableCode: "B_02.01", rowCode: "0030", columnCode: "0040", zCode: null);

        SqliteConnection.ClearAllPools();
        var result = Validator.Run(generatedPath, referencePath: null, layoutsPath);

        var cEqu01 = Assert.Single(result.Report.Checks, c => c.Id == "C-EQU-01");
        var cEqu02 = Assert.Single(result.Report.Checks, c => c.Id == "C-EQU-02");

        Assert.Equal("skipped", cEqu01.Status);
        Assert.Equal(0, cEqu01.Examined);
        Assert.Equal(0, cEqu01.Failed);
        Assert.Contains("0 checkable rules", cEqu01.SkipReason);

        // The reason is written, never "table absent" (both tables EXIST) nor a silent accident.
        Assert.Contains(cEqu02.Samples, s => s.BusinessKey == "different release" && s.Generated == "1");
        Assert.DoesNotContain(cEqu02.Samples, s => s.BusinessKey == "table absent");
    }

    /// <summary>Two minimal tables (A_01.01/B_02.01), each with Y/X and a cell with a signature,
    /// enough for a two-term rule between them to be CHECKABLE.
    ///
    /// It also includes the taxonomy link of each table (<c>mTaxonomyTable</c>/<c>mTaxonomy</c>/
    /// <c>mReportingFramework</c>, "{lowercase framework} {release}") in
    /// <see cref="TableFramework"/>/<see cref="TableRelease"/> -- the SAME release that
    /// <c>CreateLayoutsWithEqualityRule</c> uses for its <c>LayoutFile</c>, so that the two tables
    /// are genuinely CHECKABLE and do not fall into "different release" by accident (SchemaCreator
    /// creates the full schema, but mTaxonomyTable/mTaxonomy/mReportingFramework stay empty --
    /// without this link <c>PlaneCReleaseMatcher</c> finds no release key for A_01.01/B_02.01).</summary>
    private static void InsertCheckableTablesAndRule(
        string generatedPath, string signatureA, string signatureB, string releaseA = TableRelease)
    {
        using var connection = OpenReadWrite(generatedPath);
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            $"""
            INSERT INTO "mTable" ("TableID", "TableCode") VALUES (1, 'A_01.01');
            INSERT INTO "mAxis" ("AxisID", "AxisOrientation", "IsOpenAxis") VALUES (10, 'Y', 0);
            INSERT INTO "mAxis" ("AxisID", "AxisOrientation", "IsOpenAxis") VALUES (11, 'X', 0);
            INSERT INTO "mTableAxis" ("AxisID", "TableID", "Order") VALUES (10, 1, 0);
            INSERT INTO "mTableAxis" ("AxisID", "TableID", "Order") VALUES (11, 1, 1);
            INSERT INTO "mAxisOrdinate" ("OrdinateID", "AxisID", "OrdinateCode", "Order") VALUES (100, 10, '0010', 0);
            INSERT INTO "mAxisOrdinate" ("OrdinateID", "AxisID", "OrdinateCode", "Order") VALUES (101, 11, '0020', 0);
            INSERT INTO "mCellPosition" ("CellID", "OrdinateID") VALUES (1000, 100);
            INSERT INTO "mCellPosition" ("CellID", "OrdinateID") VALUES (1000, 101);
            INSERT INTO "mTableCell" ("CellID", "TableID", "IsShaded", "DatapointSignature") VALUES (1000, 1, 0, '{signatureA}');
            INSERT INTO "mReportingFramework" ("FrameworkID", "FrameworkCode") VALUES (9001, '{TableFramework}');
            INSERT INTO "mTaxonomy" ("TaxonomyID", "TaxonomyCode", "FrameworkID") VALUES (9001, '{TableFramework.ToLowerInvariant()} {releaseA}', 9001);
            INSERT INTO "mTaxonomyTable" ("TaxonomyID", "TableID", "AnnotatedTableID") VALUES (9001, 1, 1);

            INSERT INTO "mTable" ("TableID", "TableCode") VALUES (2, 'B_02.01');
            INSERT INTO "mAxis" ("AxisID", "AxisOrientation", "IsOpenAxis") VALUES (20, 'Y', 0);
            INSERT INTO "mAxis" ("AxisID", "AxisOrientation", "IsOpenAxis") VALUES (21, 'X', 0);
            INSERT INTO "mTableAxis" ("AxisID", "TableID", "Order") VALUES (20, 2, 0);
            INSERT INTO "mTableAxis" ("AxisID", "TableID", "Order") VALUES (21, 2, 1);
            INSERT INTO "mAxisOrdinate" ("OrdinateID", "AxisID", "OrdinateCode", "Order") VALUES (200, 20, '0030', 0);
            INSERT INTO "mAxisOrdinate" ("OrdinateID", "AxisID", "OrdinateCode", "Order") VALUES (201, 21, '0040', 0);
            INSERT INTO "mCellPosition" ("CellID", "OrdinateID") VALUES (2000, 200);
            INSERT INTO "mCellPosition" ("CellID", "OrdinateID") VALUES (2000, 201);
            INSERT INTO "mTableCell" ("CellID", "TableID", "IsShaded", "DatapointSignature") VALUES (2000, 2, 0, '{signatureB}');
            INSERT INTO "mReportingFramework" ("FrameworkID", "FrameworkCode") VALUES (9002, '{TableFramework}');
            INSERT INTO "mTaxonomy" ("TaxonomyID", "TaxonomyCode", "FrameworkID") VALUES (9002, '{TableFramework.ToLowerInvariant()} {TableRelease}', 9002);
            INSERT INTO "mTaxonomyTable" ("TaxonomyID", "TableID", "AnnotatedTableID") VALUES (9002, 2, 2);
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>The SAME release that <see cref="InsertCheckableTablesAndRule"/> registers for
    /// A_01.01/B_02.01 -- so a rule that is checkable is checkable under the release guard too,
    /// and not by accident of the mechanism being absent.</summary>
    private const string TableFramework = "TEST";
    private const string TableRelease = "4.2";

    private static void CreateLayoutsWithEqualityRule(
        string path, long ruleId, int position, string term, string tableCode, string rowCode, string columnCode, string? zCode)
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

        // ONE source sheet for all the terms of this test (SheetId=1), with its LayoutFile --
        // FrameworkCode/ReleaseLabel that InsertCheckableTablesAndRule matches.
        // TableCode CANNOT be NULL -- PlaneCComparer.Compare (which Validator.Run ALWAYS calls,
        // regardless of whether this test is about C-EQU-01 or C-DPS-01) reads ALL the sheets of
        // "LayoutSheet" through PlaneCLayoutReader.Read, which assumes a non-null TableCode (the
        // real production shape); the TableCode of the first term itself is used -- without
        // LayoutDeclaration/LayoutValue/LayoutCell for this sheet, PlaneCComparer attributes no
        // signature to it.
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText =
                $"""
                INSERT INTO "LayoutFile" ("FileId", "FrameworkCode", "ReleaseLabel") VALUES (1, '{TableFramework}', '{TableRelease}');
                INSERT INTO "LayoutSheet" ("SheetId", "FileId", "TableCode") VALUES (1, 1, '{tableCode}');
                """;
            cmd.ExecuteNonQuery();
        }

        AddEqualityRuleTermOnConnection(connection, ruleId, position, term, tableCode, rowCode, columnCode, zCode, sheetId: 1);
    }

    private static void AddEqualityRuleTerm(
        string path, long ruleId, int position, string term, string tableCode, string rowCode, string columnCode, string? zCode)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        connection.Open();
        AddEqualityRuleTermOnConnection(connection, ruleId, position, term, tableCode, rowCode, columnCode, zCode, sheetId: 1);
    }

    private static void AddEqualityRuleTermOnConnection(
        SqliteConnection connection, long ruleId, int position, string term, string tableCode, string rowCode, string columnCode,
        string? zCode, long sheetId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """INSERT INTO "LayoutEqualityRule" VALUES (@ruleId, @position, @term, @tableCode, @rowCode, @columnCode, @zCode, @sheetId)""";
        cmd.Parameters.AddWithValue("@ruleId", ruleId);
        cmd.Parameters.AddWithValue("@position", position);
        cmd.Parameters.AddWithValue("@term", term);
        cmd.Parameters.AddWithValue("@tableCode", tableCode);
        cmd.Parameters.AddWithValue("@rowCode", rowCode);
        cmd.Parameters.AddWithValue("@columnCode", columnCode);
        cmd.Parameters.AddWithValue("@zCode", (object?)zCode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@sheetId", sheetId);
        cmd.ExecuteNonQuery();
    }

    private static SqliteConnection OpenReadWrite(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite }.ToString());
        connection.Open();

        // Same criterion as SchemaCreator.Create: no foreign_keys, so synthetic rows can be
        // inserted without fabricating ALL the mConcept/mTemplateOrTable rows that the declared FKs
        // of the schema would require -- the FKs are not the subject of this test.
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = OFF;";
            pragma.ExecuteNonQuery();
        }

        return connection;
    }
}
