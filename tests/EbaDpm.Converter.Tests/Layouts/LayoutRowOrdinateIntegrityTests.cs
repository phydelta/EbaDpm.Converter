using EbaDpm.Converter.Core.Layouts;
using EbaDpm.Converter.Tests.Schema;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Tests.Layouts;

/// <summary>
/// Integrity of <c>LayoutCell.RowOrdinateId</c>: it points from the CELL to its row ordinate (it is
/// not a self-reference inside <c>LayoutOrdinate</c>).
///
/// <b>Key finding</b>: the invariant "<c>RowOrdinateId</c> is <c>NULL</c> only in sheets without
/// any Y ordinate" could in principle be broken by an adversarial construction (a datapoint in the
/// HEADER region, outside any Y row, in a sheet that DOES have Y elsewhere). Under the current
/// model (<c>PopulateCells</c>, "cells are not searched for, they are GENERATED") that counterexample
/// does NOT reproduce -- the adversarial cell simply never comes to exist in <c>LayoutCell</c>,
/// because the cartesian product only generates cells at the positions
/// <c>(row of a Y ordinate, column of an X ordinate)</c>, and the row of the header cell is never
/// that of a Y ordinate. This is verified here, not assumed.
/// </summary>
[Collection("LayoutsRealData")]
public sealed class LayoutRowOrdinateIntegrityTests(LayoutRepositoryFixture fixture)
{
    [DataFact]
    public void RealData_RowOrdinateIdNull_OnlyInSheetsWithoutAnyYOrdinate_ZeroExceptions()
    {
        // The business property, with an explicit RATE and denominator -- not a snapshot. Over the
        // real 4.2 corpus: 141,550 of 143,427 LayoutCell rows have RowOrdinateId populated; the
        // rest (1,877) belong to open-row-axis sheets (such as C_106.00) -- 0 exceptions to that rule.
        var con = fixture.Repository42WithDictionary;

        using var totalCmd = con.CreateCommand();
        totalCmd.CommandText = "SELECT COUNT(*) FROM LayoutCell";
        var total = Convert.ToInt64(totalCmd.ExecuteScalar());
        Assert.True(total > 100000, $"Only {total} LayoutCell rows in the 4.2 corpus; review the extraction.");

        using var populatedCmd = con.CreateCommand();
        populatedCmd.CommandText = "SELECT COUNT(*) FROM LayoutCell WHERE RowOrdinateId IS NOT NULL";
        var populated = Convert.ToInt64(populatedCmd.ExecuteScalar());

        var populatedRate = (double)populated / total;
        Assert.True(populatedRate >= 0.95,
            $"Only {populatedRate:P1} of the LayoutCell rows have RowOrdinateId ({populated}/{total}); " +
            "check whether a new, uncharacterised cause of an open row axis has appeared.");

        // The exact invariant (0 exceptions, not a rate): every LayoutCell with a NULL RowOrdinateId
        // must belong to a sheet that has NO Y ordinate at all -- never to a sheet that does have
        // one on another row.
        using var exceptionsCmd = con.CreateCommand();
        exceptionsCmd.CommandText =
            """
            SELECT COUNT(*) FROM LayoutCell lc
            WHERE lc.RowOrdinateId IS NULL AND EXISTS (
                SELECT 1 FROM LayoutOrdinate y WHERE y.SheetId = lc.SheetId AND y.Axis = 'Y'
            )
            """;
        var exceptions = Convert.ToInt64(exceptionsCmd.ExecuteScalar());
        var nullCount = total - populated;
        Assert.Equal(0L, exceptions);
        Assert.True(nullCount > 0, "There is no LayoutCell with a NULL RowOrdinateId to measure: review the extraction.");
    }

    [DataFact]
    public void RealData_ReferentialIntegrity_EveryNonNullRowOrdinateId_PointsToARealMatchingYOrdinate()
    {
        // This is what PRAGMA foreign_keys = OFF stops checking at load time. The check is a
        // direct JOIN on OrdinateId (RowOrdinateId points to the real primary key of
        // LayoutOrdinate, so there is no need to parse CellRef and compare row numbers).
        // 0 malformed or dangling references in the real corpus.
        var con = fixture.Repository42WithDictionary;

        using var cmd = con.CreateCommand();
        cmd.CommandText =
            """
            SELECT COUNT(*) FROM LayoutCell lc
            WHERE lc.RowOrdinateId IS NOT NULL AND NOT EXISTS (
                SELECT 1 FROM LayoutOrdinate y
                WHERE y.OrdinateId = lc.RowOrdinateId AND y.Axis = 'Y' AND y.SheetId = lc.SheetId
            )
            """;
        var malformed = Convert.ToInt64(cmd.ExecuteScalar());
        Assert.Equal(0L, malformed);
    }

    [DataFact]
    public void TheAdversarialConstruction_ThatCouldBreakTheInvariant_DoesNot_DesignVerified()
    {
        // Fabricates a datapoint in the HEADER (row 2, outside any Y row, in a sheet that DOES
        // have a Y ordinate in row 10) -- the construction that would show whether the invariant
        // is a guarantee of the code. Under the cartesian-product model, the mechanism that could
        // produce the counterexample is structurally closed: PopulateCells only generates cells at
        // (row of a Y, column of an X), and the header cell (row 2) is not the row of any Y
        // ordinate -- so it simply DOES NOT COME TO EXIST in LayoutCell, instead of existing with
        // a NULL RowOrdinateId.
        var tempDir = Path.Combine(Path.GetTempPath(), $"EbaDpm.RowOrdCounterExample_{Environment.ProcessId}_{Guid.NewGuid():N}");
        try
        {
            var inputDir = Path.Combine(tempDir, "input");
            new MinimalXlsxBuilder("T_COUNTEREXAMPLE")
                .WithCell("D2", "5551_x000D_text") // datapoint in the HEADER, outside any Y row
                .WithCell("A3", "Rows")
                .WithCell("B10", "Some row label")
                .WithCell("C10", "0010")
                .WithCell("D10", "199116_x000D_text")
                .Save(inputDir);

            var outputPath = Path.Combine(tempDir, "out.db");
            LayoutExtractor.Extract(inputDir, outputPath, overwrite: false, RepoPaths.ReferenceDatabasePath);

            using var connection = Open(outputPath);

            // The sheet DOES have a Y ordinate.
            using var cmdY = connection.CreateCommand();
            cmdY.CommandText = "SELECT COUNT(*) FROM LayoutOrdinate WHERE Axis = 'Y'";
            Assert.Equal(1L, Convert.ToInt64(cmdY.ExecuteScalar()));

            // The adversarial cell (D2) does NOT come to exist in LayoutCell: PopulateCells discards
            // it when regenerating the full cartesian product.
            using var cmdD2 = connection.CreateCommand();
            cmdD2.CommandText = "SELECT COUNT(*) FROM LayoutCell WHERE CellRef = 'D2'";
            Assert.Equal(0L, Convert.ToInt64(cmdD2.ExecuteScalar()));

            // And, coherently, the invariant has no exception in this fabricated sheet: 0
            // LayoutCell rows with a NULL RowOrdinateId even though the sheet has a Y.
            using var cmdInvariant = connection.CreateCommand();
            cmdInvariant.CommandText =
                """
                SELECT COUNT(*) FROM LayoutCell lc
                WHERE lc.RowOrdinateId IS NULL AND EXISTS (
                    SELECT 1 FROM LayoutOrdinate y WHERE y.SheetId = lc.SheetId AND y.Axis = 'Y'
                )
                """;
            var exceptions = Convert.ToInt64(cmdInvariant.ExecuteScalar());
            Assert.Equal(0L, exceptions);

            // And the sheet has exactly 1 LayoutCell: the cartesian product of 1 Y ordinate (row
            // 10) x 1 X ordinate (column D, seeded by D10 itself) -- D2 does not contribute a
            // second column because its fallback ordinate coincides with D10's (same column D).
            using var cmdCount = connection.CreateCommand();
            cmdCount.CommandText = "SELECT COUNT(*) FROM LayoutCell";
            Assert.Equal(1L, Convert.ToInt64(cmdCount.ExecuteScalar()));
        }
        finally
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

    private static SqliteConnection Open(string path)
    {
        var connectionString = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly }.ToString();
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }
}
