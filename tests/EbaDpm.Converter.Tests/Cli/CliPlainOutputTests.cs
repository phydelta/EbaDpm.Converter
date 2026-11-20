using System.Reflection;
using EbaDpm.Converter.Cli;
using EbaDpm.Converter.Tests.Dpm2;
using EbaDpm.Converter.Tests.Infrastructure;
using EbaDpm.Converter.Tests.Schema;
using Microsoft.Data.Sqlite;
using Spectre.Console;

namespace EbaDpm.Converter.Tests.Cli;

/// <summary>
/// #76 (Spectre.Console): output through the <see cref="TextWriter"/> overload of
/// <see cref="CliRunner.Run(string[], TextWriter, TextWriter)"/> is always plain (no ESC), the
/// <c>--plain</c> flag and <c>NO_COLOR</c> select the plain console, value-less options are argument
/// errors, and an unexpected exception ends with exit code 1 and a labelled message.
/// <c>CliConsole</c> is <c>internal</c> to the CLI assembly (no <c>InternalsVisibleTo</c>, and tests
/// do not touch <c>src/</c>), so the rich-console cases reach it by reflection.
/// </summary>
[Trait("Tier", "Synthetic")]
[Collection("CliEnvironment")]
public sealed class CliPlainOutputTests : IDisposable
{
    private const char Esc = '\u001b';

    /// <summary>An SGR sequence that sets a foreground or background colour.</summary>
    private const string ColourSequence = @"\u001b\[(?:[0-9]+;)*(?:3[0-8]|4[0-8]|9[0-7]|10[0-7])(?:;[0-9]+)*m";

    private readonly string _directory = Path.Join(Path.GetTempPath(), $"EbaDpm.CliPlain_{Environment.ProcessId}_{Guid.NewGuid():N}");

    public CliPlainOutputTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup of a temp directory: a locked file must not mask the test verdict.
        }
    }

    private static (int Exit, string Stdout, string Stderr) Run(params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = CliRunner.Run(args, stdout, stderr);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    // ------------------------------------------------------------------
    // Reflection over the internal console type
    // ------------------------------------------------------------------

    private static readonly Assembly CliAssembly = typeof(CliRunner).Assembly;

    private static Type ConsoleType => CliAssembly.GetType("EbaDpm.Converter.Cli.CliConsole", throwOnError: true)!;

    private static object Detect(TextWriter writer, bool isRedirected)
        => ConsoleType.GetMethod("Detect", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [writer, isRedirected])!;

    private static bool IsRich(object console)
        => (bool)ConsoleType.GetProperty("Rich", BindingFlags.Public | BindingFlags.Instance)!.GetValue(console)!;

    /// <summary>A console that is forced to emit ANSI (the positive control: it does colour).</summary>
    private static object ForcedAnsi(TextWriter writer)
    {
        var ansi = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.Yes,
            ColorSystem = ColorSystemSupport.TrueColor,
            Interactive = InteractionSupport.No,
            Out = new AnsiConsoleOutput(writer),
        });
        var constructor = ConsoleType.GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single();
        return constructor.Invoke([ansi, writer, true]);
    }

    private static int RunOnConsoles(string[] args, object stdout, object stderr)
    {
        var run = typeof(CliRunner).GetMethod(
            "Run", BindingFlags.NonPublic | BindingFlags.Static, null, [typeof(string[]), ConsoleType, ConsoleType], null)!;
        return (int)run.Invoke(null, [args, stdout, stderr])!;
    }

    /// <summary>
    /// Runs <paramref name="body"/> with a deterministic terminal environment and restores it afterwards.
    /// <c>CliConsole.Detect</c> lets Spectre read the process environment: <c>TERM</c> decides whether the
    /// stream is an ANSI terminal (without it Spectre falls back to plain on a headless shell, and the
    /// non-redirected cases would pass or fail by the shell they run in), and <c>NO_COLOR</c> selects the
    /// colour system. Both are set here; <paramref name="noColor"/> null means unset.
    /// </summary>
    private static string WithEnvironment(string? noColor, Func<string> body)
    {
        var previous = new[] { "NO_COLOR", "TERM" }.ToDictionary(n => n, Environment.GetEnvironmentVariable);
        Environment.SetEnvironmentVariable("NO_COLOR", noColor);
        Environment.SetEnvironmentVariable("TERM", "xterm-256color");
        try
        {
            return body();
        }
        finally
        {
            foreach (var (name, value) in previous)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }

    // ------------------------------------------------------------------
    // (a) No ESC through the TextWriter overload
    // ------------------------------------------------------------------

    [Fact]
    public void Help_ThroughTheWriterOverload_HasNoEscape_AndShowsTheSpectreSections()
    {
        var (exit, stdout, stderr) = Run("--help");

        Assert.Equal(CliRunner.ExitOk, exit);
        Assert.DoesNotContain(Esc, stdout);
        Assert.Empty(stderr);
        Assert.Contains("USAGE:", stdout, StringComparison.Ordinal);
        Assert.Contains("EXAMPLES:", stdout, StringComparison.Ordinal);
        Assert.Contains("OPTIONS:", stdout, StringComparison.Ordinal);
        // The option row of the OPTIONS section (the EXAMPLES section writes the lower-case <code>).
        Assert.Contains("--release <CODE>", stdout, StringComparison.Ordinal);
        Assert.Contains("--plain", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void NoArguments_ThroughTheWriterOverload_ErrorLineHintAndHelpGoToStderr_WithoutEscape()
    {
        var (exit, stdout, stderr) = Run();

        Assert.Equal(CliRunner.ExitArgumentError, exit);
        Assert.Empty(stdout);
        Assert.DoesNotContain(Esc, stderr);
        Assert.StartsWith("Error: no arguments given.", stderr, StringComparison.Ordinal);
        Assert.Contains("Use --help to see the usage.", stderr, StringComparison.Ordinal);
        Assert.Contains("USAGE:", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void ArgumentError_PrintsTheMessageAndTheHint_NotTheUsageDump()
    {
        var (exit, stdout, stderr) = Run("--source", "x.accdb", "--unknown-option");

        Assert.Equal(CliRunner.ExitArgumentError, exit);
        Assert.Empty(stdout);
        Assert.DoesNotContain(Esc, stderr);
        Assert.Contains("unrecognized argument", stderr, StringComparison.Ordinal);
        Assert.Contains("--unknown-option", stderr, StringComparison.Ordinal);
        Assert.Contains("Use --help to see the usage.", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("USAGE:", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void SchemaOnly_ConversionSummary_ThroughTheWriterOverload_HasNoEscape_AndWritesTheFile()
    {
        var output = Path.Join(_directory, "schema.db");

        var (exit, stdout, stderr) = Run("--schema-only", "--output", output);

        Assert.Equal(CliRunner.ExitOk, exit);
        Assert.True(File.Exists(output));
        Assert.DoesNotContain(Esc, stdout);
        Assert.DoesNotContain(Esc, stderr);
        Assert.NotEmpty(stdout);
    }

    [Fact]
    public void Validate_ThroughTheWriterOverload_HasNoEscape_AndKeepsTheResultLines()
    {
        var generated = Path.Join(_directory, "to-validate.db");
        Assert.Equal(CliRunner.ExitOk, Run("--schema-only", "--output", generated).Exit);
        SqliteConnection.ClearAllPools(); // in-process only: the writer's pooled connection still holds the file

        var (exit, stdout, stderr) = Run("--validate", generated);

        Assert.True(exit is CliRunner.ExitOk or CliRunner.ExitValidationCriticalDifferences, $"exit={exit}; stderr: {stderr}");
        Assert.DoesNotContain(Esc, stdout);
        Assert.DoesNotContain(Esc, stderr);
        Assert.Contains("RESULT", stdout, StringComparison.Ordinal);
        Assert.Contains("plane C: not evaluated, --layouts missing.", stdout, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // (b) --plain and NO_COLOR select the plain console; forced ANSI does colour (positive control)
    // ------------------------------------------------------------------

    [Fact]
    public void ForcedAnsiConsole_EmitsEscapeSequences_PositiveControlOfTheNoEscapeChecks()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exit = RunOnConsoles(["--help"], ForcedAnsi(stdout), ForcedAnsi(stderr));

        Assert.Equal(CliRunner.ExitOk, exit);
        Assert.Contains(Esc, stdout.ToString());
    }

    [Fact]
    public void ForcedAnsiConsole_ErrorLabelIsColoured_WhenNotPlain()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exit = RunOnConsoles(["--source", "x.accdb", "--unknown-option"], ForcedAnsi(stdout), ForcedAnsi(stderr));

        Assert.Equal(CliRunner.ExitArgumentError, exit);
        Assert.Contains(Esc, stderr.ToString());
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("--schema-only")]
    public void PlainFlag_OnAForcedAnsiConsole_SelectsPlainOutput(string mode)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var args = mode == "--help"
            ? new[] { "--help", "--plain" }
            : ["--schema-only", "--output", Path.Join(_directory, "plain-flag.db"), "--plain"];

        var exit = RunOnConsoles(args, ForcedAnsi(stdout), ForcedAnsi(stderr));

        Assert.Equal(CliRunner.ExitOk, exit);
        Assert.NotEmpty(stdout.ToString());
        Assert.DoesNotContain(Esc, stdout.ToString());
        Assert.DoesNotContain(Esc, stderr.ToString());
    }

    [Fact]
    public void PlainFlag_ErrorOnAForcedAnsiConsole_IsPlain()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exit = RunOnConsoles(["--source", "x.accdb", "--unknown-option", "--plain"], ForcedAnsi(stdout), ForcedAnsi(stderr));

        Assert.Equal(CliRunner.ExitArgumentError, exit);
        Assert.DoesNotContain(Esc, stderr.ToString());
        Assert.Contains("Error: unrecognized argument", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Detect_WhenRedirected_IsPlain()
    {
        using var writer = new StringWriter();

        Assert.False(IsRich(Detect(writer, isRedirected: true)));
    }

    [Fact]
    public void Detect_OnANonRedirectedWriter_WithoutNoColor_IsRich_AndColoursTheErrorLabel()
    {
        // Positive control of the NO_COLOR test below: the same detection, NO_COLOR unset, does colour.
        var text = WithEnvironment(noColor: null, () =>
        {
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            var console = Detect(stderr, isRedirected: false);
            Assert.True(IsRich(console), "A non-redirected writer is expected to be detected as an ANSI terminal here.");
            RunOnConsoles(["--source", "x.accdb", "--unknown-option"], Detect(stdout, isRedirected: false), console);
            return stderr.ToString();
        });

        Assert.Contains(Esc, text);
    }

    [Fact]
    public void ColourSequencePattern_MatchesTheColouredErrorLabel_PositiveControl()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        RunOnConsoles(["--source", "x.accdb", "--unknown-option"], ForcedAnsi(stdout), ForcedAnsi(stderr));

        Assert.Matches(ColourSequence, stderr.ToString());
    }

    [Theory]
    [InlineData("1")]
    [InlineData("anything")]
    public void Detect_WithNoColor_WritesNoColourSequence(string noColor)
    {
        // The no-color.org reading of NO_COLOR: no colour codes (SGR 30-37, 38, 40-47, 48, 90-97, 100-107).
        // Bold and other attributes are allowed by that convention; --plain is the switch for fully
        // plain output (see PlainFlag_* tests).
        var text = WithEnvironment(noColor, () =>
        {
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            var console = Detect(stderr, isRedirected: false);
            // Still an ANSI terminal: it is NO_COLOR, not a plain fallback, that removes the colours.
            Assert.True(IsRich(console), "NO_COLOR keeps the terminal features: the console must still be rich.");
            RunOnConsoles(["--source", "x.accdb", "--unknown-option"], Detect(stdout, isRedirected: false), console);
            return stderr.ToString();
        });

        Assert.DoesNotMatch(ColourSequence, text);
        Assert.Contains("Error:", text, StringComparison.Ordinal);
        Assert.Contains("unrecognized argument", text, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // (c) Options without a value
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("--taxonomies")]
    [InlineData("--taxonomykeys")]
    [InlineData("--releases")]
    [InlineData("--report")]
    [InlineData("--release")]
    [InlineData("--source")]
    [InlineData("--output")]
    [InlineData("--validate")]
    [InlineData("--reference")]
    [InlineData("--layouts")]
    [InlineData("--extract-layouts")]
    [InlineData("--out")]
    [InlineData("--dictionary")]
    public void ValueOption_AtTheEndOfTheArguments_IsAnArgumentError(string option)
    {
        var (exit, stdout, stderr) = Run("--source", "does-not-exist.accdb", option);

        Assert.Equal(CliRunner.ExitArgumentError, exit);
        Assert.Empty(stdout);
        Assert.Contains($"argument {option} requires a value.", stderr, StringComparison.Ordinal);
        Assert.Contains("Use --help to see the usage.", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(Esc, stderr);
    }

    [Theory]
    [InlineData("--taxonomies")]
    [InlineData("--taxonomykeys")]
    [InlineData("--releases")]
    [InlineData("--report")]
    public void ValueOption_FollowedByAnotherOption_IsAnArgumentError(string option)
    {
        var (exit, _, stderr) = Run("--source", "does-not-exist.accdb", option, "--overwrite");

        Assert.Equal(CliRunner.ExitArgumentError, exit);
        Assert.Contains($"argument {option} requires a value.", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void ValueOption_WithAValue_IsNotAValueError_PositiveControlOfTheValueChecks()
    {
        // Same shape with a value: it is NOT reported as missing a value (it fails later, on the source).
        var (exit, _, stderr) = Run("--source", "does-not-exist.accdb", "--output", "o.db", "--taxonomies", "COREP 3.2");

        Assert.Equal(CliRunner.ExitArgumentError, exit);
        Assert.DoesNotContain("requires a value", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void RepeatedOption_LastValueWins()
    {
        var first = Path.Join(_directory, "first.db");
        var last = Path.Join(_directory, "last.db");

        var (exit, _, stderr) = Run("--schema-only", "--output", first, "--output", last);

        Assert.True(exit == CliRunner.ExitOk, $"exit={exit}; stderr: {stderr}");
        Assert.True(File.Exists(last), "The last --output must be the one written.");
        Assert.False(File.Exists(first), "The first --output must be ignored.");
    }

    // ------------------------------------------------------------------
    // (d) Unexpected exceptions
    // ------------------------------------------------------------------

    /// <summary>A writer that fails with an exception no code path of the CLI anticipates.</summary>
    private sealed class FailingWriter : StringWriter
    {
        public override void Write(char value) => throw new NotSupportedException("synthetic writer failure");

        public override void Write(string? value) => throw new NotSupportedException("synthetic writer failure");

        public override void Write(char[] buffer, int index, int count) => throw new NotSupportedException("synthetic writer failure");

        public override void WriteLine(string? value) => throw new NotSupportedException("synthetic writer failure");

        public override void WriteLine() => throw new NotSupportedException("synthetic writer failure");
    }

    [Fact]
    public void UnexpectedException_IsReportedOnStderrWithItsTypeAndMessage_AndEndsWithExitCode1()
    {
        using var stdout = new FailingWriter();
        using var stderr = new StringWriter();

        var exit = CliRunner.Run(["--schema-only", "--output", Path.Join(_directory, "unexpected.db")], stdout, stderr);

        Assert.Equal(CliRunner.ExitArgumentError, exit);
        Assert.Contains("Error: unexpected failure (NotSupportedException): synthetic writer failure", stderr.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Esc, stderr.ToString());
        // The details: the exception in full (type, message and stack).
        Assert.Contains("System.NotSupportedException", stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains(" at ", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ExpectedFailure_IsNotLabelledAsUnexpected_PositiveControlOfTheTestAbove()
    {
        var (exit, _, stderr) = Run("--source", "x.accdb", "--unknown-option");

        Assert.Equal(CliRunner.ExitArgumentError, exit);
        Assert.DoesNotContain("unexpected failure", stderr, StringComparison.Ordinal);
    }
}

/// <summary>
/// Real data (#76): a DPM 2.0 conversion and the real <c>--list-taxonomies</c> over the plain writer
/// overload leave no ESC; the conversion is the one the suite already runs (<see cref="Dpm20SkeletonFixture"/>).
/// </summary>
[Collection("Dpm2Skeleton")]
[Trait("Tier", "RealData")]
public sealed class CliPlainOutputDpm20DataTests(Dpm20SkeletonFixture fixture)
{
    private const char Esc = '\u001b';

    [DataFact]
    public void Conversion_Stdout_HasNoEscape_PhaseLinesAndTheSummaryTable()
    {
        Assert.Equal(0, fixture.ExitCode);
        Assert.DoesNotContain(Esc, fixture.Stdout);
        Assert.DoesNotContain(Esc, fixture.Stderr);
        // Phase lines such as "  [   5.0 s] Dictionary".
        Assert.Matches(@"\[\s*\d+(\.\d+)? s\] \S", fixture.Stdout);
        // Summary table header columns.
        Assert.Contains("Area", fixture.Stdout, StringComparison.Ordinal);
        Assert.Contains("Item", fixture.Stdout, StringComparison.Ordinal);
        Assert.Contains("Rows", fixture.Stdout, StringComparison.Ordinal);
        Assert.Contains("Note", fixture.Stdout, StringComparison.Ordinal);
    }

    [DataFact]
    public void ListTaxonomies_OnTheRealSource_HasNoEscape()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exit = CliRunner.Run(["--list-taxonomies", "--source", RepoPaths.AccessDpm20DatabasePath], stdout, stderr);

        Assert.Equal(CliRunner.ExitOk, exit);
        Assert.DoesNotContain(Esc, stdout.ToString());
        Assert.Contains("Total: ", stdout.ToString(), StringComparison.Ordinal);
    }
}
