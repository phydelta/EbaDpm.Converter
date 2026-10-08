using EbaDpm.Converter.Core.Layouts;
using EbaDpm.Converter.Tests.Schema;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Tests.Layouts;

/// <summary>
/// Extraction of the "==" equality rules from the cell comments, over fabricated <c>.xlsx</c>
/// files (never the data directory). <c>LayoutEqualityRuleParser</c> is <c>internal</c> without
/// <c>InternalsVisibleTo</c> (a test that invokes the very function it claims to test would not
/// detect an error shared between the two), so EVERYTHING is exercised through the public surface
/// <c>LayoutExtractor.Extract</c> and the resulting SQLite repository.
/// </summary>
public sealed class LayoutEqualityRuleExtractionTests : IDisposable
{
    private readonly string _tempDir;

    public LayoutEqualityRuleExtractionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"EbaDpm.EquExtract_{Environment.ProcessId}_{Guid.NewGuid():N}");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort.
        }
    }

    [Fact]
    public void Extract_OneLineWithTwoTerms_WritesASingleRuleWithTwoPositions()
    {
        var inputDir = Path.Combine(_tempDir, "input");
        new MultiSheetXlsxWithCommentsBuilder()
            .AddSheet("A_01.01", out var sheetA)
            .AddSheet("B_02.01", out var sheetB)
            .WithCell(sheetA, "A1", "A_01.01 - Title")
            .WithCell(sheetB, "A1", "B_02.01 - Title")
            .WithComment(sheetA, "D10", "{A_01.01, r0010, c0020}=={B_02.01, r0030, c0040}", commentsFileNumber: 1)
            .Save(inputDir);

        var outputPath = Path.Combine(_tempDir, "out.db");
        var summary = LayoutExtractor.Extract(inputDir, outputPath, overwrite: false, dictionaryPath: null);

        Assert.Equal(0, summary.EqualityRuleGrammarFailures);
        Assert.Equal(1, summary.EqualityRulesWritten);
        Assert.Equal(1, summary.EqualityRuleLinesFound);

        using var connection = Open(outputPath);
        var rows = Rows(connection,
            """SELECT "RuleId", "Position", "TableCode", "RowCode", "ColumnCode", "ZCode" FROM "LayoutEqualityRule" ORDER BY "RuleId", "Position" """);

        Assert.Equal(2, rows.Count);
        var ruleId = rows[0][0];
        Assert.All(rows, r => Assert.Equal(ruleId, r[0]));

        // CANONICAL order: alphabetical by raw term: "A_01.01..." < "B_02.01...".
        Assert.Equal(["0", "A_01.01", "0010", "0020", ""], rows[0].Skip(1).Select(v => v ?? "").ToArray());
        Assert.Equal(["1", "B_02.01", "0030", "0040", ""], rows[1].Skip(1).Select(v => v ?? "").ToArray());
    }

    [Fact]
    public void Extract_ThreeChainedTerms_IsASingleRuleOfThreePositions_NotTwoOfTwo()
    {
        var inputDir = Path.Combine(_tempDir, "input");
        new MultiSheetXlsxWithCommentsBuilder()
            .AddSheet("A_01.01", out var sheetA)
            .WithCell(sheetA, "A1", "A_01.01")
            .WithComment(sheetA, "D10", "{A_01.01, r1, c1}=={B_01.01, r2, c2}=={C_01.01, r3, c3}", commentsFileNumber: 1)
            .Save(inputDir);

        var outputPath = Path.Combine(_tempDir, "out.db");
        var summary = LayoutExtractor.Extract(inputDir, outputPath, overwrite: false, dictionaryPath: null);

        Assert.Equal(1, summary.EqualityRulesWritten);
        Assert.Equal(0, summary.EqualityRuleGrammarFailures);

        using var connection = Open(outputPath);
        var distinctRules = Scalar(connection, """SELECT COUNT(DISTINCT "RuleId") FROM "LayoutEqualityRule" """);
        var totalRows = Scalar(connection, """SELECT COUNT(*) FROM "LayoutEqualityRule" """);

        Assert.Equal(1L, distinctRules);
        Assert.Equal(3L, totalRows);
    }

    [Fact]
    public void Extract_TheSameRuleAnnotatedInBothCellsItNames_WithADifferentTermOrder_DedupsToASingleRule()
    {
        // The EBA annotates the SAME rule, in full, in the comment of EACH participating cell --
        // sometimes with a different term order depending on which cell it is read from.
        // Deduplication has to be by SET, not by literal text.
        var inputDir = Path.Combine(_tempDir, "input");
        new MultiSheetXlsxWithCommentsBuilder()
            .AddSheet("A_01.01", out var sheetA)
            .AddSheet("B_02.01", out var sheetB)
            .WithCell(sheetA, "A1", "A_01.01")
            .WithCell(sheetB, "A1", "B_02.01")
            .WithComment(sheetA, "D10", "{A_01.01, r0010, c0020}=={B_02.01, r0030, c0040}", commentsFileNumber: 1)
            .WithComment(sheetB, "E20", "{B_02.01, r0030, c0040}=={A_01.01, r0010, c0020}", commentsFileNumber: 2)
            .Save(inputDir);

        var outputPath = Path.Combine(_tempDir, "out.db");
        var summary = LayoutExtractor.Extract(inputDir, outputPath, overwrite: false, dictionaryPath: null);

        // Two comments examined, two "==" lines found, but only ONE rule written.
        Assert.Equal(2, summary.EqualityCommentsExamined);
        Assert.Equal(2, summary.EqualityRuleLinesFound);
        Assert.Equal(1, summary.EqualityRulesWritten);

        using var connection = Open(outputPath);
        Assert.Equal(1L, Scalar(connection, """SELECT COUNT(DISTINCT "RuleId") FROM "LayoutEqualityRule" """));
        Assert.Equal(2L, Scalar(connection, """SELECT COUNT(*) FROM "LayoutEqualityRule" """));
    }

    [Fact]
    public void Extract_TermWithZCode_CapturesItTrimmed_AndToleratesTheFormWithoutSpaceAfterC()
    {
        var inputDir = Path.Combine(_tempDir, "input");
        new MultiSheetXlsxWithCommentsBuilder()
            .AddSheet("A_01.01", out var sheetA)
            .WithCell(sheetA, "A1", "A_01.01")
            // "c 0200" (with a space) and "r0090" (without) -- both forms coexist in the real
            // corpus -- and the Z term "s0020" without a space after the comma.
            .WithComment(sheetA, "D10", "{A_01.01, r0090, c 0200, s0020}=={B_01.01, r1, c1}", commentsFileNumber: 1)
            .Save(inputDir);

        var outputPath = Path.Combine(_tempDir, "out.db");
        var summary = LayoutExtractor.Extract(inputDir, outputPath, overwrite: false, dictionaryPath: null);

        Assert.Equal(0, summary.EqualityRuleGrammarFailures);

        using var connection = Open(outputPath);
        var row = Rows(connection,
            """SELECT "RowCode", "ColumnCode", "ZCode" FROM "LayoutEqualityRule" WHERE "TableCode" = 'A_01.01' """).Single();

        Assert.Equal("0090", row[0]);
        Assert.Equal("0200", row[1]);
        Assert.Equal("0020", row[2]);
    }

    [Fact]
    public void Extract_ALineWithDoubleEqualsButTermsWithoutCommas_IsAGrammarFailure_NotSilentlyDiscarded()
    {
        var inputDir = Path.Combine(_tempDir, "input");
        new MultiSheetXlsxWithCommentsBuilder()
            .AddSheet("A_01.01", out var sheetA)
            .WithCell(sheetA, "A1", "A_01.01")
            .WithComment(sheetA, "D10", "{no-commas-here}=={nor-here}", commentsFileNumber: 1)
            .Save(inputDir);

        var outputPath = Path.Combine(_tempDir, "out.db");
        var summary = LayoutExtractor.Extract(inputDir, outputPath, overwrite: false, dictionaryPath: null);

        Assert.Equal(1, summary.EqualityRuleLinesFound);
        Assert.Equal(0, summary.EqualityRulesWritten);
        Assert.Equal(1, summary.EqualityRuleGrammarFailures);

        using var connection = Open(outputPath);
        Assert.Equal(0L, Scalar(connection, """SELECT COUNT(*) FROM "LayoutEqualityRule" """));
        Assert.Equal(
            1L,
            Scalar(connection, """SELECT COUNT(*) FROM "LayoutUnparsed" WHERE "Kind" = 'EqualityRuleGrammarFailure' """));
    }

    [Fact]
    public void Extract_ACommentWithoutDoubleEquals_GeneratesNoRule_ItIsProseOrAnOpenAxisRestriction()
    {
        var inputDir = Path.Combine(_tempDir, "input");
        new MultiSheetXlsxWithCommentsBuilder()
            .AddSheet("A_01.01", out var sheetA)
            .WithCell(sheetA, "A1", "A_01.01")
            .WithComment(sheetA, "D10", "KeyVariableID: 12345\nValue chosen from: qEEA(*[qAE1])", commentsFileNumber: 1)
            .Save(inputDir);

        var outputPath = Path.Combine(_tempDir, "out.db");
        var summary = LayoutExtractor.Extract(inputDir, outputPath, overwrite: false, dictionaryPath: null);

        Assert.Equal(1, summary.EqualityCommentsExamined);
        Assert.Equal(0, summary.EqualityRuleLinesFound);
        Assert.Equal(0, summary.EqualityRulesWritten);
        Assert.Equal(0, summary.EqualityRuleGrammarFailures);
    }

    /// <summary>
    /// The real corpus misaligns <c>sheetN.xml</c> and <c>commentsN.xml</c> as soon as one sheet
    /// carries no comments (the following <c>commentsN.xml</c> files shift), which is why the
    /// reader resolves them through <c>.rels</c>. Exactly that shape is fabricated: the sheet
    /// WITHOUT comments is <c>sheet1.xml</c> (position 1, first file); the sheet WITH comments is
    /// <c>sheet2.xml</c> (position 2), but its <c>comments*.xml</c> is the FIRST comments file of
    /// the package -- <c>xl/comments1.xml</c>, the number that "sheetN -> commentsN" would assign
    /// to sheet 1, not to sheet 2 -- linked only by the own relationship of <c>sheet2.xml</c>
    /// (<c>xl/worksheets/_rels/sheet2.xml.rels</c>).
    ///
    /// If <c>XlsxReader</c> assumed "sheetN.xml -> commentsN.xml" instead of reading the
    /// <c>.rels</c>, this comment would be lost (or associated with sheet 1, which has no declared
    /// comments relationship) -- the rule would come out with an empty <c>TableCode</c> or would not
    /// appear at all. With resolution through <c>.rels</c> it has to appear bound to <c>B_02.01</c>.
    /// </summary>
    [Fact]
    public void Extract_SheetWithoutCommentsFollowedBySheetWithComments_ResolvesTheCommentThroughRels_NotByFileNumber()
    {
        var inputDir = Path.Combine(_tempDir, "input");
        new MultiSheetXlsxWithCommentsBuilder()
            .AddSheet("A_01.01", out var sheetA) // sheet1.xml -- WITHOUT comments, no _rels.
            .AddSheet("B_02.01", out var sheetB) // sheet2.xml -- WITH a comment, but in comments1.xml.
            .WithCell(sheetA, "A1", "A_01.01")
            .WithCell(sheetB, "A1", "B_02.01")
            .WithComment(sheetB, "D10", "{B_02.01, r0010, c0020}=={C_03.01, r0030, c0040}", commentsFileNumber: 1)
            .Save(inputDir);

        var outputPath = Path.Combine(_tempDir, "out.db");
        var summary = LayoutExtractor.Extract(inputDir, outputPath, overwrite: false, dictionaryPath: null);

        // Control: sheet A (without comments) contributes no examined comment; the only comment in
        // the package is B's, and it IS examined -- if the misalignment broke the resolution, this
        // counter would be 0.
        Assert.Equal(1, summary.EqualityCommentsExamined);
        Assert.Equal(1, summary.EqualityRulesWritten);
        Assert.Equal(0, summary.EqualityRuleGrammarFailures);

        using var connection = Open(outputPath);

        // The SheetId the LayoutEqualityRule rows are bound to has to be that of B_02.01 (join
        // against LayoutSheet), NEVER that of A_01.01 -- which shows that the resolution went
        // through the file's own relationship, not through "sheet2.xml -> comments2.xml" (which
        // does not exist: comments2.xml was not written at all in this fixture).
        var sheetCodes = Rows(connection,
            """
            SELECT s."TableCode"
            FROM "LayoutEqualityRule" r
            JOIN "LayoutSheet" s ON s."SheetId" = r."SheetId"
            """).Select(r => r[0]).Distinct().ToList();

        Assert.Equal(["B_02.01"], sheetCodes);

        var tableCodes = Rows(connection, """SELECT "TableCode" FROM "LayoutEqualityRule" ORDER BY "Position" """)
            .Select(r => r[0]!).ToList();
        Assert.Equal(["B_02.01", "C_03.01"], tableCodes);
    }

    private static SqliteConnection Open(string path)
    {
        var connectionString = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly }.ToString();
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static List<string?[]> Rows(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        var result = new List<string?[]>();
        while (reader.Read())
        {
            var row = new string?[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
            {
                row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i).ToString();
            }

            result.Add(row);
        }

        return result;
    }
}
