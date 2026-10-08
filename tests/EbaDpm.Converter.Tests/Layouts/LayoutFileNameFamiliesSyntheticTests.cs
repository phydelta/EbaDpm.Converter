using EbaDpm.Converter.Core.Layouts;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Tests.Layouts;

/// <summary>
/// The Annotated Table Layouts extractor used to reject the file names of the 3.2 and 4.0 eras
/// (only 3.2 had been registered at one point) -- a defect that <c>--extract-layouts</c> left
/// SILENTLY outside the universe until someone tried exactly those two releases. This file pins,
/// SYNTHETICALLY (without touching the data directory), that <see cref="LayoutFileNameParser"/>
/// (exercised indirectly through <c>LayoutExtractor.Extract</c>; it is <c>internal</c> without
/// <c>InternalsVisibleTo</c> -- same criterion as the release-detection and equality-rule tests)
/// resolves the THREE file-name families at once, over a directory that mixes them -- exactly what
/// <c>--extract-layouts</c> does over the data directory in production.
///
/// All three sheets are <c>"TOC"</c> -- <c>LayoutExtractor</c> explicitly skips them
/// (<c>if (sheet.Name == "TOC") continue;</c>), so this test only exercises the FILE name, not the
/// sheet content: <c>LayoutFile</c> is inserted BEFORE the sheet loop, so the three rows are
/// written anyway even though the three sheets are discarded afterwards.
/// </summary>
public sealed class LayoutFileNameFamiliesSyntheticTests
{
    [Fact]
    public void Extract_OverADirectoryWithTheThreeNameFamilies_ResolvesFrameworkReleaseAndModulePerEra()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"EbaDpm.FileNameFamilies_{Environment.ProcessId}_{Guid.NewGuid():N}");
        try
        {
            var inputDir = Path.Combine(tempDir, "input");

            // 3.2: "Annotated Table Layout {PHASE}-{FW} {REL}" -- NO date, NO module.
            new MinimalXlsxBuilder("TOC").SaveWithFileName(inputDir, "Annotated Table Layout 320-P1-AE 3.2.xlsx");

            // 4.0: "{date} Annotated Table Layout  {MODULE}{FW} {REL}" -- WITHOUT the leading
            // "{FW} {REL}" that 4.2/4.3 carry. Framework=COREP, module=COREP_LR (separated by
            // structure: the module starts with the framework followed by "_").
            new MinimalXlsxBuilder("TOC").SaveWithFileName(
                inputDir, "20241217 Annotated Table Layout  COREP_LRCOREP 4.0.xlsx");

            // 4.2: "{date} Annotated Table Layout  {FW} {REL} {MODULE}{FW} {REL}" -- repeats
            // framework+release at the beginning AND at the end.
            new MinimalXlsxBuilder("TOC").SaveWithFileName(
                inputDir, "20260106 Annotated Table Layout  AE 4.2 AEAE 4.2.xlsx");

            var outputPath = Path.Combine(tempDir, "out.db");
            var summary = LayoutExtractor.Extract(inputDir, outputPath, overwrite: false, dictionaryPath: null);

            Assert.Equal(3, summary.FilesProcessed);

            using var connection = Open(outputPath);

            var (fw32, rel32, mod32) = QueryFile(connection, "320-P1-AE 3.2");
            Assert.Equal("AE", fw32);
            Assert.Equal("3.2", rel32);
            Assert.Null(mod32); // 3.2: module granularity does not exist in that era -- it is not invented.

            var (fw40, rel40, mod40) = QueryFile(connection, "COREP_LRCOREP 4.0");
            Assert.Equal("COREP", fw40);
            Assert.Equal("4.0", rel40);
            Assert.Equal("COREP_LR", mod40);

            var (fw42, rel42, mod42) = QueryFile(connection, "AE 4.2 AEAE 4.2");
            Assert.Equal("AE", fw42);
            Assert.Equal("4.2", rel42);
            Assert.Equal("AE", mod42);
        }
        finally
        {
            Cleanup(tempDir);
        }
    }

    private static (string? Framework, string? Release, string? Module) QueryFile(SqliteConnection connection, string pathContains)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT FrameworkCode, ReleaseLabel, ModuleCode FROM LayoutFile WHERE Path LIKE $p";
        cmd.Parameters.AddWithValue("$p", $"%{pathContains}%");
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read(), $"No LayoutFile found whose Path contains '{pathContains}'.");
        var framework = reader.IsDBNull(0) ? null : reader.GetString(0);
        var release = reader.IsDBNull(1) ? null : reader.GetString(1);
        var module = reader.IsDBNull(2) ? null : reader.GetString(2);
        Assert.False(reader.Read(), $"More than one LayoutFile whose Path contains '{pathContains}': the test is not deterministic.");
        return (framework, release, module);
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
