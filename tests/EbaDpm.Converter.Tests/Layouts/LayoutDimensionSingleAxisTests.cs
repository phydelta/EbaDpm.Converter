using EbaDpm.Converter.Core.Layouts;
using EbaDpm.Converter.Tests.Schema;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Tests.Layouts;

/// <summary>
/// "One dimension, one axis": within a given table, a dimension belongs to EXACTLY one axis -- it
/// is never declared on two different axes of the same sheet. It is an invariant that can be
/// checked without comparing anything against the reference: if it fails in the layout, the Excel
/// has been misread.
///
/// The axis is revealed by <c>LayoutDeclaration.Region</c> as stored at the end of parsing:
/// <c>Rows</c> -> X axis, <c>Columns</c> -> Y axis, <c>Header</c> -> Z axis (the same rule used by
/// <see cref="LayoutValueOrdinateLinker"/> to link values to ordinates).
/// </summary>
[Collection("LayoutsRealData")]
public sealed class LayoutDimensionSingleAxisTests(LayoutRepositoryFixture fixture)
{
    [DataFact]
    public void RealData_NoSheetDeclaresTheSameDimensionOnTwoDifferentAxes_ZeroOf10147()
    {
        var con = fixture.Repository42WithDictionary;

        using var cmd = con.CreateCommand();
        cmd.CommandText =
            """
            SELECT SheetId, DimensionCode,
                   CASE Region WHEN 'Rows' THEN 'X' WHEN 'Columns' THEN 'Y' WHEN 'Header' THEN 'Z' ELSE Region END AS axis
            FROM LayoutDeclaration
            WHERE DimensionCode IS NOT NULL
            """;
        using var reader = cmd.ExecuteReader();
        var axesByPair = new Dictionary<(long SheetId, string DimensionCode), HashSet<string>>();
        while (reader.Read())
        {
            var key = (reader.GetInt64(0), reader.GetString(1));
            if (!axesByPair.TryGetValue(key, out var axes))
            {
                axes = [];
                axesByPair[key] = axes;
            }

            axes.Add(reader.GetString(2));
        }

        Assert.True(axesByPair.Count > 5000, $"Only {axesByPair.Count} (sheet, dimension) pairs: review the extraction.");

        var multiAxis = axesByPair.Where(kv => kv.Value.Count > 1).ToList();
        Assert.True(multiAxis.Count == 0,
            $"{multiAxis.Count} of {axesByPair.Count} (sheet, dimension) pairs appear on more than one axis: " +
            string.Join("; ", multiAxis.Take(5).Select(kv => $"SheetId={kv.Key.SheetId} Dim={kv.Key.DimensionCode} axes={string.Join(",", kv.Value)}")));
    }

    [DataFact]
    public void PositiveControl_TheSameDimensionDeclaredInRowsAndColumns_IsDetectedAsMultiAxis()
    {
        // Positive control: fabricates, outside the data directory, a sheet where the SAME
        // dimension ("qLHL") is declared once in the grid (Region=Rows -> X axis) and once in the
        // header-paired-in-the-grid (Region=Columns -> Y axis). The query of the property above,
        // applied ONLY to this fabricated sheet, must find 1 pair on more than one axis -- not 0:
        // if it found 0, the real property (0 of 10,147) would be indistinguishable from a query
        // that never looks at anything.
        var tempDir = Path.Combine(Path.GetTempPath(), $"EbaDpm.SingleAxisGate_{Environment.ProcessId}_{Guid.NewGuid():N}");
        try
        {
            var inputDir = Path.Combine(tempDir, "input");
            new MinimalXlsxBuilder("T_DIM_TWO_AXES")
                // HEADER declaration (row 1, before "Rows"): it does not pair within the header
                // (row 2 empty in that column), so it falls to the in-grid scan -- it ends up with
                // Region="Columns" (Y axis).
                .WithCell("D1", "(qLHL:qTR) Type of risk, declared in the header (Columns -> Y axis)")
                .WithCell("A3", "Rows")
                // MATRIX declaration (row 3 = markerRow): same dimension, Region="Rows" (X axis).
                .WithCell("B3", "(qLHL:qTR) Type of risk, declared in the grid (Rows -> X axis)")
                .WithCell("C3", "(qTR:qx2003) Market risk")
                // A real Y ordinate row (B/C shaped like a short code) -- D10 stays UNCLAIMED by
                // ParseGridRow (it does not have the shape of a datapoint) and the header scan
                // pairs it with the declaration in D1.
                .WithCell("B10", "Row label")
                .WithCell("C10", "0020")
                .WithCell("D10", "(qTR:qx2003) Market risk, same value, paired further down the grid")
                .Save(inputDir);

            var outputPath = Path.Combine(tempDir, "out.db");
            LayoutExtractor.Extract(inputDir, outputPath, overwrite: false, RepoPaths.ReferenceDatabasePath);

            using var connection = Open(outputPath);
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                """
                SELECT SheetId, DimensionCode,
                       CASE Region WHEN 'Rows' THEN 'X' WHEN 'Columns' THEN 'Y' WHEN 'Header' THEN 'Z' ELSE Region END AS axis
                FROM LayoutDeclaration
                WHERE DimensionCode IS NOT NULL
                """;
            using var reader = cmd.ExecuteReader();
            var axesByPair = new Dictionary<(long, string), HashSet<string>>();
            while (reader.Read())
            {
                var key = (reader.GetInt64(0), reader.GetString(1));
                if (!axesByPair.TryGetValue(key, out var axes))
                {
                    axes = [];
                    axesByPair[key] = axes;
                }

                axes.Add(reader.GetString(2));
            }

            var multiAxis = axesByPair.Where(kv => kv.Value.Count > 1).ToList();
            var only = Assert.Single(multiAxis);
            Assert.Equal("qLHL", only.Key.Item2);
            Assert.Equal(["X", "Y"], only.Value.OrderBy(x => x).ToArray());
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
