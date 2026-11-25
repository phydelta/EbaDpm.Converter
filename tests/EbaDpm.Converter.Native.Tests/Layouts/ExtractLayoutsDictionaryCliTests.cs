using EbaDpm.Converter.Cli;
using EbaDpm.Converter.Native.Tests.PlaneC;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Native.Tests.Layouts;

/// <summary>
/// <c>--extract-layouts --dictionary &lt;file&gt;</c> takes a native output as well as a 45-table one
/// (docs/table-layouts.md, "Dictionary from a native output"; docs/usage.md). The dictionary is told
/// apart by its catalogue; what each one makes of a layout is checked on two fabricated sheets: a
/// single code <c>(zFact)</c> under the Main Property that the dictionary knows as a metric, and a single
/// code <c>(qDIM)</c> that it knows as a dimension. A dictionary that knows neither yields neither, so the
/// assertions are about the CONTENT the extraction read from the dictionary, not only about the exit code.
/// </summary>
public sealed class ExtractLayoutsDictionaryCliTests : IDisposable
{
    private readonly string _scratch = Path.Join(Path.GetTempPath(), $"native-lay-cli_{Environment.ProcessId}_{Guid.NewGuid():N}");
    private int _runs;

    public ExtractLayoutsDictionaryCliTests() => Directory.CreateDirectory(_scratch);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_scratch, recursive: true);
        }
        catch (IOException)
        {
            // Left for the OS temp cleanup.
        }
    }

    private string LayoutsDirectory()
    {
        var directory = Path.Join(_scratch, "layouts");
        if (!Directory.Exists(directory))
        {
            new MinimalXlsxBuilder("T_NAT_METRIC")
                .WithCell("D1", "Main Property").WithCell("D2", "(zFact) A metric").WithCell("A3", "Rows")
                .Save(directory, module: "MODA");
            new MinimalXlsxBuilder("T_NAT_DIM")
                .WithCell("D1", "Main Property").WithCell("D2", "(qDIM) A dimension").WithCell("A3", "Rows")
                .Save(directory, module: "MODB");
        }

        return directory;
    }

    private sealed record Extraction(
        int Exit, string Stdout, string Stderr, List<(string? Domain, string? Member)> Values,
        List<(string? Dimension, string? Domain)> Declarations, string? DictionaryPath, string? DictionarySha256);

    private Extraction Extract(string dictionaryPath)
    {
        var output = Path.Join(_scratch, $"layouts_{++_runs}.db");
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = CliRunner.Run(
            ["--extract-layouts", LayoutsDirectory(), "--out", output, "--dictionary", dictionaryPath, "--plain"], stdout, stderr);
        var values = new List<(string?, string?)>();
        var declarations = new List<(string?, string?)>();
        string? path = null;
        string? sha = null;
        if (File.Exists(output))
        {
            using var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder { DataSource = output, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            connection.Open();
            Read(connection, "SELECT DomainCode, MemberCode FROM LayoutValue ORDER BY ValueId", r => values.Add((Text(r, 0), Text(r, 1))));
            Read(connection, "SELECT DimensionCode, DomainCode FROM LayoutDeclaration ORDER BY DeclId", r => declarations.Add((Text(r, 0), Text(r, 1))));
            Read(connection, "SELECT DictionaryPath, DictionarySha256 FROM LayoutExtraction", r =>
            {
                path = Text(r, 0);
                sha = Text(r, 1);
            });
        }

        return new Extraction(exit, stdout.ToString(), stderr.ToString(), values, declarations, path, sha);
    }

    private static string? Text(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static void Read(SqliteConnection connection, string sql, Action<SqliteDataReader> onRow)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            onRow(reader);
        }
    }

    private static SyntheticNativeFile NativeDictionary(bool knowsTheCodes)
    {
        var native = new SyntheticNativeFile();
        if (knowsTheCodes)
        {
            var fact = native.Property("zFact");
            native.Variable("fact", fact);
            var dimension = native.Property("qDIM");
            var member = native.Member("MEM", "qM1");
            native.Insert(
                "PropertyCategory",
                ("PropertyID", dimension), ("StartReleaseID", 1L), ("CategoryID", native.Category("MEM")));
            native.Variable("fact", native.Property("zOther"), (dimension, member));
        }

        native.Connection.Close(); // the extraction hashes and reads the file: no open writer.
        SqliteConnection.ClearAllPools();
        return native;
    }

    private string FortyFiveTableDictionary(bool knowsTheCodes)
    {
        var path = Path.Join(_scratch, $"dist_{knowsTheCodes}.db");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE mDomain (DomainID INTEGER PRIMARY KEY, DomainCode TEXT);
            CREATE TABLE mMember (MemberID INTEGER PRIMARY KEY, DomainID INTEGER, MemberCode TEXT);
            CREATE TABLE mDimension (DimensionID INTEGER PRIMARY KEY, DimensionCode TEXT, DomainID INTEGER);
            CREATE TABLE mReportingFramework (FrameworkID INTEGER PRIMARY KEY, FrameworkCode TEXT);
            CREATE TABLE mTaxonomy (TaxonomyID INTEGER PRIMARY KEY, FrameworkID INTEGER);
            CREATE TABLE mModule (ModuleID INTEGER PRIMARY KEY, TaxonomyID INTEGER, ModuleCode TEXT);
            INSERT INTO mDomain VALUES (1, 'MET'), (2, 'MEM');
            """;
        command.ExecuteNonQuery();
        if (knowsTheCodes)
        {
            command.CommandText =
                """
                INSERT INTO mMember VALUES (1, 1, 'zFact'), (2, 2, 'qM1');
                INSERT INTO mDimension VALUES (1, 'qDIM', 2);
                """;
            command.ExecuteNonQuery();
        }

        return path;
    }

    private static void AssertReadAsMetricAndDimension(Extraction run, string dictionaryPath)
    {
        Assert.True(run.Exit == 0, $"exit {run.Exit}; stderr: {run.Stderr}; stdout: {run.Stdout}");
        Assert.Contains(("MET", "zFact"), run.Values);
        Assert.Contains(("qDIM", "MEM"), run.Declarations);
        Assert.Equal(dictionaryPath, run.DictionaryPath);
        Assert.Matches("^[0-9a-f]{64}$", run.DictionarySha256!.ToLowerInvariant());
    }

    [Fact]
    public void NativeDictionary_IsRecognisedAndUsed_TheCodesItKnowsAreClassified()
    {
        using var native = NativeDictionary(knowsTheCodes: true);

        var run = Extract(native.FilePath);

        AssertReadAsMetricAndDimension(run, native.FilePath);
    }

    [Fact]
    public void NativeDictionary_WithoutTheCodes_YieldsNeitherTheMetricNorTheDimension()
    {
        // Control of the test above: the same layouts and the same command with a native file that does
        // not know the codes. If the dictionary content were not read, this run would equal the other.
        using var native = NativeDictionary(knowsTheCodes: false);

        var run = Extract(native.FilePath);

        Assert.True(run.Exit == 0, $"exit {run.Exit}; stderr: {run.Stderr}");
        Assert.DoesNotContain(("MET", "zFact"), run.Values);
        Assert.DoesNotContain(run.Declarations, d => d.Dimension == "qDIM");
    }

    [Fact]
    public void FortyFiveTableDictionary_StillUsesTheDistributionLoader()
    {
        var path = FortyFiveTableDictionary(knowsTheCodes: true);

        var run = Extract(path);

        AssertReadAsMetricAndDimension(run, path);
    }

    [Fact]
    public void FortyFiveTableDictionary_WithoutTheCodes_YieldsNeitherTheMetricNorTheDimension()
    {
        var path = FortyFiveTableDictionary(knowsTheCodes: false);

        var run = Extract(path);

        Assert.True(run.Exit == 0, $"exit {run.Exit}; stderr: {run.Stderr}");
        Assert.DoesNotContain(("MET", "zFact"), run.Values);
        Assert.DoesNotContain(run.Declarations, d => d.Dimension == "qDIM");
    }

    [Fact]
    public void NativeAndFortyFiveTableDictionaries_GiveTheSameRepositoryForTheSameCodes()
    {
        using var native = NativeDictionary(knowsTheCodes: true);
        var dist = FortyFiveTableDictionary(knowsTheCodes: true);

        var fromNative = Extract(native.FilePath);
        var fromDist = Extract(dist);

        Assert.Equal(fromDist.Values, fromNative.Values);
        Assert.Equal(fromDist.Declarations, fromNative.Declarations);
    }
}
