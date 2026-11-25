using System.Data.OleDb;
using EbaDpm.Converter.Cli;
using EbaDpm.Converter.Native.Catalog;
using EbaDpm.Converter.Native.Schema;
using EbaDpm.Converter.Native.Tests.Support;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Native.Tests.Cli;

/// <summary>
/// <c>--target native</c> on the command line (docs/usage.md, "Native output"): argument errors,
/// the happy path, <c>--schema-only</c>, and the guarantee that the default target is unchanged.
/// </summary>
public sealed class NativeCliTests : IClassFixture<SyntheticConversionFixture>, IDisposable
{
    private readonly SyntheticConversionFixture _fixture;
    private readonly string _scratch = Path.Join(Path.GetTempPath(), $"native-cli_{Environment.ProcessId}_{Guid.NewGuid():N}");

    public NativeCliTests(SyntheticConversionFixture fixture)
    {
        _fixture = fixture;
        Directory.CreateDirectory(_scratch);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        TempFiles.ReleaseAccess();
        try
        {
            Directory.Delete(_scratch, recursive: true);
        }
        catch (IOException)
        {
            // Left for the OS temp cleanup.
        }
    }

    private string P(string name) => Path.Join(_scratch, name);

    private static (int Code, string Out, string Err) Run(params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var code = CliRunner.Run(args, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private static List<(string Type, string Name, string? Sql)> Master(string path)
    {
        using var sqlite = AccessSqliteComparer.OpenSqlite(path);
        return AccessSqliteComparer.Rows(
                sqlite, "SELECT type, name, sql FROM sqlite_master WHERE name NOT LIKE 'sqlite!_%' ESCAPE '!' ORDER BY type, name")
            .Select(r => ((string)r[0]!, (string)r[1]!, (string?)r[2])).ToList();
    }

    // ------------------------------------------------------------------ --target values

    [Theory]
    [InlineData("Native")]
    [InlineData("NATIVE")]
    [InlineData("Distribution")]
    [InlineData("nativ")]
    [InlineData("1.0")]
    [InlineData("both")]
    public void Target_InvalidValue_IsAnArgumentError_ListingTheValidOnes(string value)
    {
        var (code, stdout, stderr) = Run("--schema-only", "--output", P("x.db"), "--target", value);

        Assert.Equal(CliRunner.ExitArgumentError, code);
        Assert.Contains($"invalid value '{value}' for --target", stderr, StringComparison.Ordinal);
        Assert.Contains("distribution", stderr, StringComparison.Ordinal);
        Assert.Contains("native", stderr, StringComparison.Ordinal);
        Assert.False(File.Exists(P("x.db")));
        Assert.Empty(stdout);
    }

    [Fact]
    public void Target_InvalidValue_InConversionMode_IsAnArgumentError()
    {
        var (code, _, stderr) = Run("--source", "any.accdb", "--output", P("x.db"), "--target", "oracle");

        Assert.Equal(CliRunner.ExitArgumentError, code);
        Assert.Contains("invalid value 'oracle' for --target", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Target_WithoutValue_IsAnArgumentError()
    {
        var (code, _, stderr) = Run("--schema-only", "--output", P("x.db"), "--target");

        Assert.Equal(CliRunner.ExitArgumentError, code);
        Assert.Contains("--target requires a value", stderr, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ options that do not apply

    [Theory]
    [InlineData("--release", "4.4", "--release")]
    [InlineData("--taxonomies", "T1", "--taxonomies")]
    [InlineData("--taxonomykeys", "K1", "--taxonomykeys")]
    [InlineData("--releases", "4.0,4.1", "--releases")]
    public void TargetNative_WithAnInapplicableOption_IsAnArgumentError_AndWritesNothing(string option, string value, string named)
    {
        var (code, stdout, stderr) = Run("--source", _fixture.Source?.Path ?? "any.accdb", "--output", P("n.db"), "--target", "native", option, value);

        Assert.Equal(CliRunner.ExitArgumentError, code);
        Assert.Contains($"{named} does not apply to --target native", stderr, StringComparison.Ordinal);
        Assert.False(File.Exists(P("n.db")));
        Assert.False(File.Exists(P("n.db.partial")));
        Assert.DoesNotContain("Detected source model", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void TargetNative_WithAll_IsAnArgumentError_AndWritesNothing()
    {
        var (code, _, stderr) = Run("--source", _fixture.Source?.Path ?? "any.accdb", "--output", P("n.db"), "--target", "native", "--all");

        Assert.Equal(CliRunner.ExitArgumentError, code);
        Assert.Contains("--all does not apply to --target native", stderr, StringComparison.Ordinal);
        Assert.False(File.Exists(P("n.db")));
    }

    [Fact]
    public void InapplicableOptionCheck_PositiveControl_WithoutTheOptionTheErrorIsAnother()
    {
        // The same command line with no offending option does not report "does not apply".
        var (code, _, stderr) = Run("--source", P("does-not-exist.accdb"), "--output", P("n.db"), "--target", "native");

        Assert.Equal(CliRunner.ExitArgumentError, code);
        Assert.DoesNotContain("does not apply", stderr, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(stderr));
    }

    [Fact]
    public void TargetNative_WithValidate_IsAnArgumentError()
    {
        var (code, _, stderr) = Run("--validate", "some.db", "--target", "native");

        Assert.Equal(CliRunner.ExitArgumentError, code);
        Assert.Contains("unrecognized argument in --validate mode: --target", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void TargetNative_WithExtractLayouts_IsAnArgumentError()
    {
        var (code, _, stderr) = Run("--extract-layouts", "some-dir", "--out", P("l.db"), "--dictionary", P("d.db"), "--target", "native");

        Assert.Equal(CliRunner.ExitArgumentError, code);
        Assert.Contains("unrecognized argument in --extract-layouts mode: --target", stderr, StringComparison.Ordinal);
        Assert.False(File.Exists(P("l.db")));
    }

    [Fact]
    public void TargetNative_WithListTaxonomies_IsAnArgumentError()
    {
        var (code, _, stderr) = Run("--list-taxonomies", "--source", _fixture.Source?.Path ?? "any.accdb", "--target", "native");

        Assert.Equal(CliRunner.ExitArgumentError, code);
        Assert.Contains("--target", stderr, StringComparison.Ordinal);
        Assert.Contains("unrecognized argument", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void TargetNative_WithoutSourceOrOutput_IsAnArgumentError()
    {
        var (codeNoSource, _, errNoSource) = Run("--output", P("n.db"), "--target", "native");
        var (codeNoOutput, _, errNoOutput) = Run("--source", "any.accdb", "--target", "native");

        Assert.Equal(CliRunner.ExitArgumentError, codeNoSource);
        Assert.Contains("--source", errNoSource, StringComparison.Ordinal);
        Assert.Equal(CliRunner.ExitArgumentError, codeNoOutput);
        Assert.Contains("--output", errNoOutput, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ conversion

    [AceFact]
    public void TargetNative_HappyPath_ExitsZero_AndWritesTheNativeFile()
    {
        var output = P("native.db");

        var (code, stdout, stderr) = Run("--source", _fixture.SourcePath, "--output", output, "--target", "native");

        Assert.Equal(CliRunner.ExitOk, code);
        Assert.Empty(stderr);
        Assert.Contains("Detected source model: DPM 2.0.", stdout, StringComparison.Ordinal);
        Assert.Contains("Native DPM 2.0 output written", stdout, StringComparison.Ordinal);
        Assert.Contains("Tables: 9.", stdout, StringComparison.Ordinal);
        Assert.True(File.Exists(output));
        Assert.False(File.Exists(output + ".partial"));
        using var sqlite = AccessSqliteComparer.OpenSqlite(output);
        Assert.Equal(9, AccessSqliteComparer.SqliteUserTables(sqlite).Count);
        Assert.Equal("ok", AccessSqliteComparer.ScalarText(sqlite, "PRAGMA integrity_check"));
    }

    [AceFact]
    public void TargetNative_ExistingOutput_WithoutOverwrite_IsAnError_AndWithOverwriteReplaced()
    {
        var output = P("exists.db");
        File.WriteAllText(output, "keep me");

        var (code, _, stderr) = Run("--source", _fixture.SourcePath, "--output", output, "--target", "native");

        Assert.Equal(CliRunner.ExitArgumentError, code);
        Assert.Contains("--overwrite", stderr, StringComparison.Ordinal);
        Assert.Equal("keep me", File.ReadAllText(output));

        var (code2, _, _) = Run("--source", _fixture.SourcePath, "--output", output, "--target", "native", "--overwrite");

        Assert.Equal(CliRunner.ExitOk, code2);
        using var sqlite = AccessSqliteComparer.OpenSqlite(output);
        Assert.Equal(9, AccessSqliteComparer.SqliteUserTables(sqlite).Count);
    }

    [AceFact]
    public void TargetNative_OutputIsADirectory_IsAnArgumentError()
    {
        var (code, _, stderr) = Run("--source", _fixture.SourcePath, "--output", _scratch, "--target", "native");

        Assert.Equal(CliRunner.ExitArgumentError, code);
        Assert.Contains("is a directory", stderr, StringComparison.Ordinal);
    }

    [AceFact]
    public void TargetNative_Dpm10Source_IsRejected_WithExitOne_AndNoFile()
    {
        var path = Dpm2TestAccdbFactory.CreateAccdbWithTables("Domain", "Category");
        try
        {
            var output = P("dpm1.db");

            var (code, _, stderr) = Run("--source", path, "--output", output, "--target", "native");

            Assert.Equal(CliRunner.ExitArgumentError, code);
            Assert.Contains("DPM 2.0", stderr, StringComparison.Ordinal);
            Assert.False(File.Exists(output));
            Assert.False(File.Exists(output + ".partial"));
        }
        finally
        {
            TempFiles.TryDeleteAccdb(path);
        }
    }

    [AceFact]
    public void TargetNative_SourceBelow44_IsRejected_WithExitOne_NamingTheCurrentRelease_AndNoFile()
    {
        using var source = SyntheticDpm2Accdb.Create(["4.2.1", "4.3"]);
        var output = P("below.db");

        var (code, _, stderr) = Run("--source", source.Path, "--output", output, "--target", "native");

        Assert.Equal(CliRunner.ExitArgumentError, code);
        Assert.Contains("4.4", stderr, StringComparison.Ordinal);
        Assert.Contains("'4.3'", stderr, StringComparison.Ordinal);
        Assert.False(File.Exists(output));
        Assert.False(File.Exists(output + ".partial"));
    }

    // ------------------------------------------------------------------ --schema-only

    [Fact]
    public void SchemaOnly_TargetNative_CreatesExactlyTheBaseline_59Tables()
    {
        var output = P("native-empty.db");

        var (code, stdout, stderr) = Run("--schema-only", "--output", output, "--target", "native");

        Assert.Equal(CliRunner.ExitOk, code);
        Assert.Empty(stderr);
        Assert.Contains("59 tables", stdout, StringComparison.Ordinal);

        var ddl = NativeSchemaBuilder.Build(NativeBaseline.Load().Catalog);
        var master = Master(output);
        var tables = master.Where(m => m.Type == "table").ToList();
        Assert.Equal(59, tables.Count);
        Assert.Equal(ddl.Tables.Select(t => t.Name).Order(StringComparer.Ordinal), tables.Select(t => t.Name).Order(StringComparer.Ordinal));
        // The text of every statement (tables and indexes) is what SQLite stored.
        Assert.Equal(
            ddl.Statements.Order(StringComparer.Ordinal),
            master.Select(m => m.Sql!).Order(StringComparer.Ordinal));

        using var sqlite = AccessSqliteComparer.OpenSqlite(output);
        foreach (var table in tables)
        {
            Assert.Equal(0, AccessSqliteComparer.ScalarLong(sqlite, $"SELECT COUNT(1) FROM {AccessSqliteComparer.Q(table.Name)}"));
        }
    }

    [Fact]
    public void SchemaOnly_TargetNative_PositiveControl_TheComparisonSeesAMissingOrExtraTable()
    {
        var output = P("control.db");
        Run("--schema-only", "--output", output, "--target", "native");

        using (var sqlite = AccessSqliteComparer.OpenSqlite(output, readOnly: false))
        {
            using var drop = sqlite.CreateCommand();
            drop.CommandText = "DROP TABLE \"Reference\"; CREATE TABLE \"ChangeLog\" (\"Id\" INTEGER)";
            drop.ExecuteNonQuery();
        }

        var ddl = NativeSchemaBuilder.Build(NativeBaseline.Load().Catalog);
        var names = Master(output).Where(m => m.Type == "table").Select(m => m.Name).ToList();

        Assert.NotEqual(ddl.Tables.Select(t => t.Name).Order(StringComparer.Ordinal), names.Order(StringComparer.Ordinal));
        Assert.Contains("ChangeLog", names);
        Assert.DoesNotContain("Reference", names);
    }

    [Fact]
    public void SchemaOnly_WithoutTarget_StillCreatesTheDistributionSchema_45ContractTablesPlusTheDeclaredExtension()
    {
        var output = P("default.db");

        var (code, stdout, _) = Run("--schema-only", "--output", output);

        Assert.Equal(CliRunner.ExitOk, code);
        // 45 tables of the contract + the declared extension table mOrdinateMetricRestriction
        // (docs/target-schema.md, "Extension tables"): 46 physical tables, unchanged by --target.
        Assert.Contains("46 tables", stdout, StringComparison.Ordinal);
        var tables = Master(output).Where(m => m.Type == "table").Select(m => m.Name).ToList();
        Assert.Equal(46, tables.Count);
        Assert.Contains("mOrdinateMetricRestriction", tables);
        Assert.Contains("mConcept", tables);
        Assert.DoesNotContain("Concept", tables); // none of the native tables
    }

    [Fact]
    public void SchemaOnly_TargetDistribution_EqualsTheDefault()
    {
        var implicitTarget = P("implicit.db");
        var explicitTarget = P("explicit.db");

        var (code1, out1, _) = Run("--schema-only", "--output", implicitTarget);
        var (code2, out2, _) = Run("--schema-only", "--output", explicitTarget, "--target", "distribution");

        Assert.Equal(CliRunner.ExitOk, code1);
        Assert.Equal(CliRunner.ExitOk, code2);
        Assert.Equal(Master(implicitTarget), Master(explicitTarget));
        Assert.Equal(
            out1.Replace(implicitTarget, "<out>", StringComparison.Ordinal),
            out2.Replace(explicitTarget, "<out>", StringComparison.Ordinal));
    }

    [Fact]
    public void SchemaOnly_DistributionAndNative_DifferByTheirTableSets()
    {
        // Positive control of the two comparisons above: a different target gives a different schema.
        var dist = P("dist.db");
        var native = P("nat.db");
        Run("--schema-only", "--output", dist);
        Run("--schema-only", "--output", native, "--target", "native");

        Assert.NotEqual(Master(dist), Master(native));
    }

    [Fact]
    public void SchemaOnly_TargetNative_ExistingFile_NeedsOverwrite()
    {
        var output = P("again.db");
        Run("--schema-only", "--output", output, "--target", "native");
        var before = File.ReadAllBytes(output);

        var (code, _, stderr) = Run("--schema-only", "--output", output, "--target", "native");
        var (codeOverwrite, _, _) = Run("--schema-only", "--output", output, "--target", "native", "--overwrite");

        Assert.Equal(CliRunner.ExitArgumentError, code);
        Assert.Contains("--overwrite", stderr, StringComparison.Ordinal);
        Assert.Equal(CliRunner.ExitOk, codeOverwrite);
        Assert.Equal(59, Master(output).Count(m => m.Type == "table"));
        Assert.NotEmpty(before);
    }

    // ------------------------------------------------------------------ help

    [Fact]
    public void Help_ListsTheTargetOption_AndItsValues()
    {
        var (code, stdout, _) = Run("--help");

        Assert.Equal(CliRunner.ExitOk, code);
        Assert.Contains("--target", stdout, StringComparison.Ordinal);
        Assert.Contains("native", stdout, StringComparison.Ordinal);
        Assert.Contains("distribution", stdout, StringComparison.Ordinal);
    }
}
