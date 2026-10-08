using EbaDpm.Converter.Cli;
using EbaDpm.Converter.Core.Sqlite;
using EbaDpm.Converter.Core.Validation;

namespace EbaDpm.Converter.Tests.PlaneC;

/// <summary>
/// <c>--layouts</c> is OPTIONAL -- without it, <c>--validate</c> gives EXACTLY what it gave before
/// and ANNOUNCES it explicitly (a plane that does not run and does not announce itself is the
/// empty zero). It is checked in BOTH directions: without the option (plane C absent, behaviour
/// intact) and with it (plane C present, the rest UNCHANGED down to the last field).
///
/// It uses a schema-only generated database (<see cref="SchemaCreator"/>, no Access/ACE OLEDB
/// involved) so that these tests are fast: the property being checked -- "with or without
/// --layouts, the rest of the report is identical" -- does not depend on how much data is inside.
/// </summary>
public sealed class PlaneCValidateFlagTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly string _generatedPath;
    private readonly string _emptyLayoutsPath;

    public PlaneCValidateFlagTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"EbaDpm.PlaneCFlagTests_{Environment.ProcessId}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);

        _generatedPath = Path.Combine(_tempDirectory, "generated-schema-only.db");
        SchemaCreator.Create(_generatedPath, overwrite: false);

        _emptyLayoutsPath = Path.Combine(_tempDirectory, "layouts-empty.db");
        CreateEmptyLayoutsRepository(_emptyLayoutsPath);

        // Microsoft.Data.Sqlite keeps the native handle in the connection POOL even though each
        // individual connection has been closed -- Validator.Run needs an exclusive File.OpenRead
        // for the SHA-256 and for copying the file (same pattern as Dpm20ValidateFixture).
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort.
        }
    }

    [Fact]
    public void Validate_WithoutLayouts_AnnouncesThatPlaneCWasNotEvaluated_AndThereAreNoPlaneCChecks()
    {
        var result = Validator.Run(_generatedPath, referencePath: null, layoutsPath: null);

        Assert.Equal("plane C: not evaluated, --layouts missing.", result.Report.PlaneCNotice);
        Assert.DoesNotContain(result.Report.Checks, c => c.Plane == "C");
    }

    [Fact]
    public void Validate_WithLayouts_AnnouncesThatItWasEvaluated_AndThereArePlaneCChecks()
    {
        var result = Validator.Run(_generatedPath, referencePath: null, layoutsPath: _emptyLayoutsPath);

        Assert.NotNull(result.Report.PlaneCNotice);
        Assert.StartsWith("plane C: evaluated against", result.Report.PlaneCNotice);
        Assert.Contains(Path.GetFileName(_emptyLayoutsPath), result.Report.PlaneCNotice);

        var planeCChecks = result.Report.Checks.Where(c => c.Plane == "C").Select(c => c.Id).ToList();
        Assert.Contains("C-DPS-01", planeCChecks);
        Assert.Contains("C-DPS-02", planeCChecks);
        Assert.Contains("C-COB-01", planeCChecks);
        Assert.Contains("C-CLS-01", planeCChecks);
        Assert.Contains("C-REL-01", planeCChecks);

        // Coverage is ALWAYS announced, even with an empty universe (empty layout repository -> 0
        // tables compared, but the check is still present and Info).
        var coverage = Assert.Single(result.Report.Checks, c => c.Id == "C-COB-01");
        Assert.Equal("info", coverage.Status);
    }

    /// <summary>
    /// The heart of the matter: comparing the SAME generated database, with and without
    /// <c>--layouts</c>, everything that is NOT plane C has to be byte for byte equal -- same Id,
    /// Plane, Layer, Status, Examined/Matched/Failed and samples. If anything changed,
    /// <c>--layouts</c> would have stopped being independent and would be contaminating plane A/B.
    /// </summary>
    [Fact]
    public void Validate_WithAndWithoutLayouts_TheRestOfTheReportIsIdentical()
    {
        var withoutLayouts = Validator.Run(_generatedPath, referencePath: null, layoutsPath: null);
        var withLayouts = Validator.Run(_generatedPath, referencePath: null, layoutsPath: _emptyLayoutsPath);

        var checksWithoutLayouts = withoutLayouts.Report.Checks.Where(c => c.Plane != "C").Select(Fingerprint).ToList();
        var checksWithLayouts = withLayouts.Report.Checks.Where(c => c.Plane != "C").Select(Fingerprint).ToList();

        Assert.Equal(checksWithoutLayouts, checksWithLayouts);

        // The registered exceptions (they are ALWAYS enumerated) do not change either, apart from
        // the plane C known-divergence census (which is only evaluated -- Evaluated=true -- when
        // plane C runs).
        var exceptionsWithoutLayouts = withoutLayouts.Report.Exceptions.Where(e => e.Id != "DD-23").Select(ExceptionFingerprint).ToList();
        var exceptionsWithLayouts = withLayouts.Report.Exceptions.Where(e => e.Id != "DD-23").Select(ExceptionFingerprint).ToList();
        Assert.Equal(exceptionsWithoutLayouts, exceptionsWithLayouts);

        // The critical/informational summary OUTSIDE plane C does not change -- regardless of
        // whether the schema-only database (no data) makes some plane A invariant fail on its own
        // (not the business of --layouts): what is checked is the EQUALITY between the two
        // summaries, not that neither fails. C-DPS-01 over an EMPTY layout repository adds no new
        // failure (Examined=0 -> Pass), so the critical total has to match.
        Assert.Equal(withoutLayouts.Report.Summary.Critical.Failed, withLayouts.Report.Summary.Critical.Failed - CriticalFailuresIntroducedByPlaneC(withLayouts.Report));
        Assert.Equal(withLayouts.ExitCode, withoutLayouts.ExitCode);
    }

    /// <summary>Counts how many CRITICAL plane C checks fail -- so that this number can be subtracted when comparing the rest of the report without assuming it is 0.</summary>
    private static long CriticalFailuresIntroducedByPlaneC(ValidationReport report) =>
        report.Checks.Count(c => c.Plane == "C" && c.Layer == "critical" && c.Status == "fail");

    [Fact]
    public void Cli_WithANonExistentLayouts_FailsWithAnExplicitMessage()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = CliRunner.Run(
            ["--validate", _generatedPath, "--layouts", Path.Combine(_tempDirectory, "does-not-exist.db")],
            stdout, stderr);

        Assert.Equal(CliRunner.ExitArgumentError, exitCode);
        Assert.Contains("layout repository", stderr.ToString());
        Assert.Contains("does not exist", stderr.ToString());
    }

    [Fact]
    public void Cli_WithValidLayouts_PrintsPlaneC()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        // Without requiring a specific exit code: the schema-only database may make some plane A
        // invariant fail that has nothing to do with --layouts. What is checked is the printed TEXT.
        CliRunner.Run(["--validate", _generatedPath, "--layouts", _emptyLayoutsPath], stdout, stderr);

        Assert.Contains("plane C: evaluated against", stdout.ToString());
        Assert.Contains("PLANE C", stdout.ToString());
    }

    [Fact]
    public void Cli_WithoutLayouts_PrintsThatItWasNotEvaluated_AndDoesNotPrintThePlaneCHeader()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        CliRunner.Run(["--validate", _generatedPath], stdout, stderr);

        Assert.Contains("plane C: not evaluated, --layouts missing.", stdout.ToString());
        Assert.DoesNotContain("PLANE C -", stdout.ToString());
    }

    /// <summary>
    /// The CLI version of the heart of the matter: the same `--validate` on the command line, with
    /// and without `--layouts`, produces the SAME exit code -- an empty layout repository
    /// introduces no new critical failure that changes the verdict.
    /// </summary>
    [Fact]
    public void Cli_WithAndWithoutLayouts_ReturnsTheSameExitCode()
    {
        var stdoutWithout = new StringWriter();
        var exitCodeWithout = CliRunner.Run(["--validate", _generatedPath], stdoutWithout, new StringWriter());

        var stdoutWith = new StringWriter();
        var exitCodeWith = CliRunner.Run(["--validate", _generatedPath, "--layouts", _emptyLayoutsPath], stdoutWith, new StringWriter());

        Assert.Equal(exitCodeWithout, exitCodeWith);
    }

    [Fact]
    public void Help_MentionsTheLayoutsOption()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        CliRunner.Run(["--help"], stdout, stderr);

        Assert.Contains("--layouts", stdout.ToString());
    }

    private static (string Id, string Plane, string Layer, string Table, string Status, long Examined, long Matched, long Failed, string Samples) Fingerprint(CheckInfo c) =>
        (c.Id, c.Plane, c.Layer, c.Table, c.Status, c.Examined, c.Matched, c.Failed,
         string.Join(";", c.Samples.Select(s => $"{s.BusinessKey}|{s.Generated}|{s.Reference}")));

    private static (string Id, string BusinessKey, string Scope, bool AppliesToThisReference, long Matched, bool Stale, bool Evaluated) ExceptionFingerprint(ExceptionInfo e) =>
        (e.Id, e.BusinessKey, e.Scope, e.AppliesToThisReference, e.Matched, e.Stale, e.Evaluated);

    private static void CreateEmptyLayoutsRepository(string path)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = path }.ToString());
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            CREATE TABLE "LayoutFile" ("FileId" INTEGER PRIMARY KEY, "FrameworkCode" TEXT, "ReleaseLabel" TEXT);
            CREATE TABLE "LayoutSheet" ("SheetId" INTEGER PRIMARY KEY, "FileId" INTEGER, "TableCode" TEXT);
            CREATE TABLE "LayoutDeclaration" (
                "DeclId" INTEGER PRIMARY KEY, "SheetId" INTEGER, "DimensionCode" TEXT, "DomainCode" TEXT, "IsKey" INTEGER);
            CREATE TABLE "LayoutValue" (
                "ValueId" INTEGER PRIMARY KEY, "SheetId" INTEGER, "DeclId" INTEGER, "OrdinateId" INTEGER, "MemberCode" TEXT);
            CREATE TABLE "LayoutCell" (
                "CellId" INTEGER PRIMARY KEY, "SheetId" INTEGER, "RowOrdinateId" INTEGER,
                "ColumnOrdinateId" INTEGER, "IsShaded" INTEGER);
            """;
        cmd.ExecuteNonQuery();
    }
}
