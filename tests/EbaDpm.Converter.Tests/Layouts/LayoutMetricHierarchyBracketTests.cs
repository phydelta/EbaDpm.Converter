using EbaDpm.Converter.Core.Layouts;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Tests.Layouts;

/// <summary>
/// The trailing bracket <c>[domain:hierarchy]</c> of a metric annotation
/// (<c>(qAYV) Type of entity code[qCO:new_CO5]</c>) is stored in
/// <c>LayoutValue.HierarchyDomainCode</c>/<c>HierarchyCode</c> for the Main Property (metric) value
/// and for no other value (issue 61, plane C <c>C-MHR-01</c>). Synthetic sheets through
/// <see cref="LayoutExtractor.Extract"/>, without a dictionary (the conservative behaviour treats a
/// single-code value paired with the Main Property declaration as a metric), same technique as
/// <see cref="LayoutMetricLabelSyntheticTests"/>.
/// </summary>
public sealed class LayoutMetricHierarchyBracketTests
{
    private sealed record Row(string? DomainCode, string? MemberCode, string? Label, string? HierarchyDomainCode, string? HierarchyCode);

    private static List<Row> Extract(Func<MinimalXlsxBuilder, MinimalXlsxBuilder> sheet)
    {
        var tempDir = Path.Join(Path.GetTempPath(), $"EbaDpm.MetricBracket_{Environment.ProcessId}_{Guid.NewGuid():N}");
        try
        {
            var inputDir = Path.Join(tempDir, "input");
            sheet(new MinimalXlsxBuilder("T_METRIC_BRACKET")).Save(inputDir);

            var outputPath = Path.Join(tempDir, "out.db");
            LayoutExtractor.Extract(inputDir, outputPath, overwrite: false, dictionaryPath: null);

            using var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder { DataSource = outputPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT DomainCode, MemberCode, Label, HierarchyDomainCode, HierarchyCode FROM LayoutValue ORDER BY ValueId";
            using var reader = cmd.ExecuteReader();
            var rows = new List<Row>();
            while (reader.Read())
            {
                rows.Add(new Row(
                    reader.IsDBNull(0) ? null : reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4)));
            }

            return rows;
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

    [Theory]
    [InlineData("Main Property")]
    [InlineData("Metric")]
    public void MetricValue_WithBracket_StoresDomainAndHierarchy_AndTheLabelWithoutTheBracket(string metricLabel)
    {
        var rows = Extract(b => b
            .WithCell("D1", metricLabel)
            .WithCell("D2", "(qAYV) Type of entity code (Concentration funding)[qCO:new_CO5]")
            .WithCell("A3", "Rows"));

        var row = Assert.Single(rows);
        Assert.Equal(new Row("MET", "qAYV", "Type of entity code (Concentration funding)", "qCO", "new_CO5"), row);
    }

    [Fact]
    public void MetricValue_WithoutBracket_StoresNullHierarchy()
    {
        var rows = Extract(b => b
            .WithCell("D1", "Main Property")
            .WithCell("D2", "(qBV0) Some metric")
            .WithCell("A3", "Rows"));

        var row = Assert.Single(rows);
        Assert.Equal(new Row("MET", "qBV0", "Some metric", null, null), row);
    }

    /// <summary>
    /// Negative control: the bracket on a value that is NOT a Main Property metric (a two-code
    /// dimension member under a dimension declaration) is parsed by the same grammar but must not
    /// end up in the hierarchy columns, otherwise C-MHR-01 would compare dimension members.
    /// </summary>
    [Fact]
    public void NonMetricValue_WithBracket_DoesNotStoreTheHierarchy()
    {
        var rows = Extract(b => b
            .WithCell("D1", "(ZZZ:QCO) Some dimension")
            .WithCell("D2", "(ZZZ:qx2000) Some member[QCO:new_CO5]")
            .WithCell("A3", "Rows"));

        // The fixture must really produce a non-metric value, or the assertion below proves nothing.
        var row = Assert.Single(rows);
        Assert.Equal("ZZZ", row.DomainCode); // a two-code value: (dimension:member), not a metric
        Assert.Equal("qx2000", row.MemberCode);
        Assert.Null(row.HierarchyDomainCode);
        Assert.Null(row.HierarchyCode);
    }
}
