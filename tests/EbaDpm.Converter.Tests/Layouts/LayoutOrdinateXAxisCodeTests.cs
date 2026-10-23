using System.Text.RegularExpressions;
using EbaDpm.Converter.Tests.Infrastructure;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Tests.Layouts;

/// <summary>
/// The X-axis <c>OrdinateCode</c>. <c>LayoutOrdinate</c> carries no <c>CellRef</c>, so "what code
/// does the column of cell D7 have?" is not read from <c>LayoutOrdinate</c> directly -- it is read
/// via <c>LayoutCell.CellRef</c>, joining on <c>ColumnOrdinateId</c>.
///
/// Measured result, independently verified over the 4.2 layouts
/// (<see cref="ReleasePins.LayoutExtraction42"/>): the X-axis ordinates (deduplicated, one per column
/// of each sheet) ALL carry a real DPM code and none carries the column letter. The count is one per
/// column, not one per CELL: this is the deduplication working.
/// </summary>
[Collection("LayoutsRealData")]
public sealed class LayoutOrdinateXAxisCodeTests(LayoutRepositoryFixture fixture)
{
    private static readonly Regex ColumnLetterPattern = new(@"^[A-Z]+$");
    private static readonly Regex DpmCodePattern = new(@"^\d{2,6}$");

    [DataFact]
    public void RealCodeRow_IsPresentInRawButNeverBecomesTheOrdinateCode()
    {
        // C_106.00, row 6 carries the real code "0010" right above row 7 (datapoint+type), and it
        // is used -- read through LayoutCell, which is where CellRef lives.
        var con = fixture.Repository42WithDictionary;
        var sheetId = QueryScalarLong(con, "SELECT SheetId FROM LayoutSheet WHERE TableCode = 'C_106.00' LIMIT 1");

        var rawD6 = QueryRawValue(con, sheetId, "D6");
        Assert.Equal("0010", rawD6);

        Assert.Equal("0010", QueryColumnOrdinateCodeForCell(con, sheetId, "D7"));
    }

    [DataFact]
    public void RealCodeRow_C_06_02_IsPresentInRawButNeverBecomesTheOrdinateCode()
    {
        var con = fixture.Repository42WithDictionary;
        var sheetId = QueryScalarLong(con, "SELECT SheetId FROM LayoutSheet WHERE TableCode = 'C_06.02' LIMIT 1");

        var rawD10 = QueryRawValue(con, sheetId, "D10");
        Assert.Equal("0011", rawD10);

        Assert.Equal("0011", QueryColumnOrdinateCodeForCell(con, sheetId, "D11"));
    }

    [DataFact]
    public void SecondDataRow_F_32_01_NowGetsTheRealCodeToo_TheGeneralCaseIsCovered()
    {
        // The column -> code map does not depend on which row is "right above", so it covers ALL
        // the rows of the column, not only the first one -- and with the cartesian product this is
        // a structural property, not a one-off hit: EVERY cell of column E carries the same column
        // code, by construction.
        var con = fixture.Repository42WithDictionary;
        var sheetId = QueryScalarLong(con, "SELECT SheetId FROM LayoutSheet WHERE TableCode = 'F_32.01' LIMIT 1");

        Assert.Equal("0020", QueryColumnOrdinateCodeForCell(con, sheetId, "E8"));
        Assert.Equal("0020", QueryColumnOrdinateCodeForCell(con, sheetId, "E9"));

        // And column D (which used to be consumed by the row ordinate, Y, without generating its
        // own X ordinate): D8 and D9 must carry "0010" in BOTH rows.
        Assert.Equal("0010", QueryColumnOrdinateCodeForCell(con, sheetId, "D8"));
        Assert.Equal("0010", QueryColumnOrdinateCodeForCell(con, sheetId, "D9"));
    }

    [DataFact]
    public void XAxisOrdinates_AllCarryARealCode_NoneTheColumnLetter()
    {
        // EVERY X-axis ordinate that is not an explicit fallback (IsFallbackCode = 0) MUST have the
        // shape of a DPM code (2-6 digits), never the column letter -- that is the business
        // invariant. Its numerator (ordinates with a real code) and denominator (all X ordinates)
        // are pinned to the 4.2 extraction, and the identity between the classes is asserted.
        var con = fixture.Repository42WithDictionary;

        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT OrdinateCode, IsFallbackCode FROM LayoutOrdinate WHERE Axis = 'X'";
        using var reader = cmd.ExecuteReader();

        var total = 0;
        var columnLetter = 0;
        var realCode = 0;
        var fallbackFlagged = 0;
        var letterWithoutFallbackFlag = 0;
        while (reader.Read())
        {
            var code = reader.GetString(0);
            var isFallback = reader.GetInt64(1) != 0;
            total++;
            if (isFallback)
            {
                fallbackFlagged++;
            }

            if (ColumnLetterPattern.IsMatch(code))
            {
                columnLetter++;
                if (!isFallback)
                {
                    letterWithoutFallbackFlag++;
                }
            }
            else if (DpmCodePattern.IsMatch(code))
            {
                realCode++;
            }
        }

        // Structural invariant: a letter not marked as fallback would be indistinguishable from a
        // real code -- which is precisely what must not be allowed to pass silently.
        Assert.Equal(0, letterWithoutFallbackFlag);

        // Exact numerator and denominator: the real code is required for BusinessCode in every
        // column, not only the first one.
        ReleasePins.AssertPinned(ReleasePins.LayoutExtraction42, "X ordinates (denominator)", ReleasePins.LayoutExtraction42XOrdinates, total);
        ReleasePins.AssertPinned(ReleasePins.LayoutExtraction42, "X ordinates with a real code (numerator)", ReleasePins.LayoutExtraction42XOrdinates, realCode);
        Assert.Equal(0, columnLetter);
        Assert.Equal(0, fallbackFlagged);
    }

    private static long QueryScalarLong(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static string? QueryRawValue(SqliteConnection connection, long sheetId, string cellRef)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Value FROM LayoutRaw WHERE SheetId = $sid AND CellRef = $cr";
        cmd.Parameters.AddWithValue("$sid", sheetId);
        cmd.Parameters.AddWithValue("$cr", cellRef);
        return (string?)cmd.ExecuteScalar();
    }

    /// <summary>The code of the COLUMN ordinate that corresponds to <paramref name="cellRef"/>,
    /// read through the correct path: <c>LayoutCell.CellRef</c> -> <c>ColumnOrdinateId</c> ->
    /// <c>LayoutOrdinate.OrdinateCode</c>. There is no direct <c>LayoutOrdinate.CellRef</c>
    /// column.</summary>
    private static string? QueryColumnOrdinateCodeForCell(SqliteConnection connection, long sheetId, string cellRef)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT o.OrdinateCode FROM LayoutCell c
            JOIN LayoutOrdinate o ON o.OrdinateId = c.ColumnOrdinateId
            WHERE c.SheetId = $sid AND c.CellRef = $cr
            """;
        cmd.Parameters.AddWithValue("$sid", sheetId);
        cmd.Parameters.AddWithValue("$cr", cellRef);
        return (string?)cmd.ExecuteScalar();
    }
}
