using EbaDpm.Converter.Core.Layouts;
using EbaDpm.Converter.Tests.Schema;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Tests.Layouts;

/// <summary>
/// <c>IsFallbackCode = 0</c> over the whole corpus is, again, a zero -- and it is worth nothing
/// until it is shown that the fallback branch KNOWS how to switch on and get marked. Fabricates,
/// outside the data directory, sheets without a code row (or with the row in another position) to
/// check it. The extraction needs the reference database as a dictionary, so the tests require the
/// data files.
/// </summary>
public sealed class LayoutOrdinateFallbackGateTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"EbaDpm.FallbackGate_{Environment.ProcessId}_{Guid.NewGuid():N}");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort.
        }
    }

    [DataFact]
    public void SheetWithNoCodeRowAtAll_FallsBackToColumnLetter_AndIsMarked()
    {
        // Row markerRow-1 (here, row 2) is EMPTY -- there is no cell shaped like a short code for
        // any column. BuildColumnCodeMap must return an empty map, and BuildXOrdinate must fall
        // back to the column letter AND mark it IsFallbackCode = 1.
        var inputDir = Path.Combine(_tempDir, "input1");
        new MinimalXlsxBuilder("T_NO_CODE_ROW")
            .WithCell("A3", "Rows")
            .WithCell("B10", "Some row label")
            .WithCell("C10", "0010")
            .WithCell("D10", "199116_x000D_text")
            .Save(inputDir);

        var outputPath = Path.Combine(_tempDir, "out1.db");
        LayoutExtractor.Extract(inputDir, outputPath, overwrite: false, RepoPaths.ReferenceDatabasePath);

        using var connection = Open(outputPath);
        AssertColumnOrdinateForCell(connection, "D10", expectedCode: "D", expectedFallback: true);
    }

    [DataFact]
    public void SheetWithCodeRowInTheWrongPosition_StillFallsBack_AndIsMarked()
    {
        // The real code "0010" exists in the sheet, but NOT in markerRow-1 (row 2): it is in row 1,
        // two rows above where BuildColumnCodeMap looks. It must still fall back to the letter,
        // marked -- the map does not "search" for the code, it only looks at the fixed position
        // measured over the real corpus (862/862 sheets).
        var inputDir = Path.Combine(_tempDir, "input2");
        new MinimalXlsxBuilder("T_CODE_ROW_WRONG_POS")
            .WithCell("D1", "0010")
            .WithCell("A3", "Rows")
            .WithCell("B10", "Some row label")
            .WithCell("C10", "0010")
            .WithCell("D10", "199116_x000D_text")
            .Save(inputDir);

        var outputPath = Path.Combine(_tempDir, "out2.db");
        LayoutExtractor.Extract(inputDir, outputPath, overwrite: false, RepoPaths.ReferenceDatabasePath);

        using var connection = Open(outputPath);
        AssertColumnOrdinateForCell(connection, "D10", expectedCode: "D", expectedFallback: true);
    }

    [DataFact]
    public void SheetWithCodeRowInTheRightPosition_DoesNotFallBack_ControlNegative()
    {
        // NEGATIVE control: same scenario, but with the real code in markerRow-1 (row 2, here
        // markerRow=3) -- it must not fall back to the letter. Without this control, the two
        // previous tests could be "always in fallback" for a different reason (e.g. a plumbing
        // error) and look like a valid positive proof without being one.
        var inputDir = Path.Combine(_tempDir, "input3");
        new MinimalXlsxBuilder("T_CODE_ROW_RIGHT_POS")
            .WithCell("D2", "0010")
            .WithCell("A3", "Rows")
            .WithCell("B10", "Some row label")
            .WithCell("C10", "0010")
            .WithCell("D10", "199116_x000D_text")
            .Save(inputDir);

        var outputPath = Path.Combine(_tempDir, "out3.db");
        LayoutExtractor.Extract(inputDir, outputPath, overwrite: false, RepoPaths.ReferenceDatabasePath);

        using var connection = Open(outputPath);
        AssertColumnOrdinateForCell(connection, "D10", expectedCode: "0010", expectedFallback: false);
    }

    /// <summary>The column code of a specific cell is read through <c>LayoutCell.CellRef</c> ->
    /// <c>ColumnOrdinateId</c> -> <c>LayoutOrdinate.OrdinateCode</c>: <c>LayoutOrdinate</c> has no
    /// <c>CellRef</c> of its own.</summary>
    private static void AssertColumnOrdinateForCell(SqliteConnection connection, string cellRef, string expectedCode, bool expectedFallback)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT o.OrdinateCode, o.IsFallbackCode FROM LayoutCell c
            JOIN LayoutOrdinate o ON o.OrdinateId = c.ColumnOrdinateId
            WHERE c.CellRef = $cr
            """;
        cmd.Parameters.AddWithValue("$cr", cellRef);
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read(), $"A LayoutCell should have been recorded for {cellRef}.");
        Assert.Equal(expectedCode, reader.GetString(0));
        Assert.Equal(expectedFallback ? 1L : 0L, reader.GetInt64(1));
    }

    private static SqliteConnection Open(string path)
    {
        var connectionString = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly }.ToString();
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }
}
