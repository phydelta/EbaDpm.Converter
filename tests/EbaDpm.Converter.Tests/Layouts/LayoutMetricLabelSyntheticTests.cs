using EbaDpm.Converter.Core.Layouts;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Tests.Layouts;

/// <summary>
/// The label of the metric column (same cell, E4 in a real layout) changed name between eras --
/// <c>"Metric"</c> in 3.2, <c>"Main Property"</c> since 4.0 -- and accepting only the new literal
/// left a 3.2 layout WITHOUT a single metric and WITHOUT AN ERROR: it failed SILENTLY. This file
/// pins, SYNTHETICALLY over the grammar (without touching the data directory), that the TWO labels
/// resolve the same way.
///
/// <c>LayoutSheetParser.IsMainProperty</c> is <c>internal</c> without <c>InternalsVisibleTo</c>
/// (same criterion as the rest of the suite): it is exercised indirectly through
/// <c>LayoutExtractor.Extract</c>, checking the effect observable by SQL -- a single-code value
/// paired with the "Main Property"/"Metric" declaration resolves as
/// <c>LayoutValue.DomainCode = 'MET'</c> (without a dictionary: conservative behaviour).
///
/// Minimal sheet structure: a header row with the label in D1, its paired value in D2 (same
/// column, before the "Rows" marker -- resolved in the scan WITHIN the header), and the "Rows"
/// marker in A3 to fix <c>markerRow</c>.
/// </summary>
public sealed class LayoutMetricLabelSyntheticTests
{
    [Theory]
    [InlineData("Metric")] // The label of the 3.2 era.
    [InlineData("Main Property")] // The label since 4.0 -- control that the fix did not break the one already in force.
    public void ASingleCodeValue_PairedWithTheMetricLabel_ResolvesAsMET(string metricLabel)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"EbaDpm.MetricLabel_{Environment.ProcessId}_{Guid.NewGuid():N}");
        try
        {
            var inputDir = Path.Combine(tempDir, "input");
            new MinimalXlsxBuilder("T_METRIC_LABEL")
                .WithCell("D1", metricLabel)
                .WithCell("D2", "(qBV0)")
                .WithCell("A3", "Rows")
                .Save(inputDir);

            var outputPath = Path.Combine(tempDir, "out.db");
            LayoutExtractor.Extract(inputDir, outputPath, overwrite: false, dictionaryPath: null);

            using var connection = Open(outputPath);
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT DomainCode, MemberCode, CellRef FROM LayoutValue";
            using var reader = cmd.ExecuteReader();

            Assert.True(reader.Read(), $"No LayoutValue with the label '{metricLabel}': the metric was not resolved.");
            Assert.Equal("MET", reader.GetString(0));
            Assert.Equal("qBV0", reader.GetString(1));
            Assert.Equal("D2", reader.GetString(2));
            Assert.False(reader.Read(), "More than one LayoutValue: the fixture is not as minimal as intended.");
        }
        finally
        {
            Cleanup(tempDir);
        }
    }

    /// <summary>
    /// NEGATIVE control: a SIMILAR but different label ("Metrics", with an "s") must NOT trigger
    /// the recognition of "Main Property" -- if it did, <c>IsMainProperty</c> would be a loose
    /// comparison (substring/contains) instead of the exact equality that is required, and the same
    /// mechanism would accept any prose that starts with "Metric". Without this, the test above does
    /// not show that the label is recognised by EQUALITY, only that "Metric" works.
    /// </summary>
    [Fact]
    public void ASimilarButDifferentLabel_DoesNotTriggerMetricRecognition()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"EbaDpm.MetricLabelNegative_{Environment.ProcessId}_{Guid.NewGuid():N}");
        try
        {
            var inputDir = Path.Combine(tempDir, "input");
            new MinimalXlsxBuilder("T_METRIC_LABEL_NEG")
                .WithCell("D1", "Metrics") // It is NEITHER "Metric" NOR "Main Property".
                .WithCell("D2", "(qBV0)")
                .WithCell("A3", "Rows")
                .Save(inputDir);

            var outputPath = Path.Combine(tempDir, "out.db");
            LayoutExtractor.Extract(inputDir, outputPath, overwrite: false, dictionaryPath: null);

            using var connection = Open(outputPath);
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM LayoutValue WHERE DomainCode = 'MET' AND MemberCode = 'qBV0'";
            Assert.Equal(0L, Convert.ToInt64(cmd.ExecuteScalar()));
        }
        finally
        {
            Cleanup(tempDir);
        }
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
