using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Tests.PlaneC;

/// <summary>
/// Minimal, in-memory construction of the two halves that
/// <see cref="EbaDpm.Converter.Core.Validation.PlaneC.PlaneCComparer"/> needs: a subset of the
/// generated output (<c>mTable</c>...<c>mAxisOrdinate</c>) and a subset of the layout repository
/// (<c>LayoutFile</c>...<c>LayoutCell</c>). Only the columns that
/// <c>PlaneCComparer</c>/<c>PlaneCLayoutReader</c> really query -- it is not a copy of the
/// production schema, it is the smallest fixture that exercises the real code without touching
/// <c>Data/</c>.
/// </summary>
internal static class PlaneCSyntheticDatabase
{
    public static SqliteConnection OpenGenerated()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            CREATE TABLE "mTable" ("TableID" INTEGER PRIMARY KEY, "TableCode" TEXT);
            CREATE TABLE "mTableCell" ("CellID" INTEGER PRIMARY KEY, "TableID" INTEGER, "DPS" TEXT);
            CREATE TABLE "mReportingFramework" ("FrameworkID" INTEGER PRIMARY KEY, "FrameworkCode" TEXT);
            CREATE TABLE "mTaxonomy" ("TaxonomyID" INTEGER PRIMARY KEY, "TaxonomyCode" TEXT, "FrameworkID" INTEGER);
            CREATE TABLE "mTaxonomyTable" ("TaxonomyID" INTEGER, "TableID" INTEGER);
            CREATE TABLE "mAxis" ("AxisID" INTEGER PRIMARY KEY, "AxisOrientation" TEXT, "IsOpenAxis" INTEGER);
            CREATE TABLE "mTableAxis" ("TableID" INTEGER, "AxisID" INTEGER);
            CREATE TABLE "mAxisOrdinate" ("OrdinateID" INTEGER PRIMARY KEY, "AxisID" INTEGER);
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
            CREATE TABLE "LayoutDeclaration" (
                "DeclId" INTEGER PRIMARY KEY, "SheetId" INTEGER, "DimensionCode" TEXT, "DomainCode" TEXT, "IsKey" INTEGER);
            CREATE TABLE "LayoutValue" (
                "ValueId" INTEGER PRIMARY KEY, "SheetId" INTEGER, "DeclId" INTEGER, "OrdinateId" INTEGER, "MemberCode" TEXT);
            CREATE TABLE "LayoutCell" (
                "CellId" INTEGER PRIMARY KEY, "SheetId" INTEGER, "RowOrdinateId" INTEGER,
                "ColumnOrdinateId" INTEGER, "IsShaded" INTEGER);
            """;
        cmd.ExecuteNonQuery();
        return connection;
    }

    public static void Exec(this SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Registers a table with its taxonomy link "{lowercase framework} {release}".</summary>
    public static void InsertTableWithTaxonomy(
        this SqliteConnection generated, long tableId, string tableCode, long taxonomyId, string frameworkCode, string release)
    {
        generated.Exec($"""INSERT INTO "mTable" VALUES ({tableId}, '{tableCode}')""");
        var frameworkId = taxonomyId * 1000; // arbitrary, unique per test case
        generated.Exec($"""INSERT INTO "mReportingFramework" VALUES ({frameworkId}, '{frameworkCode}')""");
        generated.Exec($"""INSERT INTO "mTaxonomy" VALUES ({taxonomyId}, '{frameworkCode.ToLowerInvariant()} {release}', {frameworkId})""");
        generated.Exec($"""INSERT INTO "mTaxonomyTable" VALUES ({taxonomyId}, {tableId})""");
    }

    public static void InsertCellDps(this SqliteConnection generated, long tableId, string dps) =>
        generated.Exec($"""INSERT INTO "mTableCell" ("TableID", "DPS") VALUES ({tableId}, '{dps}')""");
}
