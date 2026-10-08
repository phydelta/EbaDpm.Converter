using EbaDpm.Converter.Core.Layouts;
using EbaDpm.Converter.Core.Validation.PlaneC;
using EbaDpm.Converter.Tests.Layouts;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Tests.PlaneC;

/// <summary>
/// The Annotated Table Layouts of the 3.2 era write the table code with a SPACE
/// (<c>"C 01.00"</c>), while the target schema and the other eras (4.0, 4.2, 4.3) use an
/// underscore (<c>"C_01.00"</c>). Without normalizing, the intersection was 0 of 502. Two angles,
/// the two sides of the comparison (a normalizer applied on ONE side only is the defect, not the
/// fix):
/// <list type="bullet">
/// <item><b>Extraction</b> (<see cref="LayoutSheetNameParser"/>, through <c>LayoutExtractor.Extract</c>):
/// the code with a space is canonicalized AT EXTRACTION -- <c>LayoutSheet.TableCode</c> already
/// comes out with an underscore, and <c>LayoutSheet.SheetName</c> keeps the raw literal.</item>
/// <item><b>Comparison</b> (<see cref="PlaneCComparer"/>): the generated-output side
/// (<c>mTable.TableCode</c>) goes through the SAME function <see cref="LayoutTableCodeNormalizer"/>
/// (through <c>PlaneCComparer.Compare</c>) -- checked here with a synthetic <c>TableCode</c> WITH a
/// space on the generated side, matching against an already canonical <c>TableCode</c> on the
/// layout side (the form in which it arrives after the extraction above).</item>
/// </list>
/// Asymmetry trap: a normalizer that only acts when there is something to normalize and is a
/// NO-OP otherwise fabricates false findings if the no-op is not VERIFIED -- hence the second test
/// of each angle, with an ALREADY canonical code (no space), confirming that <c>Canonicalize</c>
/// touches nothing when there is nothing to touch.
/// </summary>
public sealed class PlaneCTableCodeNormalizationSyntheticTests
{
    // ------------------------------------------------------------------
    // EXTRACTION side (LayoutSheetNameParser)
    // ------------------------------------------------------------------

    [Fact]
    public void Extraction_SheetNameWithSpace_IsCanonicalizedToUnderscore_AndKeepsTheRawLiteralInSheetName()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"EbaDpm.TableCodeNorm_{Environment.ProcessId}_{Guid.NewGuid():N}");
        try
        {
            var inputDir = Path.Combine(tempDir, "input");
            new MinimalXlsxBuilder("C 01.00").Save(inputDir, framework: "TEST", release: "3.2", module: "T1");

            var outputPath = Path.Combine(tempDir, "out.db");
            LayoutExtractor.Extract(inputDir, outputPath, overwrite: false, dictionaryPath: null);

            using var connection = Open(outputPath);
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT TableCode, SheetName FROM LayoutSheet";
            using var reader = cmd.ExecuteReader();

            Assert.True(reader.Read(), "No LayoutSheet was extracted.");
            Assert.Equal("C_01.00", reader.GetString(0));
            Assert.Equal("C 01.00", reader.GetString(1)); // the raw literal is NOT lost.
        }
        finally
        {
            Cleanup(tempDir);
        }
    }

    /// <summary>No-regression: without a space, the canonicalization is a NO-OP -- the code comes
    /// out IDENTICAL, not "similar" nor truncated.</summary>
    [Fact]
    public void Extraction_AlreadyCanonicalSheetName_DoesNotChange_VerifiedNoOp()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"EbaDpm.TableCodeNormNoOp_{Environment.ProcessId}_{Guid.NewGuid():N}");
        try
        {
            var inputDir = Path.Combine(tempDir, "input");
            new MinimalXlsxBuilder("C_02.00").Save(inputDir, framework: "TEST", release: "4.2", module: "T1");

            var outputPath = Path.Combine(tempDir, "out.db");
            LayoutExtractor.Extract(inputDir, outputPath, overwrite: false, dictionaryPath: null);

            using var connection = Open(outputPath);
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT TableCode, SheetName FROM LayoutSheet";
            using var reader = cmd.ExecuteReader();

            Assert.True(reader.Read(), "No LayoutSheet was extracted.");
            Assert.Equal("C_02.00", reader.GetString(0));
            Assert.Equal("C_02.00", reader.GetString(1));
        }
        finally
        {
            Cleanup(tempDir);
        }
    }

    // ------------------------------------------------------------------
    // COMPARISON side (PlaneCComparer -- the other side, so that the normalization is symmetric
    // and not a normalizer applied on one side only)
    // ------------------------------------------------------------------

    /// <summary>
    /// The GENERATED TableCode ("C 01.00", with a space -- synthetic; 0 real cases like this were
    /// measured in DPM 1.0/2.0, but the code has to match if there ever were any) and the LAYOUT
    /// TableCode already canonical ("C_01.00", the form in which it comes out of the extraction
    /// above) MUST match -- it proves that <c>PlaneCComparer</c> also normalizes the generated
    /// side, and does not just trust that the extraction already did it on the other side.
    /// </summary>
    [Fact]
    public void Comparison_GeneratedTableCodeWithSpace_MatchesAnAlreadyCanonicalLayoutTableCode()
    {
        using var generated = PlaneCSyntheticDatabase.OpenGenerated();
        using var layouts = PlaneCSyntheticDatabase.OpenLayouts();

        generated.InsertTableWithTaxonomy(1, "C 01.00", taxonomyId: 100, frameworkCode: "COREP", release: "4.2");
        generated.InsertCellDps(1, "MET(qAA)|DIM(m1)");

        BuildMatchingLayoutSheet(layouts, sheetId: 1, tableCode: "C_01.00", frameworkCode: "COREP", release: "4.2", metMember: "qAA", dimMember: "m1");

        var result = PlaneCComparer.Compare(generated, layouts);

        Assert.Equal(1, result.Coverage.TablesCompared);
        Assert.Equal(1, result.Coverage.SignaturesCompared);
        Assert.Equal(1, result.Coverage.SignaturesContained);
        Assert.Empty(result.Violations);
        Assert.Equal(0, result.Coverage.LayoutTableCodesWithoutGeneratedTable);
    }

    /// <summary>No-regression: without a space on EITHER side, it still matches -- the same control
    /// already covered by <c>PlaneCComparerTests.Compare_IdenticalSignatures_...</c>, repeated here
    /// to leave the TWO halves of this invariant (with space / without space) in the same file.</summary>
    [Fact]
    public void Comparison_GeneratedTableCodeWithoutSpace_StillMatches_VerifiedNoOp()
    {
        using var generated = PlaneCSyntheticDatabase.OpenGenerated();
        using var layouts = PlaneCSyntheticDatabase.OpenLayouts();

        generated.InsertTableWithTaxonomy(1, "C_02.00", taxonomyId: 100, frameworkCode: "COREP", release: "4.2");
        generated.InsertCellDps(1, "MET(qAA)|DIM(m1)");

        BuildMatchingLayoutSheet(layouts, sheetId: 1, tableCode: "C_02.00", frameworkCode: "COREP", release: "4.2", metMember: "qAA", dimMember: "m1");

        var result = PlaneCComparer.Compare(generated, layouts);

        Assert.Equal(1, result.Coverage.TablesCompared);
        Assert.Equal(1, result.Coverage.SignaturesCompared);
        Assert.Equal(1, result.Coverage.SignaturesContained);
        Assert.Empty(result.Violations);
    }

    /// <summary>Minimal copy of <c>PlaneCComparerTests.BuildMatchingLayoutSheet</c> (private there,
    /// not accessible from here) -- a sheet with MET(metMember)|DIM(dimMember), a single column
    /// ordinate.</summary>
    private static void BuildMatchingLayoutSheet(
        SqliteConnection layouts, long sheetId, string tableCode, string frameworkCode, string release,
        string metMember, string? dimMember)
    {
        layouts.Exec($"""INSERT INTO "LayoutFile" ("FileId","FrameworkCode","ReleaseLabel") VALUES ({sheetId}, '{frameworkCode}', '{release}')""");
        layouts.Exec($"""INSERT INTO "LayoutSheet" ("SheetId","FileId","TableCode") VALUES ({sheetId}, {sheetId}, '{tableCode}')""");
        layouts.Exec(
            $"""INSERT INTO "LayoutDeclaration" ("DeclId","SheetId","DimensionCode","DomainCode","IsKey") VALUES ({sheetId * 10 + 1}, {sheetId}, NULL, 'MET', 0)""");
        layouts.Exec(
            $"""INSERT INTO "LayoutValue" ("ValueId","SheetId","DeclId","OrdinateId","MemberCode") VALUES ({sheetId * 10 + 1}, {sheetId}, {sheetId * 10 + 1}, {sheetId * 100 + 10}, '{metMember}')""");

        if (dimMember is not null)
        {
            layouts.Exec(
                $"""INSERT INTO "LayoutDeclaration" ("DeclId","SheetId","DimensionCode","DomainCode","IsKey") VALUES ({sheetId * 10 + 2}, {sheetId}, 'DIM', NULL, 0)""");
            layouts.Exec(
                $"""INSERT INTO "LayoutValue" ("ValueId","SheetId","DeclId","OrdinateId","MemberCode") VALUES ({sheetId * 10 + 2}, {sheetId}, {sheetId * 10 + 2}, {sheetId * 100 + 10}, '{dimMember}')""");
        }

        layouts.Exec(
            $"""INSERT INTO "LayoutCell" ("CellId","SheetId","RowOrdinateId","ColumnOrdinateId","IsShaded") VALUES ({sheetId}, {sheetId}, NULL, {sheetId * 100 + 10}, 0)""");
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
