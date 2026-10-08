using EbaDpm.Converter.Core.Layouts;
using EbaDpm.Converter.Tests.Schema;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Tests.Layouts;

/// <summary>
/// Covers a defect that had no test: <b>the set of ordinates of an axis is that of its row
/// (column) of CODES, not that of the cells with visible content.</b> <c>J_05.00.a</c> is the case
/// that exposed it: its row 10 carries all 39 column codes, complete and consecutive
/// (<c>D=0010, E=0020 ... AL=0350</c>... up to <c>0390</c>), but the earlier extractor only created
/// an ordinate for the columns where it found a datapoint -- so two blocks of six entirely shaded
/// columns (without a single visible datum) disappeared from the X axis even though their code was
/// written.
/// </summary>
[Collection("LayoutsRealData")]
public sealed class LayoutColumnCodeCoverageTests(LayoutRepositoryFixture fixture)
{
    [DataFact]
    public void J_05_00_a_XAxis_Has39Ordinates_TheFullCodeRow_NotJustColumnsWithVisibleData()
    {
        var con = fixture.Repository42WithDictionary;
        var sheetId = QueryScalarLong(con, "SELECT SheetId FROM LayoutSheet WHERE TableCode = 'J_05.00.a' LIMIT 1");

        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT OrdinateCode FROM LayoutOrdinate WHERE SheetId = $sid AND Axis = 'X' ORDER BY OrdinateCode";
        cmd.Parameters.AddWithValue("$sid", sheetId);
        using var reader = cmd.ExecuteReader();
        var codes = new List<string>();
        while (reader.Read())
        {
            codes.Add(reader.GetString(0));
        }

        Assert.Equal(39, codes.Count);

        // The specific columns the original defect skipped: two blocks of six, entirely shaded in
        // the visible part ("Notional amount/optionality/Bought/Sold/yield/maturity" of the fixed
        // and the floating leg). If they disappeared again, this test detects it by code, not by
        // an aggregate count.
        var expectedPreviouslyMissing = new[] { "0010", "0020", "0030", "0040", "0050", "0060", "0260", "0270", "0280", "0290", "0300", "0310" };
        Assert.All(expectedPreviouslyMissing, code => Assert.Contains(code, codes));

        // The code goes from 0010 to 0390 in steps of 10, with no gaps -- the code row is continuous.
        var expectedAll = Enumerable.Range(1, 39).Select(i => (i * 10).ToString("0000")).ToList();
        Assert.Equal(expectedAll, codes);
    }

    [DataFact]
    public void J_05_00_a_CellCount_IsExactlyTheFullCartesianProduct_39By49()
    {
        // Regression canary of the full magnitude, measured independently: 39 columns x 49 rows =
        // 1,911 cells, of which 588 are shaded.
        var con = fixture.Repository42WithDictionary;
        var sheetId = QueryScalarLong(con, "SELECT SheetId FROM LayoutSheet WHERE TableCode = 'J_05.00.a' LIMIT 1");

        var xCount = QueryScalarLong(con, $"SELECT COUNT(*) FROM LayoutOrdinate WHERE SheetId = {sheetId} AND Axis = 'X'");
        var yCount = QueryScalarLong(con, $"SELECT COUNT(*) FROM LayoutOrdinate WHERE SheetId = {sheetId} AND Axis = 'Y'");
        var cellCount = QueryScalarLong(con, $"SELECT COUNT(*) FROM LayoutCell WHERE SheetId = {sheetId}");
        var shadedCount = QueryScalarLong(con, $"SELECT COUNT(*) FROM LayoutCell WHERE SheetId = {sheetId} AND IsShaded = 1");

        Assert.Equal(39L, xCount);
        Assert.Equal(49L, yCount);
        Assert.Equal(xCount * yCount, cellCount);
        Assert.Equal(1911L, cellCount);
        Assert.Equal(588L, shadedCount);
    }

    [DataFact]
    public void PositiveControl_AColumnEntirelyShaded_WithNoVisibleDataAnywhere_StillGetsAnOrdinate()
    {
        // Positive control: reproduces the original defect IN MINIATURE. A column (E) whose code IS
        // in the code row (row 2, markerRow-1) but which has NOT A SINGLE visible datum in any row
        // of the grid -- only grey fill. Under the earlier behaviour that column would not have
        // generated any X LayoutOrdinate (the extractor only created one when it found a
        // datapoint). It must now still exist, with its LayoutCell rows entirely shaded.
        var tempDir = Path.Combine(Path.GetTempPath(), $"EbaDpm.ColumnCoverageGate_{Environment.ProcessId}_{Guid.NewGuid():N}");
        try
        {
            var inputDir = Path.Combine(tempDir, "input");
            new MinimalXlsxBuilder("T_SHADED_COLUMN_NO_DATA")
                .WithCell("D2", "0010")
                .WithCell("E2", "0020") // column E: a real code, but NEVER a visible datum
                .WithCell("F2", "0030")
                .WithCell("A3", "Rows")
                .WithCell("B10", "Row one")
                .WithCell("C10", "0010")
                .WithCell("D10", "100001_x000D_text")
                .WithShadedCell("E10") // shaded, no datum -- the whole of column E is like this
                .WithCell("F10", "100002_x000D_text")
                .WithCell("B11", "Row two")
                .WithCell("C11", "0020")
                .WithCell("D11", "100003_x000D_text")
                .WithShadedCell("E11")
                .WithCell("F11", "100004_x000D_text")
                .Save(inputDir);

            var outputPath = Path.Combine(tempDir, "out.db");
            LayoutExtractor.Extract(inputDir, outputPath, overwrite: false, RepoPaths.ReferenceDatabasePath);

            using var connection = Open(outputPath);

            // Column E DOES have its X ordinate, with its real code -- it does not disappear for
            // lacking a single visible datum.
            using var cmdOrdinates = connection.CreateCommand();
            cmdOrdinates.CommandText = "SELECT OrdinateCode FROM LayoutOrdinate WHERE Axis = 'X' ORDER BY OrdinateCode";
            using var reader = cmdOrdinates.ExecuteReader();
            var codes = new List<string>();
            while (reader.Read())
            {
                codes.Add(reader.GetString(0));
            }

            Assert.Equal(["0010", "0020", "0030"], codes);

            // And its two cells (E10, E11) exist, shaded, with no datapoint.
            Assert.Equal(2L, Scalar(connection, "SELECT COUNT(*) FROM LayoutCell WHERE CellRef IN ('E10','E11') AND IsShaded = 1 AND DatapointId IS NULL"));

            // The full cartesian product: 3 columns x 2 rows = 6 cells.
            Assert.Equal(6L, Scalar(connection, "SELECT COUNT(*) FROM LayoutCell"));
        }
        finally
        {
            Cleanup(tempDir);
        }
    }

    private static long QueryScalarLong(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static long Scalar(SqliteConnection connection, string sql) => QueryScalarLong(connection, sql);

    private static SqliteConnection Open(string path)
    {
        var connectionString = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly }.ToString();
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }

    private static void Cleanup(string tempDir)
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort.
        }
    }
}
