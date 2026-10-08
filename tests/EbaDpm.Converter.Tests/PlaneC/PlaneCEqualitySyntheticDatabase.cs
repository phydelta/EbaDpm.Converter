using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Tests.PlaneC;

/// <summary>
/// Minimal, in-memory construction of the half of the generated output that
/// <see cref="EbaDpm.Converter.Core.Validation.PlaneC.PlaneCEqualityChecker"/> really queries:
/// <c>mTable</c>...<c>mAxisOrdinate</c>/<c>mCellPosition</c>/<c>mTableCell</c>, and the
/// <c>LayoutEqualityRule</c> table of the layout repository. Deliberately DIFFERENT from
/// <see cref="PlaneCSyntheticDatabase"/> (which serves <c>PlaneCComparer</c>, with different
/// columns: no <c>OrdinateCode</c>, no <c>mCellPosition</c>) -- mixing the two would have forced
/// touching a fixture shared by <c>PlaneCComparerTests</c> for unrelated needs.
///
/// <c>PlaneCEqualityChecker.Check</c> resolves the OWN release of each table through
/// <c>mTaxonomyTable</c>/<c>mTaxonomy</c>/<c>mReportingFramework</c> (<see cref="PlaneCReleaseMatcher"/>,
/// shared with <c>PlaneCComparer</c>) and the release of the RULE through
/// <c>LayoutEqualityRule.SheetId -> LayoutSheet -> LayoutFile</c> -- both are added here. By
/// default (<see cref="InsertRuleTerm"/> without an explicit <c>sheetId</c> and
/// <see cref="InsertTable"/> without an explicit taxonomy) the two sides match on
/// <c>("TEST", "4.2")</c>, so that the tests that are not about the release guard keep checking
/// what they checked -- without that default matching, EVERY test would have had to fabricate a
/// taxonomy and a sheet by hand just to avoid falling into "different release" by accident.
/// </summary>
internal static class PlaneCEqualitySyntheticDatabase
{
    /// <summary>Default framework/release that <see cref="InsertTable"/> and
    /// <see cref="InsertRuleTerm"/> use when no other is requested explicitly, so that the two
    /// sides (output table / rule's source sheet) match without every test having to declare a
    /// taxonomy and a sheet by hand.</summary>
    public const string DefaultFrameworkCode = "TEST";
    public const string DefaultRelease = "4.2";

    public static SqliteConnection OpenGenerated()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            CREATE TABLE "mTable" ("TableID" INTEGER PRIMARY KEY, "TableCode" TEXT);
            CREATE TABLE "mAxis" ("AxisID" INTEGER PRIMARY KEY, "AxisOrientation" TEXT, "IsOpenAxis" INTEGER);
            CREATE TABLE "mTableAxis" ("TableID" INTEGER, "AxisID" INTEGER);
            CREATE TABLE "mAxisOrdinate" ("OrdinateID" INTEGER PRIMARY KEY, "AxisID" INTEGER, "OrdinateCode" TEXT);
            CREATE TABLE "mCellPosition" ("CellID" INTEGER, "OrdinateID" INTEGER);
            CREATE TABLE "mTableCell" ("CellID" INTEGER PRIMARY KEY, "TableID" INTEGER, "IsShaded" INTEGER, "DatapointSignature" TEXT);
            CREATE TABLE "mReportingFramework" ("FrameworkID" INTEGER PRIMARY KEY, "FrameworkCode" TEXT);
            CREATE TABLE "mTaxonomy" ("TaxonomyID" INTEGER PRIMARY KEY, "TaxonomyCode" TEXT, "FrameworkID" INTEGER);
            CREATE TABLE "mTaxonomyTable" ("TaxonomyID" INTEGER, "TableID" INTEGER);
            """;
        cmd.ExecuteNonQuery();
        return connection;
    }

    public static SqliteConnection OpenLayouts()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            CREATE TABLE "LayoutFile" ("FileId" INTEGER PRIMARY KEY, "FrameworkCode" TEXT, "ReleaseLabel" TEXT);
            CREATE TABLE "LayoutSheet" ("SheetId" INTEGER PRIMARY KEY, "FileId" INTEGER, "TableCode" TEXT);
            CREATE TABLE "LayoutEqualityRule" (
                "RuleId" INTEGER, "Position" INTEGER, "Term" TEXT,
                "TableCode" TEXT, "RowCode" TEXT, "ColumnCode" TEXT, "ZCode" TEXT, "SheetId" INTEGER
            );
            """;
        cmd.ExecuteNonQuery();
        return connection;
    }

    internal static void ExecEqu(this SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Registers a table in the output, WITH its taxonomy link "{lowercase framework}
    /// {release}" -- needed so that <c>PlaneCReleaseMatcher</c> can resolve its own release
    /// (<c>mTaxonomyTable</c>/<c>mTaxonomy</c>/<c>mReportingFramework</c>, which <c>PlaneCEqualityChecker</c>
    /// would otherwise never query). By default it matches <see cref="InsertRuleTerm"/> without
    /// extra arguments -- see the class XML doc.</summary>
    public static void InsertTable(
        this SqliteConnection generated, long tableId, string tableCode,
        string frameworkCode = DefaultFrameworkCode, string release = DefaultRelease)
    {
        generated.ExecEqu($"""INSERT INTO "mTable" VALUES ({tableId}, '{tableCode}')""");
        var frameworkId = tableId * 1000 + 1; // arbitrary, unique per test table
        var taxonomyId = tableId * 1000 + 2;
        generated.ExecEqu($"""INSERT INTO "mReportingFramework" VALUES ({frameworkId}, '{frameworkCode}')""");
        generated.ExecEqu(
            $"""INSERT INTO "mTaxonomy" VALUES ({taxonomyId}, '{frameworkCode.ToLowerInvariant()} {release}', {frameworkId})""");
        generated.ExecEqu($"""INSERT INTO "mTaxonomyTable" VALUES ({taxonomyId}, {tableId})""");
    }

    /// <summary>An axis of a table, with its ordinates -- <paramref name="ordinates"/> is
    /// (OrdinateID, OrdinateCode).</summary>
    public static void InsertAxis(
        this SqliteConnection generated, long axisId, long tableId, string orientation, bool isOpenAxis,
        params (long OrdinateId, string OrdinateCode)[] ordinates)
    {
        generated.ExecEqu($"""INSERT INTO "mAxis" VALUES ({axisId}, '{orientation}', {(isOpenAxis ? 1 : 0)})""");
        generated.ExecEqu($"""INSERT INTO "mTableAxis" VALUES ({tableId}, {axisId})""");
        foreach (var (ordinateId, ordinateCode) in ordinates)
        {
            generated.ExecEqu($"""INSERT INTO "mAxisOrdinate" VALUES ({ordinateId}, {axisId}, '{ordinateCode}')""");
        }
    }

    /// <summary>A cell: its position on each axis (<paramref name="ordinateIds"/>) and its signature.</summary>
    public static void InsertCell(
        this SqliteConnection generated, long cellId, long tableId, string? signature, bool isShaded,
        params long[] ordinateIds)
    {
        var sig = signature is null ? "NULL" : $"'{signature}'";
        generated.ExecEqu($"""INSERT INTO "mTableCell" VALUES ({cellId}, {tableId}, {(isShaded ? 1 : 0)}, {sig})""");
        foreach (var ordinateId in ordinateIds)
        {
            generated.ExecEqu($"""INSERT INTO "mCellPosition" VALUES ({cellId}, {ordinateId})""");
        }
    }

    public static void UpdateCellSignature(this SqliteConnection generated, long cellId, string signature) =>
        generated.ExecEqu($"""UPDATE "mTableCell" SET "DatapointSignature" = '{signature}' WHERE "CellID" = {cellId}""");

    /// <summary>A term of an equality rule, already structured -- as if
    /// <c>LayoutEqualityRuleParser</c> had resolved it. <paramref name="term"/> is the raw text
    /// that <c>PlaneCEqualityChecker.EqualityRuleViolation.BusinessKey</c> reproduces as is.
    ///
    /// Each rule needs a SOURCE sheet (<paramref name="sheetId"/>, by default the
    /// <paramref name="ruleId"/> itself -- one rule, one sheet) with its <c>LayoutFile</c>
    /// (<paramref name="frameworkCode"/>/<paramref name="release"/>, by default matching
    /// <see cref="InsertTable"/> without extra arguments). <c>EnsureSheet</c> is idempotent:
    /// calling it twice with the same <paramref name="sheetId"/> (the several terms of ONE rule)
    /// does not duplicate the row.</summary>
    public static void InsertRuleTerm(
        this SqliteConnection layouts, long ruleId, int position, string term, string tableCode, string rowCode,
        string columnCode, string? zCode, long? sheetId = null,
        string frameworkCode = DefaultFrameworkCode, string release = DefaultRelease)
    {
        var effectiveSheetId = sheetId ?? ruleId;
        EnsureSheet(layouts, effectiveSheetId, frameworkCode, release);
        var z = zCode is null ? "NULL" : $"'{zCode}'";
        layouts.ExecEqu(
            $"""INSERT INTO "LayoutEqualityRule" VALUES ({ruleId}, {position}, '{term}', '{tableCode}', '{rowCode}', '{columnCode}', {z}, {effectiveSheetId})""");
    }

    private static void EnsureSheet(SqliteConnection layouts, long sheetId, string frameworkCode, string release)
    {
        using (var probe = layouts.CreateCommand())
        {
            probe.CommandText = $"""SELECT COUNT(*) FROM "LayoutSheet" WHERE "SheetId" = {sheetId}""";
            if (Convert.ToInt64(probe.ExecuteScalar()) > 0)
            {
                return;
            }
        }

        var fileId = sheetId; // 1:1 sheet/file in this fixture -- it does not matter for these tests.
        layouts.ExecEqu($"""INSERT INTO "LayoutFile" VALUES ({fileId}, '{frameworkCode}', '{release}')""");
        layouts.ExecEqu($"""INSERT INTO "LayoutSheet" VALUES ({sheetId}, {fileId}, NULL)""");
    }
}
