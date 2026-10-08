using EbaDpm.Converter.Core.Sqlite;
using EbaDpm.Converter.Core.Validation;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Tests.PlaneC;

/// <summary>
/// <c>C-KEY-01</c> cannot be an empty zero. On an output with NO open-axis key column at all, it
/// has to say <c>Examined=0</c> WITH the reason -- never stay silent (it cannot be absent from the
/// report, nor print a <c>0</c> without explanation). <c>Validator.Run</c> level (through
/// <c>SchemaCreator</c>), the same pattern as <see cref="PlaneCValidateFlagTests"/>: it is the only
/// level at which <c>C-KEY-01</c> exists as a <c>CheckResult</c> with a <c>Statement</c> --
/// <see cref="EbaDpm.Converter.Core.Validation.PlaneC.PlaneCComparer"/> only exposes the raw counter.
/// </summary>
public sealed class PlaneCKeyColumnGuardTests : IDisposable
{
    private readonly string _tempDirectory;

    public PlaneCKeyColumnGuardTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"EbaDpm.KeyColumnGuardTests_{Environment.ProcessId}_{Guid.NewGuid():N}");
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
    /// With no signature of the "key column" shape in the layout repository, <c>C-KEY-01</c> still
    /// appears in the report, with <c>Examined=0</c> AND the text explaining what it would measure
    /// if there were any -- never an absent check, never a silent <c>0</c>.
    /// </summary>
    [Fact]
    public void CKey01_WithNoKeyColumn_Examined0_WithTheReasonWritten_NotSilent()
    {
        var generatedPath = Path.Combine(_tempDirectory, "generated.db");
        SchemaCreator.Create(generatedPath, overwrite: false);
        InsertNormalTableAndTaxonomy(generatedPath);

        var layoutsPath = Path.Combine(_tempDirectory, "layouts.db");
        CreateLayoutsWithNormalSheet(layoutsPath);

        SqliteConnection.ClearAllPools();
        var result = Validator.Run(generatedPath, referencePath: null, layoutsPath);

        var cKey01 = Assert.Single(result.Report.Checks, c => c.Id == "C-KEY-01");

        Assert.Equal(0, cKey01.Examined);
        Assert.Equal(0, cKey01.Failed);
        Assert.Equal("info", cKey01.Status);

        // The reason has to be WRITTEN -- a check that is present with Examined=0 and no
        // explanation is indistinguishable from "nothing was checked", the same empty zero.
        Assert.False(string.IsNullOrWhiteSpace(cKey01.Statement));
        Assert.Contains("key column", cKey01.Statement, StringComparison.OrdinalIgnoreCase);

        // With no exclusion, there are no samples to name -- but that is consistent with the
        // reason (0 signatures of that shape), not an additional silence.
        Assert.Empty(cKey01.Samples);
    }

    /// <summary>Direct contrast with the previous test, in the SAME suite: with at least one key
    /// column, <c>C-KEY-01</c> stops being 0 and names the table -- it confirms that the 0 above is
    /// really "there were none", not a silent failure of the check itself.</summary>
    [Fact]
    public void CKey01_WithAKeyColumn_ExaminedGreaterThanZero_AndNamesTheTable()
    {
        var generatedPath = Path.Combine(_tempDirectory, "generated.db");
        SchemaCreator.Create(generatedPath, overwrite: false);
        InsertNormalTableAndTaxonomy(generatedPath);

        var layoutsPath = Path.Combine(_tempDirectory, "layouts.db");
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = layoutsPath }.ToString()))
        {
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

                INSERT INTO "LayoutFile" VALUES (1, 'COREP', '4.2');
                INSERT INTO "LayoutSheet" VALUES (1, 1, 'N_TEST');
                INSERT INTO "LayoutDeclaration" VALUES (1, 1, NULL, 'MET', 0);
                INSERT INTO "LayoutValue" VALUES (1, 1, 1, 10, 'qFAB');
                INSERT INTO "LayoutDeclaration" VALUES (2, 1, 'qFAB', NULL, 1);
                INSERT INTO "LayoutCell" VALUES (1, 1, NULL, 10, 0);
                """;
            cmd.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
        var result = Validator.Run(generatedPath, referencePath: null, layoutsPath);

        var cKey01 = Assert.Single(result.Report.Checks, c => c.Id == "C-KEY-01");

        Assert.Equal(1, cKey01.Examined);
        Assert.Contains(cKey01.Samples, s => s.BusinessKey.StartsWith("N_TEST", StringComparison.Ordinal));
    }

    private static void InsertNormalTableAndTaxonomy(string generatedPath)
    {
        using var connection = OpenReadWrite(generatedPath);
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO "mReportingFramework" ("FrameworkID", "FrameworkCode") VALUES (1, 'COREP');
            INSERT INTO "mTaxonomy" ("TaxonomyID", "TaxonomyCode", "FrameworkID") VALUES (100, 'corep 4.2', 1);
            INSERT INTO "mTable" ("TableID", "TableCode") VALUES (1, 'N_TEST');
            INSERT INTO "mTaxonomyTable" ("TaxonomyID", "TableID", "AnnotatedTableID") VALUES (100, 1, 1);
            INSERT INTO "mAxis" ("AxisID", "AxisOrientation", "IsOpenAxis") VALUES (10, 'Y', 0);
            INSERT INTO "mAxis" ("AxisID", "AxisOrientation", "IsOpenAxis") VALUES (11, 'X', 0);
            INSERT INTO "mTableAxis" ("AxisID", "TableID", "Order") VALUES (10, 1, 0);
            INSERT INTO "mTableAxis" ("AxisID", "TableID", "Order") VALUES (11, 1, 1);
            INSERT INTO "mAxisOrdinate" ("OrdinateID", "AxisID", "OrdinateCode", "Order") VALUES (100, 10, '0010', 0);
            INSERT INTO "mAxisOrdinate" ("OrdinateID", "AxisID", "OrdinateCode", "Order") VALUES (101, 11, '0010', 0);
            INSERT INTO "mCellPosition" ("CellID", "OrdinateID") VALUES (1000, 100);
            INSERT INTO "mCellPosition" ("CellID", "OrdinateID") VALUES (1000, 101);
            INSERT INTO "mTableCell" ("CellID", "TableID", "IsShaded", "DPS") VALUES (1000, 1, 0, 'MET(qBBU)|DIM(m1)');
            """;
        cmd.ExecuteNonQuery();
    }

    private static void CreateLayoutsWithNormalSheet(string path)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
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

            INSERT INTO "LayoutFile" VALUES (1, 'COREP', '4.2');
            INSERT INTO "LayoutSheet" VALUES (1, 1, 'N_TEST');
            INSERT INTO "LayoutDeclaration" VALUES (1, 1, NULL, 'MET', 0);
            INSERT INTO "LayoutValue" VALUES (1, 1, 1, 10, 'qBBU');
            INSERT INTO "LayoutDeclaration" VALUES (2, 1, 'DIM', NULL, 0);
            INSERT INTO "LayoutValue" VALUES (2, 1, 2, 10, 'm1');
            INSERT INTO "LayoutCell" VALUES (1, 1, NULL, 10, 0);
            """;
        cmd.ExecuteNonQuery();
    }

    private static SqliteConnection OpenReadWrite(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite }.ToString());
        connection.Open();
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = OFF;";
            pragma.ExecuteNonQuery();
        }

        return connection;
    }
}
