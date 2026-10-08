using EbaDpm.Converter.Cli;
using EbaDpm.Converter.Tests.Dpm2;
using EbaDpm.Converter.Tests.Schema;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Tests.Cli;

/// <summary>Behaviour of the <c>--cutoff-release</c> option (DPM 2.0 sources only).</summary>
public sealed class CliCutoffReleaseTests
{
    private const string AvailableCodes = "Available release codes: 3.4, 3.5, 4.0, 4.1, 4.2, 4.2.1.";

    [Theory]
    [InlineData("convert")]
    [InlineData("list")]
    public void CutoffRelease_WithoutValue_IsAnArgumentError(string mode)
    {
        var args = mode == "convert"
            ? new[] { "--source", "does-not-exist.accdb", "--output", "out.db", "--all", "--cutoff-release" }
            : new[] { "--list-taxonomies", "--source", "does-not-exist.accdb", "--cutoff-release" };
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = CliRunner.Run(args, stdout, stderr);

        Assert.Equal(CliRunner.ExitArgumentError, exitCode);
        Assert.Contains("argument --cutoff-release requires a value.", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void CutoffRelease_FollowedByAnotherOption_IsAnArgumentError()
    {
        var stderr = new StringWriter();

        var exitCode = CliRunner.Run(
            ["--source", "does-not-exist.accdb", "--output", "out.db", "--cutoff-release", "--all"], new StringWriter(), stderr);

        Assert.Equal(CliRunner.ExitArgumentError, exitCode);
        Assert.Contains("argument --cutoff-release requires a value.", stderr.ToString(), StringComparison.Ordinal);
    }

    [DataFact]
    public void ListTaxonomies_WithUnknownCutoffCode_IsAnArgumentError_ListingTheAvailableCodes()
    {
        RepoPaths.EnsureAccessDpm20DatabaseExists();
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = CliRunner.Run(
            ["--list-taxonomies", "--source", RepoPaths.AccessDpm20DatabasePath, "--cutoff-release", "9.9"], stdout, stderr);

        Assert.Equal(CliRunner.ExitArgumentError, exitCode);
        Assert.Contains("(Code='9.9') does not exist in [Release]", stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains(AvailableCodes, stderr.ToString(), StringComparison.Ordinal);
    }

    [DataFact]
    public void Convert_WithUnknownCutoffCode_IsAnArgumentError_ListingTheAvailableCodes()
    {
        RepoPaths.EnsureAccessDpm20DatabaseExists();
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"EbaDpm.CliCutoffTests_{Environment.ProcessId}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exitCode = CliRunner.Run(
                ["--source", RepoPaths.AccessDpm20DatabasePath, "--output", Path.Combine(tempDirectory, "o.db"), "--all", "--cutoff-release", "9.9"],
                stdout, stderr);

            Assert.Equal(CliRunner.ExitArgumentError, exitCode);
            Assert.Contains(AvailableCodes, stderr.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(tempDirectory, recursive: true); } catch (IOException) { }
        }
    }

    [DataFact]
    public void ListTaxonomies_WithRequestedCutoff_ShowsItInTheLogLine()
    {
        RepoPaths.EnsureAccessDpm20DatabaseExists();
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = CliRunner.Run(
            ["--list-taxonomies", "--source", RepoPaths.AccessDpm20DatabasePath, "--cutoff-release", "4.2"], stdout, stderr);

        Assert.Equal(CliRunner.ExitOk, exitCode);
        Assert.Contains("(cutoff release 4.2, requested)", stdout.ToString(), StringComparison.Ordinal);
    }

    [DataFact]
    public void ListTaxonomies_WithoutCutoff_ShowsTheNaturalCutoffWithoutTheRequestedMarker()
    {
        RepoPaths.EnsureAccessDpm20DatabaseExists();
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = CliRunner.Run(
            ["--list-taxonomies", "--source", RepoPaths.AccessDpm20DatabasePath], stdout, stderr);

        Assert.Equal(CliRunner.ExitOk, exitCode);
        Assert.Contains("(cutoff release 4.2.1)", stdout.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("requested", stdout.ToString(), StringComparison.Ordinal);
    }

    [DataFact]
    public void ListTaxonomies_OnDpm10Source_WithCutoff_IsAnArgumentError()
    {
        RepoPaths.EnsureAccessDatabaseExists();
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = CliRunner.Run(
            ["--list-taxonomies", "--source", RepoPaths.AccessDatabasePath, "--cutoff-release", "4.2"], stdout, stderr);

        Assert.Equal(CliRunner.ExitArgumentError, exitCode);
        Assert.Contains("--cutoff-release only applies to DPM 2.0 sources.", stderr.ToString(), StringComparison.Ordinal);
    }

    [DataFact]
    public void Convert_OnDpm10Source_WithCutoff_IsAnArgumentError_AndCreatesNoOutput()
    {
        RepoPaths.EnsureAccessDatabaseExists();
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"EbaDpm.CliCutoffTests_{Environment.ProcessId}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        var outputPath = Path.Combine(tempDirectory, "o.db");
        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exitCode = CliRunner.Run(
                ["--source", RepoPaths.AccessDatabasePath, "--output", outputPath, "--taxonomies", "COREP 3.2", "--cutoff-release", "4.2"],
                stdout, stderr);

            Assert.Equal(CliRunner.ExitArgumentError, exitCode);
            Assert.Contains("--cutoff-release only applies to DPM 2.0 sources.", stderr.ToString(), StringComparison.Ordinal);
            Assert.False(File.Exists(outputPath), "A rejected --cutoff-release must not create the output file.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(tempDirectory, recursive: true); } catch (IOException) { }
        }
    }
}

/// <summary>
/// The convert log line of the 4.2-cutoff conversion the suite already runs
/// (<see cref="Dpm20SkeletonFixture"/>, <c>--cutoff-release 4.2</c>): no extra conversion.
/// </summary>
[Collection("Dpm2Skeleton")]
[Trait("Tier", "RealData")]
public sealed class CliCutoffReleaseConvertLogTests(Dpm20SkeletonFixture fixture)
{
    [DataFact]
    public void Convert_WithRequestedCutoff_ShowsItInTheLogLine_AndTheOutputIsThatRelease()
    {
        Assert.Equal(0, fixture.ExitCode);
        Assert.Contains("Cutoff release: 4.2 (requested).", fixture.Stdout, StringComparison.Ordinal);

        using var command = fixture.GeneratedConnection.CreateCommand();
        command.CommandText = "SELECT COUNT(1) FROM mRelease WHERE IsCurrent = 1 AND ReleaseCode = '4.2'";
        Assert.Equal(1L, (long)command.ExecuteScalar()!);
    }
}
