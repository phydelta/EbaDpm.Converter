using EbaDpm.Converter.Core.Layouts;
using EbaDpm.Converter.Tests.Schema;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Tests.Layouts;

/// <summary>
/// Gate on <c>LayoutUnparsed</c>. <c>LayoutUnparsed = 0</c> only shows "there are no positional
/// orphans", not "all codes exist". The gate therefore:
///
/// <list type="bullet">
/// <item><c>TryPairValue</c> validates <c>(domain:member)</c> against
/// <see cref="LayoutDictionary.IsValidDomainMember"/>, even when it is correctly paired by
/// position.</item>
/// <item><c>EvaluateStandaloneCell</c> distinguishes broken syntax (<c>GrammarFailure</c>) from
/// rhetorical prose, instead of treating them the same.</item>
/// <item>The key marker is detected even if it is not preceded by <c>"("</c> (see
/// <see cref="StandaloneKeyValueMarker_WithoutLeadingParenthesis_IsFlaggedAsGrammarFailure"/>).</item>
/// </list>
///
/// This class holds the POSITIVE controls fabricated outside the data directory with
/// <see cref="MinimalXlsxBuilder"/>: a zero is accompanied by the proof that it knows how not to be
/// zero.
/// </summary>
public sealed class LayoutUnparsedGateTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"EbaDpm.UnparsedGate_{Environment.ProcessId}_{Guid.NewGuid():N}");

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
    public void OrphanTwoCodeAnnotation_WithNoPairingDeclaration_IsFlaggedAsOrphanValue()
    {
        // POSITIVE control 1: a cell shaped like "(domain:member)" that NO declaration pairs (by
        // row or by column) falls into LayoutUnparsed, real code or not, with Kind=OrphanValue.
        var inputDir = Path.Combine(_tempDir, "input1");
        new MinimalXlsxBuilder("T_ORPHAN")
            .WithCell("A3", "Rows")
            .WithCell("B10", "Structural label, not a declaration")
            .WithCell("C10", "(ZZFAKE:ZZFAKE2) orphan two-code, nothing pairs with it")
            .Save(inputDir);

        var outputPath = Path.Combine(_tempDir, "out1.db");
        var summary = LayoutExtractor.Extract(inputDir, outputPath, overwrite: false, dictionaryPath: null);

        Assert.Equal(1, summary.UnparsedWritten);

        using var connection = Open(outputPath);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT CellRef, Kind FROM LayoutUnparsed";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("C10", reader.GetString(0));
        Assert.Equal("OrphanValue", reader.GetString(1));
    }

    [DataFact]
    public void PairedTwoCodeAnnotation_WithFakeNonExistentCodes_IsRejected_AsOrphanValueAndOrphanDeclaration()
    {
        // The real declaration "(ZZDIM:ZZDOM)" paired by row with a value "(ZZFAKE:ZZFAKE2)" that
        // does not exist in any real schema must NOT be silently accepted. What MUST happen:
        // TryPairValue claims the cell (it does not stay "loose" for anyone else), queries
        // IsValidDomainMember, and since ZZFAKE:ZZFAKE2 does not exist, records it as OrphanValue
        // WITHOUT adding a LayoutValue. As a consequence, the declaration "(ZZDIM:ZZDOM)" is left
        // without any associated LayoutValue -- and the final OrphanDeclaration sweep
        // (LayoutSheetParser.Parse) also flags it. Expected total: 2 rows in LayoutUnparsed, not 0.
        var inputDir = Path.Combine(_tempDir, "input2");
        new MinimalXlsxBuilder("T_PAIRED_FAKE")
            .WithCell("A3", "Rows")
            .WithCell("B3", "(ZZDIM:ZZDOM) fake dimension declaration")
            .WithCell("C3", "(ZZFAKE:ZZFAKE2) fake paired value")
            .Save(inputDir);

        var outputPath = Path.Combine(_tempDir, "out2.db");
        var summary = LayoutExtractor.Extract(inputDir, outputPath, overwrite: false, RepoPaths.ReferenceDatabasePath);

        Assert.Equal(2, summary.UnparsedWritten);

        using var connection = Open(outputPath);

        // The fabricated value is NOT recorded as a LayoutValue.
        using var cmdValue = connection.CreateCommand();
        cmdValue.CommandText = "SELECT COUNT(*) FROM LayoutValue WHERE CellRef = 'C3'";
        Assert.Equal(0L, Convert.ToInt64(cmdValue.ExecuteScalar()));

        using var cmdC3 = connection.CreateCommand();
        cmdC3.CommandText = "SELECT Kind FROM LayoutUnparsed WHERE CellRef = 'C3'";
        Assert.Equal("OrphanValue", (string?)cmdC3.ExecuteScalar());

        using var cmdB3 = connection.CreateCommand();
        cmdB3.CommandText = "SELECT Kind FROM LayoutUnparsed WHERE CellRef = 'B3'";
        Assert.Equal("OrphanDeclaration", (string?)cmdB3.ExecuteScalar());
    }

    [DataFact]
    public void SyntacticallyBrokenAnnotation_UnclosedParenthesis_IsFlaggedAsGrammarFailure()
    {
        // An unclosed parenthesis must not be invisible (neither LayoutValue nor LayoutUnparsed):
        // it is recorded with Kind=GrammarFailure (EvaluateStandaloneCell,
        // AnnotationKind.NotCodeShaped with "!core.Contains(')')"): it is broken syntax, not
        // rhetorical prose of the "(-)"/"(a)"/"(EU)" kind.
        var inputDir = Path.Combine(_tempDir, "input3");
        new MinimalXlsxBuilder("T_BROKEN")
            .WithCell("A3", "Rows")
            .WithCell("B10", "Structural label")
            .WithCell("C10", "(ZZBROKEN unclosed parenthesis, no code shape at all")
            .Save(inputDir);

        var outputPath = Path.Combine(_tempDir, "out3.db");
        var summary = LayoutExtractor.Extract(inputDir, outputPath, overwrite: false, RepoPaths.ReferenceDatabasePath);

        Assert.Equal(1, summary.UnparsedWritten);

        using var connection = Open(outputPath);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Kind FROM LayoutUnparsed WHERE CellRef = 'C10'";
        Assert.Equal("GrammarFailure", (string?)cmd.ExecuteScalar());

        // It is still not a LayoutValue -- it never was and was never meant to be.
        using var cmdValue = connection.CreateCommand();
        cmdValue.CommandText = "SELECT COUNT(*) FROM LayoutValue WHERE CellRef = 'C10'";
        Assert.Equal(0L, Convert.ToInt64(cmdValue.ExecuteScalar()));
    }

    [DataFact]
    public void ClosedRhetoricalParenthesis_NonCodeContent_IsStillCorrectlyIgnored()
    {
        // NEGATIVE control (the positive ones must be accompanied by a negative one that stays
        // quiet): "(-)", "(a)", "(EU)", "(call)" -- parentheses that DO close but have no code
        // shape -- must still not be recorded. If this test started to fail, the gate would have
        // become too sensitive (false positives, like the "347" that motivated narrowing the key
        // marker to the literal "<Key value>").
        var inputDir = Path.Combine(_tempDir, "input5");
        new MinimalXlsxBuilder("T_RHETORICAL")
            .WithCell("A3", "Rows")
            .WithCell("B10", "Structural label")
            .WithCell("C10", "(-)")
            .WithCell("D10", "(EU) some rhetorical footnote")
            .Save(inputDir);

        var outputPath = Path.Combine(_tempDir, "out5.db");
        var summary = LayoutExtractor.Extract(inputDir, outputPath, overwrite: false, RepoPaths.ReferenceDatabasePath);

        Assert.Equal(0, summary.UnparsedWritten);
    }

    [DataFact]
    public void StandaloneKeyValueMarker_WithoutLeadingParenthesis_IsFlaggedAsGrammarFailure()
    {
        // An earlier implementation did "switch (parse.Kind) { case PlainText: case NotCodeShaped:
        // return; ... }" WITHOUT looking at HasKeyMarker for the PlainText case. A text that
        // contains the "<Key value>" marker but does NOT start with "(" (a key that lost its
        // domain parenthesis, or a transcription error) would have been PlainText and silently
        // discarded, exactly like "(-)" is.
        //
        // The current version tells them apart: EvaluateStandaloneCell computes "hasKeyMarker"
        // BEFORE looking at whether the text starts with "(", and if the resulting text
        // (parse.Kind == PlainText, because it does not start with a parenthesis) contains the
        // marker, it records it as GrammarFailure -- "not preceded by '(domain:...)'".
        var inputDir = Path.Combine(_tempDir, "input6");
        new MinimalXlsxBuilder("T_KEY_NO_PAREN")
            .WithCell("A3", "Rows")
            .WithCell("B10", "Structural label")
            .WithCell("C10", "Reference without a leading domain marker <Key value> trailing text")
            .Save(inputDir);

        var outputPath = Path.Combine(_tempDir, "out6.db");
        var summary = LayoutExtractor.Extract(inputDir, outputPath, overwrite: false, RepoPaths.ReferenceDatabasePath);

        Assert.Equal(1, summary.UnparsedWritten);

        using var connection = Open(outputPath);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Kind FROM LayoutUnparsed WHERE CellRef = 'C10'";
        Assert.Equal("GrammarFailure", (string?)cmd.ExecuteScalar());
    }

    [DataFact]
    public void DataTypeSlotAngleBracketMarkers_DifferentFromKeyValue_AreNeverTouchedByTheGate()
    {
        // Narrowing the key marker to the literal "<Key value>" must NOT make other "<...>" markers
        // invisible -- those that live in the data type slot ("<Reference portfolio/instrument...>",
        // "<Securitisation internal code>"). These go in the SAME cell as the datapoint identifier
        // (shape "NNNNN_x000D_type"), so LayoutDatapointGrammar.Matches recognises them and diverts
        // them BEFORE they reach any LayoutUnparsed path -- checked here end to end, not only by
        // reading the code.
        //
        // Where it is checked: D10 generates ITS OWN COLUMN ordinate (X, CellRef=D10) -- the right
        // place under the model: DataType is a property of the CELL (row x column), not of the
        // identity of the row. This test does NOT require seeing it on Y: requiring it there, as
        // well as on X, would be precisely the duplication that risks falling out of sync.
        var inputDir = Path.Combine(_tempDir, "input7");
        new MinimalXlsxBuilder("T_ANGLE_DATATYPE")
            .WithCell("A3", "Rows")
            .WithCell("B10", "Some row label")
            .WithCell("C10", "0010")
            .WithCell("D10", "199116_x000D_text_x000D_<Reference portfolio/instrument for the Benchmarking exercise>")
            .Save(inputDir);

        var outputPath = Path.Combine(_tempDir, "out7.db");
        var summary = LayoutExtractor.Extract(inputDir, outputPath, overwrite: false, RepoPaths.ReferenceDatabasePath);

        Assert.Equal(0, summary.UnparsedWritten);

        using var connection = Open(outputPath);

        // The requirement that matters: the CELL D10 -- DataType is a property of LayoutCell, not
        // of LayoutOrdinate (which has neither CellRef nor DataType).
        using var cmdX = connection.CreateCommand();
        cmdX.CommandText = "SELECT DataType FROM LayoutCell WHERE CellRef = 'D10'";
        var dataTypeX = (string?)cmdX.ExecuteScalar();
        Assert.NotNull(dataTypeX);
        Assert.Contains("<Reference portfolio/instrument for the Benchmarking exercise>", dataTypeX);
    }

    [DataFact]
    public void StandaloneRealSingleCode_OrphanedFromMainProperty_IsFlaggedOnlyWithDictionary()
    {
        // "qLHL" (Type of risk) is a real DimensionCode of mDimension in the 4.2 reference. Loose
        // and without a 'Main Property' row to pair it, it MUST appear in LayoutUnparsed -- but
        // only with --dictionary; without it, resolution always falls to Unknown and NOTHING is
        // recorded (no per-cell warning, only the global CLI warning).
        var inputDir = Path.Combine(_tempDir, "input4");
        new MinimalXlsxBuilder("T_STANDALONE_REAL")
            .WithCell("A3", "Rows")
            .WithCell("B10", "Structural label")
            .WithCell("C10", "(qLHL) standalone real dimension code, not under Main Property")
            .Save(inputDir);

        var outputWithDict = Path.Combine(_tempDir, "out4_dict.db");
        var summaryWithDict = LayoutExtractor.Extract(inputDir, outputWithDict, overwrite: false, RepoPaths.ReferenceDatabasePath);
        Assert.Equal(1, summaryWithDict.UnparsedWritten);

        var outputNoDict = Path.Combine(_tempDir, "out4_nodict.db");
        var summaryNoDict = LayoutExtractor.Extract(inputDir, outputNoDict, overwrite: false, dictionaryPath: null);

        // Without a dictionary, the cell is NOT lost in LayoutRaw, but neither is it marked in any
        // queryable way: 0 in LayoutUnparsed and 0 in LayoutValue. The only warning is the global
        // CLI line, not something that can be queried by SQL. The CLI always requires --dictionary;
        // this path is only reachable by calling LayoutExtractor.Extract (the library) directly,
        // as here.
        Assert.Equal(0, summaryNoDict.UnparsedWritten);
        using var connection = Open(outputNoDict);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM LayoutValue WHERE CellRef = 'C10'";
        Assert.Equal(0L, Convert.ToInt64(cmd.ExecuteScalar()));
    }

    private static SqliteConnection Open(string path)
    {
        var connectionString = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly }.ToString();
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }
}
