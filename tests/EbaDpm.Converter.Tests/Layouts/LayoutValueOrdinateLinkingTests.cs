using EbaDpm.Converter.Core.Layouts;
using EbaDpm.Converter.Tests.Schema;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Tests.Layouts;

/// <summary>
/// <c>LayoutValue.OrdinateId</c> is populated by the extractor. Covers both halves of the rule:
/// that the link HAPPENS (a rate, with its denominator) and that whatever does NOT link is
/// RECORDED AND COUNTED, never discarded (<c>LayoutUnparsed.Kind = 'UnlinkedValue'</c>, with the
/// reason for the failure).
/// </summary>
[Collection("LayoutsRealData")]
public sealed class LayoutValueOrdinateLinkingTests(LayoutRepositoryFixture fixture)
{
    [DataFact]
    public void RealData_MostLayoutValues_LinkToAnOrdinate_WithExplicitRateAndDenominator()
    {
        // The property, not a snapshot: link RATE, with the whole universe declared. Measured
        // independently over the 4.2 corpus: 99,398 of 99,636 (99.8 %).
        var con = fixture.Repository42WithDictionary;

        var total = Scalar(con, "SELECT COUNT(*) FROM LayoutValue");
        var linked = Scalar(con, "SELECT COUNT(*) FROM LayoutValue WHERE OrdinateId IS NOT NULL");

        Assert.True(total > 50000, $"Only {total} LayoutValue rows in the 4.2 corpus: review the extraction.");

        var rate = (double)linked / total;
        Assert.True(rate >= 0.99,
            $"Only {rate:P1} of LayoutValue rows link to an ordinate ({linked}/{total}); 99.8 % is " +
            "measured over the real corpus -- a large drop here is a regression of the link, not " +
            "noise from a new release.");
    }

    [DataFact]
    public void RealData_EveryUnlinkedValue_IsRegisteredInLayoutUnparsed_NeverSilentlyDropped()
    {
        // "What does not link is recorded and counted, never discarded" -- checked as an exact
        // EQUALITY between the two sources (LayoutValue.OrdinateId IS NULL and
        // LayoutUnparsed.Kind='UnlinkedValue'), not a coincidence of a snapshotted figure: if the
        // recording gate broke (for example, if someone added a "continue" before writing to
        // LayoutUnparsed), the two counts would stop matching even if the absolute figure did not
        // change much.
        var con = fixture.Repository42WithDictionary;

        var unlinkedValues = Scalar(con, "SELECT COUNT(*) FROM LayoutValue WHERE OrdinateId IS NULL");
        var unparsedUnlinked = Scalar(con, "SELECT COUNT(*) FROM LayoutUnparsed WHERE Kind = 'UnlinkedValue'");

        Assert.True(unlinkedValues > 0, "There is no unlinked LayoutValue to measure: check whether the corpus changed.");
        Assert.Equal(unlinkedValues, unparsedUnlinked);
    }

    [DataFact]
    public void RealData_UnlinkedValues_AreAllExplainedByDesign_NotByAGapInOrdinateExtraction()
    {
        // There are two causes for "does not link", with reasons DISTINGUISHABLE by text without a
        // forensic query: Z axis by design (Region=Header, no ordinate to link) vs a real
        // extraction gap (the code exists as a value but not as an ordinate of its axis).
        // Measured over the 4.2 corpus: the whole residue (238/238) is by design -- 0 gaps.
        var con = fixture.Repository42WithDictionary;

        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT Reason, COUNT(*) FROM LayoutUnparsed WHERE Kind = 'UnlinkedValue' GROUP BY Reason";
        using var reader = cmd.ExecuteReader();
        var byReason = new Dictionary<string, long>();
        while (reader.Read())
        {
            byReason[reader.GetString(0)] = reader.GetInt64(1);
        }

        var byDesign = byReason.GetValueOrDefault(HeaderRegionReasonLiteral);
        var total = byReason.Values.Sum();
        var gaps = total - byDesign;

        Assert.True(total > 0, "There is no UnlinkedValue row to measure.");
        Assert.Equal(0L, gaps);
        Assert.Equal(total, byDesign);
    }

    [DataFact]
    public void PositiveControl_AValueInAColumnWithNoXOrdinate_FailsToLinkWithTheGapReason_NotSilently()
    {
        // Positive control: fabricates, outside the data directory, a MATRIX-role value (Region =
        // "Rows") in a column that NEVER gets an X ordinate -- neither from the code row (empty,
        // no marker in markerRow-1) nor from any datapoint in that column. The link rule (Rows ->
        // X axis by the value's column) must fail with the reason "ordinate extraction gap", not
        // with the Z-axis one and not silently.
        //
        // The matrix value lives in the SAME row as its declaration: a declaration
        // "(ZZDIM:ZZDOM)" is fabricated in B3 with its value. It is used without a dictionary so
        // that the value is NOT rejected for "does not exist in the dictionary" before it even
        // reaches LayoutValueOrdinateLinker.
        var tempDir = Path.Combine(Path.GetTempPath(), $"EbaDpm.UnlinkGate_{Environment.ProcessId}_{Guid.NewGuid():N}");
        try
        {
            var inputDir = Path.Combine(tempDir, "input");
            new MinimalXlsxBuilder("T_UNLINK_GATE")
                .WithCell("A3", "Rows")
                .WithCell("B3", "(ZZDIM:ZZDOM) fake dimension declaration")
                .WithCell("F3", "(ZZDOM:ZZMEM) fake value, column F never gets an X ordinate")
                .Save(inputDir);

            var outputPath = Path.Combine(tempDir, "out.db");
            LayoutExtractor.Extract(inputDir, outputPath, overwrite: false, dictionaryPath: null);

            using var connection = Open(outputPath);

            // Control: column F indeed has no X ordinate registered.
            Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM LayoutOrdinate WHERE Axis = 'X'"));

            // The value WAS written to LayoutValue (it is never discarded), but without OrdinateId.
            using var cmdValue = connection.CreateCommand();
            cmdValue.CommandText = "SELECT OrdinateId FROM LayoutValue WHERE CellRef = 'F3'";
            using var readerValue = cmdValue.ExecuteReader();
            Assert.True(readerValue.Read(), "The value fabricated in F3 should have been written to LayoutValue.");
            Assert.True(readerValue.IsDBNull(0), "OrdinateId should be NULL: column F has no X ordinate.");
            readerValue.Close();

            // And it was COUNTED in LayoutUnparsed, with the GAP reason -- not the Z-axis one.
            using var cmdReason = connection.CreateCommand();
            cmdReason.CommandText = "SELECT Reason FROM LayoutUnparsed WHERE Kind = 'UnlinkedValue' AND CellRef = 'F3'";
            var reason = (string?)cmdReason.ExecuteScalar();
            Assert.NotNull(reason);
            Assert.Contains("ordinate extraction gap", reason);
            Assert.NotEqual(HeaderRegionReasonLiteral, reason);
        }
        finally
        {
            Cleanup(tempDir);
        }
    }

    /// <summary>Literal copy of <c>LayoutValueOrdinateLinker.HeaderRegionReason</c> (the rest of the
    /// suite already treats the <c>Kind</c>/<c>Reason</c> literals as the contract queryable by
    /// SQL, not as assembly types). If the text changes in the production code, this test must
    /// fail and be updated here too -- which is precisely what makes this literal a useful
    /// canary.</summary>
    private const string HeaderRegionReasonLiteral =
        "Z axis (Region=Header): no ordinate to link by design — the link rule does not apply, it is not a gap";

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
