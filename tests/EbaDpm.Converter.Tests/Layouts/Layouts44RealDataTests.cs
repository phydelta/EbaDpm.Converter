using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using EbaDpm.Converter.Tests.Dpm2;
using EbaDpm.Converter.Tests.Infrastructure;
using EbaDpm.Converter.Tests.Schema;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace EbaDpm.Converter.Tests.Layouts;

/// <summary>
/// Extraction of the EBA Annotated Table Layouts 4.4 (Phase 1, #67) with the <c>--release 4.4</c> file as
/// <c>--dictionary</c> (<see cref="Dpm2044ValidateFixture.Layouts44DatabasePath"/>), contrasted with an
/// independent reading of the workbooks (<see cref="IndependentXlsx"/>: ZIP + XML, no production reader)
/// and with the Access database. The 4.4 file names carry no framework: it must equal the Access route
/// (<c>ModuleVersion</c> -&gt; <c>Module</c> -&gt; <c>Framework</c>) for every file. Nothing is pinned.
/// </summary>
[Collection("Dpm2044ValidateSuite")]
[Trait("Tier", "RealData")]
public sealed class Layouts44RealDataTests(Dpm2044ValidateFixture fixture, ITestOutputHelper output)
{
    private static readonly Regex FileNamePattern = new(
        @"^(?<date>\d+) (?<module>.+)_table layout (?<rel>\S+)\.xlsx$", RegexOptions.Compiled);

    private static string[] Workbooks() =>
        Directory.GetFiles(RepoPaths.TableLayouts44Directory, "*.xlsx", SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.Ordinal).ToArray();

    private SqliteConnection OpenLayouts()
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = fixture.Layouts44DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        c.Open();
        return c;
    }

    private static List<object[]> Query(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        var rows = new List<object[]>();
        while (r.Read())
        {
            var row = new object[r.FieldCount];
            r.GetValues(row);
            rows.Add(row);
        }

        return rows;
    }

    private static string Text(object o) => Convert.ToString(o, CultureInfo.InvariantCulture) ?? string.Empty;

    [DataFact]
    public void Extraction44_Succeeds_WithTheOutputOfTheRelease44AsDictionary()
    {
        RepoPaths.EnsureTableLayouts44DirectoryExists();
        var summary = fixture.Layouts44Summary;

        Assert.True(summary.DictionaryUsed);
        Assert.Equal(fixture.GeneratedDatabasePath, summary.DictionaryPath);
        output.WriteLine(
            $"files={summary.FilesProcessed} sheets={summary.SheetsProcessed} declarations={summary.DeclarationsWritten} " +
            $"ordinates={summary.OrdinatesWritten} values={summary.ValuesWritten} cells={summary.CellsWritten} " +
            $"shaded={summary.ShadedCellsWritten} unparsed={summary.UnparsedWritten}");
    }

    [DataFact]
    public void Extraction44_Files_AreTheXlsxFilesOfTheDirectory_AndTheirModulesAreTheModuleVersionsStartingIn44()
    {
        var files = Workbooks();
        Assert.NotEmpty(files);
        Assert.Equal(files.Length, fixture.Layouts44Summary.FilesProcessed);

        using var c = OpenLayouts();
        var rows = Query(c, "SELECT Path, ModuleCode FROM LayoutFile ORDER BY FileId");
        Assert.Equal(files.Length, rows.Count);

        // The module code of each file, read from the FILE NAME with an independent pattern.
        var fromNames = files
            .Select(f => FileNamePattern.Match(Path.GetFileName(f)))
            .Select(m => { Assert.True(m.Success); return m.Groups["module"].Value.ToUpperInvariant(); })
            .OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.Equal(fromNames, rows.Select(r => Text(r[1]).ToUpperInvariant()).OrderBy(x => x, StringComparer.Ordinal));

        // "A release publishes layouts only for the module versions that start in it" (the issue): the module
        // set of the layouts equals the module versions that start in 4.4, queried from Access.
        var starting = AccessCensus.Rows(
                RepoPaths.AccessDpm20Release44DatabasePath,
                "SELECT mv.[Code] FROM ([ModuleVersion] mv INNER JOIN [Module] m ON m.[ModuleID] = mv.[ModuleID]) "
                + "INNER JOIN [Release] r ON r.[ReleaseID] = mv.[StartReleaseID] "
                + $"WHERE r.[Code] = '{RepoPaths.Release44Code}' AND m.[isDocumentModule] = 0")
            .Select(r => Text(r[0]).ToUpperInvariant()).OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.NotEmpty(starting);
        Assert.Equal(starting, fromNames);
    }

    [DataFact]
    public void Extraction44_SheetsPerFile_EqualTheWorkbookSheetsMinusTheIndexSheet()
    {
        var files = Workbooks();
        using var c = OpenLayouts();

        var expectedByFile = new Dictionary<string, int>(StringComparer.Ordinal);
        var totalSheets = 0;
        foreach (var file in files)
        {
            var names = IndependentXlsx.SheetNames(file);
            totalSheets += names.Count;
            // Positive control of the subtraction: every workbook has exactly one Index sheet to remove.
            Assert.Equal(1, names.Count(n => n == "Index"));
            Assert.DoesNotContain("TOC", names);
            expectedByFile[Path.GetFileName(file)] = names.Count - 1;
        }

        var actualByFile = Query(
                c,
                "SELECT f.Path, COUNT(1) FROM LayoutSheet s JOIN LayoutFile f ON f.FileId = s.FileId GROUP BY f.FileId, f.Path")
            .ToDictionary(r => Path.GetFileName(Text(r[0])), r => Convert.ToInt32(r[1], CultureInfo.InvariantCulture), StringComparer.Ordinal);

        Assert.Equal(expectedByFile.OrderBy(kv => kv.Key, StringComparer.Ordinal), actualByFile.OrderBy(kv => kv.Key, StringComparer.Ordinal));
        Assert.Equal(totalSheets - files.Length, fixture.Layouts44Summary.SheetsProcessed);
        Assert.Equal((long)fixture.Layouts44Summary.SheetsProcessed, Convert.ToInt64(Query(c, "SELECT COUNT(1) FROM LayoutSheet")[0][0], CultureInfo.InvariantCulture));
        Assert.DoesNotContain(Query(c, "SELECT SheetName FROM LayoutSheet").Select(r => Text(r[0])), n => n is "Index" or "TOC");
    }

    [DataFact]
    public void Extraction44_TableCodesPerFile_EqualTheTableCodesOfTheIndexSheet()
    {
        using var c = OpenLayouts();
        var problems = new List<string>();
        var compared = 0;

        foreach (var file in Workbooks())
        {
            // The Index sheet lists the table code of every sheet (the code only: a sheet of a table with a
            // Z axis, "K_74.00.a (0010)", is listed once per Z value as "K_74.00.a").
            var expected = IndependentXlsx.IndexTableCodes(file).ToHashSet(StringComparer.Ordinal);
            var actual = Query(
                    c,
                    "SELECT DISTINCT s.TableCode FROM LayoutSheet s JOIN LayoutFile f ON f.FileId = s.FileId "
                    + $"WHERE f.Path LIKE '%{Path.GetFileName(file).Replace("'", "''", StringComparison.Ordinal)}'")
                .Select(r => Text(r[0])).ToHashSet(StringComparer.Ordinal);
            compared += expected.Count;

            var onlyIndex = expected.Except(actual).OrderBy(x => x, StringComparer.Ordinal).ToList();
            var onlyExtracted = actual.Except(expected).OrderBy(x => x, StringComparer.Ordinal).ToList();
            if (onlyIndex.Count > 0 || onlyExtracted.Count > 0)
            {
                problems.Add(
                    $"{Path.GetFileName(file)}: in the Index only [{string.Join(", ", onlyIndex.Select(x => $"'{x}'"))}], " +
                    $"extracted only [{string.Join(", ", onlyExtracted.Select(x => $"'{x}'"))}]");
            }
        }

        Assert.True(compared > 0, "The independent reading found no table code in any Index sheet.");
        Assert.True(problems.Count == 0, $"{problems.Count} file(s) whose extracted LayoutSheet.TableCode set differs from the Index sheet: {string.Join(" || ", problems)}");
    }

    [DataFact]
    public void Extraction44_EverySheet_IsAttributedToItsModuleFrameworkAndRelease44_AndTheFrameworkEqualsTheAccessRoute()
    {
        using var c = OpenLayouts();
        var accdb = RepoPaths.AccessDpm20Release44DatabasePath;
        var releaseId = AccessCensus.ReleaseId(accdb, RepoPaths.Release44Code);

        // No sheet is left without a file, and no file without its three attributes.
        Assert.Equal(0L, Convert.ToInt64(Query(c, "SELECT COUNT(1) FROM LayoutSheet s LEFT JOIN LayoutFile f ON f.FileId = s.FileId WHERE f.FileId IS NULL")[0][0], CultureInfo.InvariantCulture));
        var files = Query(c, "SELECT Path, FrameworkCode, ModuleCode, ReleaseLabel FROM LayoutFile ORDER BY FileId");
        Assert.NotEmpty(files);

        // The SQLite route, on the dictionary (mModule -> mTaxonomy -> mReportingFramework).
        using var dictionary = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = fixture.GeneratedDatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        dictionary.Open();
        var sqliteRoute = Query(
                dictionary,
                "SELECT m.ModuleCode, f.FrameworkCode FROM mModule m JOIN mTaxonomy t ON t.TaxonomyID = m.TaxonomyID "
                + "JOIN mReportingFramework f ON f.FrameworkID = t.FrameworkID")
            .ToDictionary(r => Text(r[0]).ToUpperInvariant(), r => Text(r[1]), StringComparer.Ordinal);

        var problems = new List<string>();
        foreach (var row in files)
        {
            var file = Path.GetFileName(Text(row[0]));
            var module = Text(row[2]);
            var framework = Text(row[1]);
            var release = Text(row[3]);

            // The Access route, queried independently.
            var accessFrameworks = AccessCensus.Rows(
                    accdb,
                    "SELECT f.[Code] FROM (([ModuleVersion] mv INNER JOIN [Module] m ON m.[ModuleID] = mv.[ModuleID]) "
                    + "INNER JOIN [Framework] f ON f.[FrameworkID] = m.[FrameworkID]) "
                    + $"WHERE mv.[Code] = '{module}' AND mv.[StartReleaseID] = {releaseId}")
                .Select(r => Text(r[0])).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            if (release != RepoPaths.Release44Code)
            {
                problems.Add($"{file}: release '{release}'");
            }

            if (accessFrameworks.Count != 1)
            {
                problems.Add($"{file}: Access gives {accessFrameworks.Count} framework(s) for module '{module}' starting in {RepoPaths.Release44Code}");
                continue;
            }

            if (!string.Equals(framework, accessFrameworks[0], StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"{file}: framework '{framework}' (LayoutFile) vs '{accessFrameworks[0]}' (Access route)");
            }

            if (!sqliteRoute.TryGetValue(module.ToUpperInvariant(), out var viaSqlite) || !string.Equals(framework, viaSqlite, StringComparison.Ordinal))
            {
                problems.Add($"{file}: framework '{framework}' (LayoutFile) vs '{viaSqlite ?? "<none>"}' (SQLite route)");
            }

            output.WriteLine($"{file}: module={module} framework={framework} release={release}; Access route={accessFrameworks[0]}");
        }

        Assert.True(problems.Count == 0, $"{problems.Count} file attribution problem(s): {string.Join(" || ", problems)}");
    }

    /// <summary>ZIP + XML reading of a workbook, independent of the production <c>XlsxReader</c>.</summary>
    private static class IndependentXlsx
    {
        private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        private static readonly XNamespace RelNs = "http://schemas.openxmlformats.org/package/2006/relationships";
        private static readonly XNamespace RIdNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

        public static List<string> SheetNames(string path)
        {
            using var zip = ZipFile.OpenRead(path);
            return Load(zip, "xl/workbook.xml")!.Descendants(Main + "sheet").Select(s => (string)s.Attribute("name")!).ToList();
        }

        /// <summary>The "Table Code" column (B) of the <c>Index</c> sheet, from the first row below the header (row 3).</summary>
        public static List<string> IndexTableCodes(string path)
        {
            using var zip = ZipFile.OpenRead(path);
            var workbook = Load(zip, "xl/workbook.xml")!;
            var rels = Load(zip, "xl/_rels/workbook.xml.rels")!;
            var rId = (string)workbook.Descendants(Main + "sheet").Single(s => (string)s.Attribute("name")! == "Index").Attribute(RIdNs + "id")!;
            var target = (string)rels.Descendants(RelNs + "Relationship").Single(r => (string)r.Attribute("Id")! == rId).Attribute("Target")!;
            var sheet = Load(zip, "xl/" + target.TrimStart('/').Replace("xl/", string.Empty, StringComparison.Ordinal))!;
            var shared = Load(zip, "xl/sharedStrings.xml")?.Descendants(Main + "si").Select(si => string.Concat(si.Descendants(Main + "t").Select(t => t.Value))).ToList() ?? [];

            var codes = new List<string>();
            foreach (var row in sheet.Descendants(Main + "row"))
            {
                var rowNumber = int.Parse((string)row.Attribute("r")!, CultureInfo.InvariantCulture);
                if (rowNumber <= 3)
                {
                    continue;
                }

                var cell = row.Elements(Main + "c").FirstOrDefault(c => ((string)c.Attribute("r")!).StartsWith('B'));
                if (cell is null)
                {
                    continue;
                }

                var type = (string?)cell.Attribute("t");
                var text = type switch
                {
                    "inlineStr" => string.Concat(cell.Descendants(Main + "t").Select(t => t.Value)),
                    "s" => shared[int.Parse(cell.Element(Main + "v")!.Value, CultureInfo.InvariantCulture)],
                    _ => cell.Element(Main + "v")?.Value ?? string.Empty,
                };
                if (text.Length > 0)
                {
                    codes.Add(text.Trim());
                }
            }

            return codes;
        }

        private static XDocument? Load(ZipArchive zip, string name)
        {
            var entry = zip.GetEntry(name);
            if (entry is null)
            {
                return null;
            }

            using var s = entry.Open();
            return XDocument.Load(s);
        }
    }
}
