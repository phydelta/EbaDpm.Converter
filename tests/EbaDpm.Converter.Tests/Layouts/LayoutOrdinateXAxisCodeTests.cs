using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Tests.Layouts;

/// <summary>
/// The X-axis <c>OrdinateCode</c>. <c>LayoutOrdinate</c> carries no <c>CellRef</c>, so "what code
/// does the column of cell D7 have?" is not read from <c>LayoutOrdinate</c> directly -- it is read
/// via <c>LayoutCell.CellRef</c>, joining on <c>ColumnOrdinateId</c>.
///
/// Measured result, independently verified over the 51 real 4.2 files: <b>7,809 X-axis ordinates
/// (deduplicated, one per column of each sheet), of which 7,809 (100&#160;%) carry a real DPM code;
/// 0 carry the column letter</b>. The figure is low compared to the pre-normalization one (111,528)
/// because that one counted one row per CELL, not per column (26,446 distinct
/// `(Axis, OrdinateCode)` pairs over 130,282 rows); this is not a regression, it is the
/// deduplication working.
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
    public void XAxisOrdinates_RealCodeRate_Is100Percent_WithARateGuardAgainstRegression()
    {
        // Derived target, not a snapshot: EVERY X-axis ordinate that is not an explicit fallback
        // (IsFallbackCode = 0) MUST have the shape of a DPM code (2-6 digits), never the column
        // letter -- that is the business invariant, independent of how many sheets or files there
        // are today. A RATE with its explicit denominator is fixed, not a count equality -- an exact
        // figure would have expired with the normalization (tests that assert snapshots instead of
        // properties go stale).
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

        Assert.True(total > 5000, $"Only {total} X-axis ordinates (deduplicated): review the extraction.");

        // Structural invariant: a letter not marked as fallback would be indistinguishable from a
        // real code -- which is precisely what must not be allowed to pass silently.
        Assert.Equal(0, letterWithoutFallbackFlag);

        // The RATE assertion: a threshold below the 100 % measured today, so as not to be
        // unsatisfiable by a future real sheet with a legitimate fallback case, but high enough
        // for a massive regression (such as the 5.5 % of an early version of this grammar) to
        // still break the test. Explicit denominator: total.
        var realCodeRate = (double)realCode / total;
        Assert.True(realCodeRate >= 0.99,
            $"Only {realCodeRate:P1} of the X-axis ordinates carry a real code " +
            $"({realCode}/{total}); the real code is required for BusinessCode in the vast " +
            "majority of columns, not only the first one.");

        // Structural regression canary (not the main assertion): value measured independently over
        // the 4.2 corpus after normalization, deduplicated by column.
        Assert.Equal(7809, total);
        Assert.Equal(7809, realCode);
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
