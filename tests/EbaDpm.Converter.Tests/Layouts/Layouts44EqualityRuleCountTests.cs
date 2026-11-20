using System.IO.Compression;
using System.Xml.Linq;
using EbaDpm.Converter.Tests.Dpm2;
using EbaDpm.Converter.Tests.Infrastructure;
using EbaDpm.Converter.Tests.Schema;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace EbaDpm.Converter.Tests.Layouts;

/// <summary>
/// Equality rules ("==" lines of the cell comments) in the 4.4 layouts (#67). The count is made
/// independently of <c>XlsxReader</c> (ZIP + XML over every <c>xl/comments*.xml</c> part) and compared with
/// <c>LayoutEqualityRule</c> of the extracted repository. 4.4 has none; the same scan over the 4.3 layouts
/// finds them, which shows that the scan can find one.
/// </summary>
internal static class CommentCensus
{
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    /// <summary>(comments in total, comments with at least one "==" in their text) over every workbook of the directory.</summary>
    public static (long Comments, long WithEquality) Count(string directory)
    {
        long comments = 0, withEquality = 0;
        foreach (var archive in Directory.EnumerateFiles(directory, "*.xlsx").Select(ZipFile.OpenRead))
        {
            using var zip = archive;
            foreach (var commentStream in zip.Entries
                         .Where(e => e.FullName.StartsWith("xl/comments", StringComparison.Ordinal) && e.FullName.EndsWith(".xml", StringComparison.Ordinal))
                         .Select(e => e.Open()))
            {
                using var stream = commentStream;
                foreach (var comment in XDocument.Load(stream).Descendants(Main + "comment"))
                {
                    comments++;
                    var text = string.Concat(comment.Descendants(Main + "t").Select(t => t.Value));
                    if (text.Contains("==", StringComparison.Ordinal))
                    {
                        withEquality++;
                    }
                }
            }
        }

        return (comments, withEquality);
    }
}

[Trait("Tier", "Synthetic")]
public sealed class CommentCensusSyntheticTests : IDisposable
{
    private readonly string _dir = Path.Join(Path.GetTempPath(), $"EbaDpm.CommentCensus_{Environment.ProcessId}_{Guid.NewGuid():N}");

    public CommentCensusSyntheticTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void TheCensus_CountsACommentWithAnEqualityLine_AndOneWithout_PositiveControl()
    {
        const string xml =
            "<comments xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><commentList>"
            + "<comment ref=\"A1\"><text><r><t>x == y</t></r></text></comment>"
            + "<comment ref=\"A2\"><text><r><t>no rule here</t></r></text></comment>"
            + "</commentList></comments>";
        using (var zip = ZipFile.Open(Path.Join(_dir, "w.xlsx"), ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(zip.CreateEntry("xl/comments1.xml").Open());
            writer.Write(xml);
        }

        Assert.Equal((2L, 1L), CommentCensus.Count(_dir));
    }
}

[Collection("Dpm2044ValidateSuite")]
[Trait("Tier", "RealData")]
public sealed class Layouts44EqualityRuleCountRealDataTests(Dpm2044ValidateFixture fixture, ITestOutputHelper output)
{
    private static long RulesIn(string repository)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = repository, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(1) FROM LayoutEqualityRule";
        return (long)cmd.ExecuteScalar()!;
    }

    [DataFact]
    public void Layouts44_HaveNoEqualityRule_AsAnIndependentScanOfTheCommentsAndTheRepositoryAgree()
    {
        RepoPaths.EnsureTableLayouts44DirectoryExists();
        RepoPaths.EnsureTableLayouts43DirectoryExists();

        var (comments44, equality44) = CommentCensus.Count(RepoPaths.TableLayouts44Directory);
        var rules44 = RulesIn(fixture.Layouts44DatabasePath);
        var (comments43, equality43) = CommentCensus.Count(RepoPaths.TableLayouts43Directory);
        output.WriteLine($"4.4: {comments44} comments, {equality44} with '=='; LayoutEqualityRule rows {rules44}. 4.3 (positive control): {comments43} comments, {equality43} with '=='.");

        Assert.True(comments44 > 0, "The scan found no comment in the 4.4 layouts: a zero over nothing proves nothing.");
        Assert.True(equality43 > 0, "Positive control: the scan finds no '==' comment in the 4.3 layouts.");
        Assert.Equal(0, equality44);
        Assert.Equal(0, rules44);
    }
}
