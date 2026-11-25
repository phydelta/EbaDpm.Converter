using System.IO.Compression;
using System.Text;
using EbaDpm.Converter.Core.Layouts;
using EbaDpm.Converter.Core.Layouts.Xlsx;
using EbaDpm.Converter.Core.Sqlite;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Distribution.Tests.Layouts;

/// <summary>
/// The fourth Annotated Table Layout file-name format, EBA 4.4 (#67): <c>{date} {MODULE}_table layout {REL}</c>,
/// with NO framework in the name. The framework is resolved from the module code through the SQLite
/// dictionary (<c>mModule</c> -&gt; <c>mTaxonomy</c> -&gt; <c>mReportingFramework</c>, case-insensitive) and
/// extraction fails clearly, never guessing, when there is no dictionary or the module is not in it. Also
/// the <c>Index</c> sheet (skipped like <c>TOC</c>). Synthetic: no data directory.
/// </summary>
[Trait("Tier", "Synthetic")]
public sealed class LayoutModuleNameFormatSyntheticTests : IDisposable
{
    private readonly string _dir;
    private int _counter;

    public LayoutModuleNameFormatSyntheticTests()
    {
        _dir = Path.Join(Path.GetTempPath(), $"EbaDpm.LayoutModuleName_{Environment.ProcessId}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort.
        }
    }

    /// <summary>A real-schema SQLite dictionary whose only content is (module code, framework code) pairs.</summary>
    private string Dictionary(params (string Module, string Framework)[] pairs)
    {
        var path = Path.Join(_dir, $"dict{_counter++}.db");
        SchemaCreator.Create(path, overwrite: true);
        SqliteConnection.ClearAllPools(); // SchemaCreator leaves a pooled handle that would lock the file for the extractor
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        c.Open();
        Exec(c, "PRAGMA foreign_keys = OFF");
        var id = 0;
        foreach (var (module, framework) in pairs)
        {
            id++;
            Exec(c, $"INSERT INTO mReportingFramework (FrameworkID, FrameworkCode) VALUES ({id}, '{framework}')");
            Exec(c, $"INSERT INTO mTaxonomy (TaxonomyID, TaxonomyCode, FrameworkID) VALUES ({id}, '{framework.ToLowerInvariant()} 4.4', {id})");
            Exec(c, $"INSERT INTO mModule (ModuleID, TaxonomyID, ModuleCode) VALUES ({id}, {id}, '{module}')");
        }

        c.Close();
        SqliteConnection.ClearAllPools();
        return path;
    }

    private static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static readonly (string Module, string Framework)[] Real44Pairs =
    [
        ("AML_ELIGIBILITY", "AMLA"), ("AML_RA", "AMLA"), ("CODIS", "PILLAR3"), ("MREL_DECISIONS", "MREL"),
        ("TCB_LIQUIDITY", "TCB"), ("RESOL1", "RES"),
    ];

    // ---- LayoutFileNameParser: the fourth format ----

    [Theory]
    [InlineData("20261007 AML_ELIGIBILITY_table layout 4.4", "AMLA", "AML_ELIGIBILITY")] // module with '_' and framework not a prefix of it
    [InlineData("20261007 MREL_DECISIONS_table layout 4.4", "MREL", "MREL_DECISIONS")]
    [InlineData("20261007 TCB_LIQUIDITY_table layout 4.4", "TCB", "TCB_LIQUIDITY")]
    [InlineData("20261007 CODIS_table layout 4.4", "PILLAR3", "CODIS")] // module with no '_' at all
    [InlineData("20261007 RESOL1_table layout 4.4", "RES", "RESOL1")]
    public void FourthFormat_ResolvesTheFrameworkFromTheModuleThroughTheDictionary(string name, string framework, string module)
    {
        using var dictionary = DistributionLayoutDictionaryLoader.Load(Dictionary(Real44Pairs));

        var info = LayoutFileNameParser.Parse(name, dictionary);

        Assert.Equal(framework, info.FrameworkCode);
        Assert.Equal("4.4", info.ReleaseLabel);
        Assert.Equal(module, info.ModuleCode);
    }

    [Fact]
    public void FourthFormat_ModuleCodeWithSeveralUnderscores_IsKeptWhole_AnchoredOnTheLiteral()
    {
        using var dictionary = DistributionLayoutDictionaryLoader.Load(Dictionary(("A_B_C_D", "FWX")));

        var info = LayoutFileNameParser.Parse("20270101 A_B_C_D_table layout 4.5", dictionary);

        Assert.Equal("A_B_C_D", info.ModuleCode);
        Assert.Equal("FWX", info.FrameworkCode);
        Assert.Equal("4.5", info.ReleaseLabel);
    }

    [Fact]
    public void FourthFormat_MatchesTheModuleCodeCaseInsensitively()
    {
        using var dictionary = DistributionLayoutDictionaryLoader.Load(Dictionary(("AML_RA", "AMLA")));

        var info = LayoutFileNameParser.Parse("20261007 aml_ra_table layout 4.4", dictionary);

        Assert.Equal("AMLA", info.FrameworkCode);
        Assert.Equal("AML_RA", info.ModuleCode, ignoreCase: true);
    }

    [Fact]
    public void FourthFormat_WithoutADictionary_FailsClearly_NamingTheModule()
    {
        // Positive control: the very same name parses with a dictionary that has the module.
        using (var withModule = DistributionLayoutDictionaryLoader.Load(Dictionary(("AML_RA", "AMLA"))))
        {
            Assert.Equal("AMLA", LayoutFileNameParser.Parse("20261007 AML_RA_table layout 4.4", withModule).FrameworkCode);
        }

        var nullDictionary = Assert.Throws<InvalidOperationException>(
            () => LayoutFileNameParser.Parse("20261007 AML_RA_table layout 4.4"));
        Assert.Contains("AML_RA", nullDictionary.Message, StringComparison.Ordinal);
        Assert.Contains("dictionary", nullDictionary.Message, StringComparison.OrdinalIgnoreCase);

        using var absent = new LayoutDictionary(null);
        var absentDictionary = Assert.Throws<InvalidOperationException>(
            () => LayoutFileNameParser.Parse("20261007 AML_RA_table layout 4.4", absent));
        Assert.Contains("AML_RA", absentDictionary.Message, StringComparison.Ordinal);
        Assert.Contains("dictionary", absentDictionary.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FourthFormat_ModuleMissingFromTheDictionary_FailsNamingTheFileTheModuleAndTheDictionary_NeverGuesses()
    {
        var dictionaryPath = Dictionary(("AML_RA", "AMLA"));
        using var dictionary = DistributionLayoutDictionaryLoader.Load(dictionaryPath);

        var error = Assert.Throws<InvalidOperationException>(
            () => LayoutFileNameParser.Parse("20261007 UNKNOWN_MOD_table layout 4.4", dictionary));

        Assert.Contains("20261007 UNKNOWN_MOD_table layout 4.4", error.Message, StringComparison.Ordinal);
        Assert.Contains("UNKNOWN_MOD", error.Message, StringComparison.Ordinal);
        Assert.Contains(dictionaryPath, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FourthFormat_ModuleMappedToTwoFrameworks_IsAnError_NotAGuess()
    {
        using var dictionary = DistributionLayoutDictionaryLoader.Load(Dictionary(("DUP", "FWA"), ("DUP", "FWB")));

        var error = Assert.Throws<InvalidOperationException>(
            () => LayoutFileNameParser.Parse("20261007 DUP_table layout 4.4", dictionary));

        Assert.Contains("FWA", error.Message, StringComparison.Ordinal);
        Assert.Contains("FWB", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("20261007 AML_RA_table layout")] // no release
    [InlineData("20261007 table layout 4.4")] // no module
    [InlineData("AML_RA_table layout 4.4")] // no date
    public void FourthFormat_MalformedNames_AreRejected_ListingTheKnownPatterns(string name)
    {
        using var dictionary = DistributionLayoutDictionaryLoader.Load(Dictionary(Real44Pairs));

        var error = Assert.Throws<InvalidOperationException>(() => LayoutFileNameParser.Parse(name, dictionary));

        Assert.Contains("table layout", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Annotated Table Layout 320-P1-AE 3.2", "AE", "3.2", null)]
    [InlineData("20241217 Annotated Table Layout  COREP_LRCOREP 4.0", "COREP", "4.0", "COREP_LR")]
    [InlineData("20260106 Annotated Table Layout  AE 4.2 AEAE 4.2", "AE", "4.2", "AE")]
    [InlineData("20260623 Annotated Table Layout  TCB 4.3 TCB_LIQUIDITYTCB 4.3", "TCB", "4.3", "TCB_LIQUIDITY")]
    public void TheThreeEarlierFormats_AreUnchanged_WithAndWithoutADictionary(string name, string framework, string release, string? module)
    {
        var without = LayoutFileNameParser.Parse(name);
        using var dictionary = DistributionLayoutDictionaryLoader.Load(Dictionary(Real44Pairs));
        var with = LayoutFileNameParser.Parse(name, dictionary);

        Assert.Equal((framework, release, module), (without.FrameworkCode, without.ReleaseLabel, without.ModuleCode));
        Assert.Equal(without, with);
    }

    // ---- LayoutExtractor: file-name format + Index sheet, end to end over a synthetic workbook ----

    private string Workbook(string fileName, params (string Sheet, string[] Cells)[] sheets)
    {
        var directory = Path.Join(_dir, $"in{_counter++}");
        new SimpleXlsx().Save(directory, fileName, sheets.Select(s => new SimpleXlsx.Sheet(s.Sheet, s.Cells.Select((c, i) => (c, $"{c}")).ToList())).ToList(), []);
        return directory;
    }

    private static readonly (string Sheet, string[] Cells)[] IndexAndOneTable =
    [
        ("Index", ["A1"]),
        ("V_00.01", ["A1"]),
    ];

    private (LayoutExtractionSummary Summary, List<(string Sheet, string TableCode)> Sheets, List<(string File, string? Fw, string? Module, string? Release)> Files) ExtractFromDirectory(
        string directory, string? dictionaryPath)
    {
        var output = Path.Join(_dir, $"layouts{_counter++}.db");
        var summary = LayoutExtractor.Extract(directory, output, overwrite: false, () => DistributionLayoutDictionaryLoader.Load(dictionaryPath));
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = output, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        c.Open();
        var sheets = new List<(string, string)>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT SheetName, TableCode FROM LayoutSheet ORDER BY SheetId";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                sheets.Add((r.GetString(0), r.GetString(1)));
            }
        }

        var files = new List<(string, string?, string?, string?)>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT Path, FrameworkCode, ModuleCode, ReleaseLabel FROM LayoutFile ORDER BY FileId";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                files.Add((Path.GetFileName(r.GetString(0)), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3)));
            }
        }

        return (summary, sheets, files);
    }

    [Fact]
    public void Extract_FourthFormatFile_AttributesTheSheetToItsModuleFrameworkAndRelease_AndSkipsTheIndexSheet()
    {
        var dictionaryPath = Dictionary(("AML_ELIGIBILITY", "AMLA"));
        var directory = Workbook("20261007 AML_ELIGIBILITY_table layout 4.4.xlsx", IndexAndOneTable);

        var (summary, sheets, files) = ExtractFromDirectory(directory, dictionaryPath);

        Assert.Equal(1, summary.FilesProcessed);
        Assert.Equal(1, summary.SheetsProcessed);
        var table = Assert.Single(sheets);
        Assert.Equal(("V_00.01", "V_00.01"), table);
        var file = Assert.Single(files);
        Assert.Equal(("20261007 AML_ELIGIBILITY_table layout 4.4.xlsx", "AMLA", "AML_ELIGIBILITY", "4.4"), file);
    }

    [Fact]
    public void Extract_IndexIsSkippedLikeToc_AndOnlyThoseTwoNames()
    {
        var dictionaryPath = Dictionary(("AML_ELIGIBILITY", "AMLA"));
        var skipped = Workbook(
            "20261007 AML_ELIGIBILITY_table layout 4.4.xlsx",
            ("TOC", ["A1"]), ("Index", ["A1"]), ("V_00.01", ["A1"]));
        Assert.Equal(1, ExtractFromDirectory(skipped, dictionaryPath).Summary.SheetsProcessed);

        // Positive control: a sheet that is NOT named Index/TOC is processed as a table, so the zero
        // above is the skip and not a workbook the extractor could not read.
        var notSkipped = Workbook(
            "20261007 AML_ELIGIBILITY_table layout 4.4.xlsx",
            ("Indices", ["A1"]), ("V_00.01", ["A1"]));
        var (summary, sheets, _) = ExtractFromDirectory(notSkipped, dictionaryPath);
        Assert.Equal(2, summary.SheetsProcessed);
        Assert.Contains(sheets, s => s.Sheet == "Indices");
    }

    [Fact]
    public void Extract_FourthFormatFile_WithoutADictionary_Fails_AndNamesTheModule()
    {
        var directory = Workbook("20261007 AML_ELIGIBILITY_table layout 4.4.xlsx", IndexAndOneTable);

        var error = Assert.Throws<InvalidOperationException>(
            () => LayoutExtractor.Extract(directory, Path.Join(_dir, "nodict.db"), overwrite: false, () => new LayoutDictionary(null)));

        Assert.Contains("AML_ELIGIBILITY", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_FourthFormatFile_WithAModuleMissingFromTheDictionary_Fails_NamingTheFileModuleAndDictionary()
    {
        var dictionaryPath = Dictionary(("AML_RA", "AMLA"));
        var directory = Workbook("20261007 AML_ELIGIBILITY_table layout 4.4.xlsx", IndexAndOneTable);

        var error = Assert.Throws<InvalidOperationException>(
            () => LayoutExtractor.Extract(directory, Path.Join(_dir, "missing.db"), overwrite: false, () => DistributionLayoutDictionaryLoader.Load(dictionaryPath)));

        Assert.Contains("20261007 AML_ELIGIBILITY_table layout 4.4", error.Message, StringComparison.Ordinal);
        Assert.Contains("AML_ELIGIBILITY", error.Message, StringComparison.Ordinal);
        Assert.Contains(dictionaryPath, error.Message, StringComparison.Ordinal);
    }

    // ---- XlsxReader: shading is the colour without its alpha byte ----

    private IReadOnlyList<(string Cell, bool Shaded)> ShadedBy(string fgColorAttributes, string patternType = "solid")
    {
        var directory = Path.Join(_dir, $"shade{_counter++}");
        new SimpleXlsx().Save(
            directory,
            "shade.xlsx",
            [new SimpleXlsx.Sheet("S", [("A1", "plain"), ("B1", "coloured")])],
            fills: [(patternType, fgColorAttributes)],
            styledCells: ["B1"]);
        var sheet = Assert.Single(XlsxReader.Read(Path.Join(directory, "shade.xlsx")).Sheets);
        return [("A1", sheet.IsCellShaded(1, 1)), ("B1", sheet.IsCellShaded(1, 2))];
    }

    [Theory]
    [InlineData("indexed=\"55\"")] // legacy palette, up to 4.3
    [InlineData("rgb=\"FFD8D8D8\"")] // up to 4.3
    [InlineData("rgb=\"00D8D8D8\"")] // 4.4: alpha 00
    [InlineData("rgb=\"80D8D8D8\"")] // any alpha
    [InlineData("rgb=\"00d8d8d8\"")] // lower case
    [InlineData("rgb=\"00999999\"")] // 4.4: the colour indexed=55 resolves to
    [InlineData("rgb=\"FF999999\"")]
    [InlineData("rgb=\"00595959\"")] // 4.4: the dark grey of K_41.00 r0610 c0080 (1 cell, Access-excluded)
    [InlineData("rgb=\"FF595959\"")]
    public void TheGreyFills_AreShaded_WhateverTheAlphaByte(string fgColor)
    {
        var cells = ShadedBy(fgColor);

        Assert.Equal(("B1", true), cells[1]);
        Assert.Equal(("A1", false), cells[0]); // the unstyled neighbour is not: the flag is per cell
    }

    [Theory]
    [InlineData("rgb=\"00FFFF00\"")] // yellow
    [InlineData("rgb=\"FFFFFF00\"")]
    [InlineData("rgb=\"00C4D79B\"")] // green
    [InlineData("rgb=\"FFC4D79B\"")]
    [InlineData("rgb=\"FFFFFFFF\"")]
    [InlineData("indexed=\"64\"")]
    public void TheOtherFills_AreNotShaded(string fgColor)
    {
        Assert.Equal(("B1", false), ShadedBy(fgColor)[1]);
    }

    [Fact]
    public void AGreyColourWithoutASolidPattern_IsNotShaded()
    {
        Assert.Equal(("B1", false), ShadedBy("rgb=\"00D8D8D8\"", patternType: "none")[1]);
        Assert.Equal(("B1", true), ShadedBy("rgb=\"00D8D8D8\"", patternType: "solid")[1]); // positive control: same colour, solid
    }

    /// <summary>A minimal, literal-name <c>.xlsx</c> with several sheets and explicit fills (the shared builders fix both).</summary>
    private sealed class SimpleXlsx
    {
        public sealed record Sheet(string Name, IReadOnlyList<(string Ref, string Text)> Cells);

        public void Save(
            string directory,
            string fileName,
            IReadOnlyList<Sheet> sheets,
            IReadOnlyList<(string PatternType, string FgColorAttributes)> fills,
            IReadOnlyList<string>? styledCells = null)
        {
            Directory.CreateDirectory(directory);
            using var stream = new FileStream(Path.Join(directory, fileName), FileMode.Create, FileAccess.Write);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Create);

            var workbook = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>");
            var rels = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
            for (var i = 0; i < sheets.Count; i++)
            {
                workbook.Append($"<sheet name=\"{sheets[i].Name}\" sheetId=\"{i + 1}\" r:id=\"rId{i + 1}\"/>");
                rels.Append($"<Relationship Id=\"rId{i + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{i + 1}.xml\"/>");
                Write(zip, $"xl/worksheets/sheet{i + 1}.xml", SheetXml(sheets[i], styledCells ?? []));
            }

            workbook.Append("</sheets></workbook>");
            rels.Append("</Relationships>");
            Write(zip, "xl/workbook.xml", workbook.ToString());
            Write(zip, "xl/_rels/workbook.xml.rels", rels.ToString());

            if (fills.Count > 0)
            {
                // fills[0] = no fill (the default style); fills[1..] are the ones under test, cellXfs[k] uses fills[k].
                var styles = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
                styles.Append($"<fills count=\"{fills.Count + 1}\"><fill><patternFill patternType=\"none\"/></fill>");
                foreach (var (pattern, fg) in fills)
                {
                    styles.Append($"<fill><patternFill patternType=\"{pattern}\"><fgColor {fg}/><bgColor indexed=\"64\"/></patternFill></fill>");
                }

                styles.Append($"</fills><cellXfs count=\"{fills.Count + 1}\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/>");
                for (var k = 1; k <= fills.Count; k++)
                {
                    styles.Append($"<xf numFmtId=\"0\" fontId=\"0\" fillId=\"{k}\" borderId=\"0\" applyFill=\"1\"/>");
                }

                styles.Append("</cellXfs></styleSheet>");
                Write(zip, "xl/styles.xml", styles.ToString());
            }
        }

        private static string SheetXml(Sheet sheet, IReadOnlyList<string> styledCells)
        {
            var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData><row r=\"1\">");
            foreach (var (cellRef, text) in sheet.Cells)
            {
                var style = styledCells.Contains(cellRef) ? " s=\"1\"" : string.Empty;
                sb.Append($"<c r=\"{cellRef}\"{style} t=\"inlineStr\"><is><t>{text}</t></is></c>");
            }

            return sb.Append("</row></sheetData></worksheet>").ToString();
        }

        private static void Write(ZipArchive zip, string entryName, string content)
        {
            var entry = zip.CreateEntry(entryName);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            writer.Write(content);
        }
    }
}
