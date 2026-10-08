using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Tests.Layouts;

/// <summary>
/// Structural property of the extracted layout repository: the row x column CARTESIAN PRODUCT.
/// <c>LayoutOrdinate</c> holds one row per <c>(SheetId, Axis, OrdinateCode)</c> (the cell
/// reference lives in <c>LayoutCell</c>), and the (row code, column code) pair of each cell is read
/// through a direct FOREIGN KEY (<c>LayoutCell.RowOrdinateId</c>/<c>ColumnOrdinateId</c>), not by
/// parsing <c>CellRef</c> and joining on the row number.
///
/// Every closed-axis sheet has exactly <c>|Y| x |X|</c> cells, no more and no fewer, and every
/// <c>LayoutCell</c> resolves its two ordinates by FK without ambiguity.
/// </summary>
[Collection("LayoutsRealData")]
public sealed class LayoutCellReconstructionTests(LayoutRepositoryFixture fixture)
{
    [DataFact]
    public void ClosedAxisSheets_CellCount_IsExactlyTheCartesianProductOfItsOrdinates()
    {
        // In EVERY closed-axis sheet (non-null RowOrdinateId) the number of LayoutCell rows is
        // EXACTLY |Y ordinates of the sheet| x |X ordinates of the sheet| -- not approximately,
        // not "most of them". This is a property of CONSTRUCTION (LayoutSheetParser.PopulateCells),
        // not a fact about the corpus: it must stay true even if the figures change tomorrow.
        var con = fixture.Repository42WithDictionary;

        using var cmd = con.CreateCommand();
        cmd.CommandText =
            """
            SELECT lc.SheetId,
                   COUNT(*) AS cells,
                   COUNT(DISTINCT lc.RowOrdinateId) AS ys,
                   COUNT(DISTINCT lc.ColumnOrdinateId) AS xs
            FROM LayoutCell lc
            WHERE lc.RowOrdinateId IS NOT NULL
            GROUP BY lc.SheetId
            HAVING cells != ys * xs
            """;
        using var reader = cmd.ExecuteReader();
        var violations = new List<string>();
        while (reader.Read())
        {
            violations.Add($"SheetId={reader.GetInt64(0)} cells={reader.GetInt64(1)} ys={reader.GetInt64(2)} xs={reader.GetInt64(3)}");
        }

        Assert.True(violations.Count == 0,
            $"{violations.Count} closed-axis sheet(s) do NOT have exactly |Y|x|X| cells: {string.Join("; ", violations.Take(10))}");

        // Explicit denominator: how many sheets were checked, so that "0 violations" over 0 sheets
        // is not mistaken for a real check.
        using var sheetCountCmd = con.CreateCommand();
        sheetCountCmd.CommandText = "SELECT COUNT(DISTINCT SheetId) FROM LayoutCell WHERE RowOrdinateId IS NOT NULL";
        var closedAxisSheets = Convert.ToInt64(sheetCountCmd.ExecuteScalar());
        Assert.True(closedAxisSheets > 500, $"Only {closedAxisSheets} closed-axis sheets were checked; review the extraction.");
    }

    [DataFact]
    public void EveryLayoutCell_ResolvesItsOrdinatesByForeignKey_ToTheCorrectAxisInTheSameSheet()
    {
        // The referential-integrity property that "PRAGMA foreign_keys = OFF" stops enforcing at
        // load time: for every LayoutCell, RowOrdinateId (when not NULL) must point to a real
        // LayoutOrdinate with Axis='Y' of the SAME sheet; ColumnOrdinateId (always non-null) must
        // point to Axis='X' of the same sheet. 0 exceptions over the whole corpus.
        var con = fixture.Repository42WithDictionary;

        using var totalCmd = con.CreateCommand();
        totalCmd.CommandText = "SELECT COUNT(*) FROM LayoutCell";
        var total = Convert.ToInt64(totalCmd.ExecuteScalar());
        Assert.True(total > 100000, $"Only {total} LayoutCell rows in the 4.2 corpus; review the extraction.");

        using var badRowCmd = con.CreateCommand();
        badRowCmd.CommandText =
            """
            SELECT COUNT(*) FROM LayoutCell lc
            WHERE lc.RowOrdinateId IS NOT NULL AND NOT EXISTS (
                SELECT 1 FROM LayoutOrdinate y
                WHERE y.OrdinateId = lc.RowOrdinateId AND y.Axis = 'Y' AND y.SheetId = lc.SheetId
            )
            """;
        Assert.Equal(0L, Convert.ToInt64(badRowCmd.ExecuteScalar()));

        using var badColCmd = con.CreateCommand();
        badColCmd.CommandText =
            """
            SELECT COUNT(*) FROM LayoutCell lc
            WHERE NOT EXISTS (
                SELECT 1 FROM LayoutOrdinate x
                WHERE x.OrdinateId = lc.ColumnOrdinateId AND x.Axis = 'X' AND x.SheetId = lc.SheetId
            )
            """;
        Assert.Equal(0L, Convert.ToInt64(badColCmd.ExecuteScalar()));
    }

    [DataFact]
    public void KnownSmallTable_F3201_CellCount_MatchesItsOwnOrdinateCounts()
    {
        // A concrete, small case that can be verified by hand (a real anchor, not just
        // aggregates): F_32.01 acts as a regression canary on a known sheet, complementing the
        // general property above.
        var con = fixture.Repository42WithDictionary;
        var sheetId = QueryScalarLong(con, "SELECT SheetId FROM LayoutSheet WHERE TableCode = 'F_32.01' LIMIT 1");

        var xCount = QueryScalarLong(con, $"SELECT COUNT(*) FROM LayoutOrdinate WHERE SheetId = {sheetId} AND Axis = 'X'");
        var yCount = QueryScalarLong(con, $"SELECT COUNT(*) FROM LayoutOrdinate WHERE SheetId = {sheetId} AND Axis = 'Y'");
        var cellCount = QueryScalarLong(con, $"SELECT COUNT(*) FROM LayoutCell WHERE SheetId = {sheetId}");

        Assert.True(xCount > 0 && yCount > 0, "F_32.01 should have ordinates on both axes.");
        Assert.Equal(xCount * yCount, cellCount);
    }

    [DataFact]
    public void C_106_00_TypedDimensionTable_HasNoRowOrdinate_ByDesign_NotByLoss()
    {
        // C_106.00 is an "Open Rows" table: the row is supplied by whoever reports, it is not
        // fixed by the layout. It has NO Y ordinate, and that is correct: there is no row code to
        // lose. With this model it also means its LayoutCell rows are the ones "seen" while
        // parsing, not a cartesian product -- the test checks explicitly that the two figures
        // coincide.
        var con = fixture.Repository42WithDictionary;
        var sheetId = QueryScalarLong(con, "SELECT SheetId FROM LayoutSheet WHERE TableCode = 'C_106.00' LIMIT 1");

        Assert.Equal(0L, QueryScalarLong(con, $"SELECT COUNT(*) FROM LayoutOrdinate WHERE SheetId = {sheetId} AND Axis = 'Y'"));
        Assert.Equal(7L, QueryScalarLong(con, $"SELECT COUNT(*) FROM LayoutOrdinate WHERE SheetId = {sheetId} AND Axis = 'X'"));

        // Open row axis: the cell is kept exactly as the parse saw it -- NOT the cartesian product
        // (which would be 0 here, as there is no Y at all), but the cells actually seen.
        var cellCount = QueryScalarLong(con, $"SELECT COUNT(*) FROM LayoutCell WHERE SheetId = {sheetId}");
        Assert.Equal(7L, cellCount);
        Assert.Equal(0L, QueryScalarLong(con, $"SELECT COUNT(*) FROM LayoutCell WHERE SheetId = {sheetId} AND RowOrdinateId IS NOT NULL"));
    }

    private static long QueryScalarLong(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }
}
