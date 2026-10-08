using EbaDpm.Converter.Core.Layouts;
using EbaDpm.Converter.Tests.Schema;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Tests.Layouts;

/// <summary>
/// Covers the shaded/datapoint mutual exclusion of <c>LayoutCell</c> -- "a shaded cell, by
/// definition, carries no datapoint".
///
/// This is not enough as "only check that there are shaded cells": an early <c>IsShaded</c> could
/// NEVER be <c>true</c> (the first version of <c>LayoutCell</c> was only populated from cells WITH a
/// datapoint, so <c>IsShaded=1</c> was always <c>0</c>) and it was passed off as a "real result".
/// A test that only checked "0 rows both shaded and with a datapoint" would not have caught it:
/// the same zero would have come out with a dead field. The positive control below shows that the
/// query KNOWS how to find a violation when there is one.
/// </summary>
[Collection("LayoutsRealData")]
public sealed class LayoutCellShadedDatapointExclusionTests(LayoutRepositoryFixture fixture)
{
    [DataFact]
    public void RealData_NoCellIsBothShadedAndHasADatapoint_WithExplicitDenominators()
    {
        // The property, with BOTH denominators explicit (a "0" without the universe it examines is
        // not a measurement) -- not only "0 violations", but "0 of 31,898 shaded" and
        // "0 of 111,528/111,529 with a datapoint": the field discriminates both ways, neither
        // universe is empty.
        var con = fixture.Repository42WithDictionary;

        var totalCells = Scalar(con, "SELECT COUNT(*) FROM LayoutCell");
        var shadedTotal = Scalar(con, "SELECT COUNT(*) FROM LayoutCell WHERE IsShaded = 1");
        var datapointTotal = Scalar(con, "SELECT COUNT(*) FROM LayoutCell WHERE DatapointId IS NOT NULL");
        var violations = Scalar(con, "SELECT COUNT(*) FROM LayoutCell WHERE IsShaded = 1 AND DatapointId IS NOT NULL");

        Assert.True(totalCells > 100000, $"Only {totalCells} LayoutCell rows: review the extraction.");
        Assert.True(shadedTotal > 1000, $"Only {shadedTotal} shaded cells out of {totalCells}: the IsShaded field may be dead.");
        Assert.True(datapointTotal > 1000, $"Only {datapointTotal} cells with a datapoint out of {totalCells}: review the extraction.");

        Assert.Equal(0L, violations);
    }

    [DataFact]
    public void PositiveControl_AShadedCellWithARealDatapoint_IsDetectedByTheSameQuery()
    {
        // Mandatory positive control: a sheet is fabricated, outside the data directory, where the
        // SAME data cell (D10, with a real datapoint identifier) is marked as shaded with the exact
        // mechanism that XlsxReader.IsShadedStyle reads (styles.xml, fillId indexed=55) -- not a
        // shortcut of the test. The query of the property above MUST find 1 violation here, not
        // 0: if it found 0, the property above would be indistinguishable from a dead field.
        var tempDir = Path.Combine(Path.GetTempPath(), $"EbaDpm.ShadedDatapointGate_{Environment.ProcessId}_{Guid.NewGuid():N}");
        try
        {
            var inputDir = Path.Combine(tempDir, "input");
            new MinimalXlsxBuilder("T_SHADED_WITH_DATAPOINT")
                .WithCell("A3", "Rows")
                .WithCell("B10", "Some row label")
                .WithCell("C10", "0010")
                .WithShadedCell("D10", "199116_x000D_text") // shaded AND with a real datapoint: the fabricated violation
                .Save(inputDir);

            var outputPath = Path.Combine(tempDir, "out.db");
            LayoutExtractor.Extract(inputDir, outputPath, overwrite: false, RepoPaths.ReferenceDatabasePath);

            using var connection = Open(outputPath);

            // Control: the cell DID end up marked shaded AND with a datapoint -- if this failed,
            // the fabrication would not be testing what it claims to test.
            Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM LayoutCell WHERE CellRef = 'D10' AND IsShaded = 1 AND DatapointId IS NOT NULL"));

            // The SAME query as the real property: here it MUST find the violation.
            var violations = Scalar(connection, "SELECT COUNT(*) FROM LayoutCell WHERE IsShaded = 1 AND DatapointId IS NOT NULL");
            Assert.Equal(1L, violations);
        }
        finally
        {
            Cleanup(tempDir);
        }
    }

    [DataFact]
    public void NegativeControl_AShadedCellWithoutADatapoint_IsNotFlagged()
    {
        // Negative control (the positive one must be accompanied by one that stays quiet): a shaded
        // cell WITHOUT a datapoint (the normal case) must not be counted as a violation -- if this
        // test started to fail, the query would have become too sensitive.
        var tempDir = Path.Combine(Path.GetTempPath(), $"EbaDpm.ShadedDatapointGateNeg_{Environment.ProcessId}_{Guid.NewGuid():N}");
        try
        {
            var inputDir = Path.Combine(tempDir, "input");
            new MinimalXlsxBuilder("T_SHADED_NO_DATAPOINT")
                .WithCell("D2", "0020") // code row (markerRow-1): seeds the column ordinate
                .WithCell("A3", "Rows")
                .WithCell("B10", "Some row label")
                .WithCell("C10", "0010")
                .WithShadedCell("D10") // shaded, no text -- the normal case of a disabled cell
                .Save(inputDir);

            var outputPath = Path.Combine(tempDir, "out.db");
            LayoutExtractor.Extract(inputDir, outputPath, overwrite: false, RepoPaths.ReferenceDatabasePath);

            using var connection = Open(outputPath);

            Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM LayoutCell WHERE CellRef = 'D10' AND IsShaded = 1"));
            Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM LayoutCell WHERE IsShaded = 1 AND DatapointId IS NOT NULL"));
        }
        finally
        {
            Cleanup(tempDir);
        }
    }

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

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
