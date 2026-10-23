using EbaDpm.Converter.Cli;
using EbaDpm.Converter.Tests.Infrastructure;
using EbaDpm.Converter.Tests.Schema;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Tests.Dpm2;

/// <summary>
/// End-to-end behaviour of the CLI over a DPM 2.0 source (<see cref="CliRunner.Run"/>).
/// </summary>
public sealed class Dpm2CliTests
{
    [DataFact]
    public void ListTaxonomies_OnDpm20Source_Exits0_AndReportsThePinnedTaxonomyCount()
    {
        RepoPaths.EnsureAccessDpm20DatabaseExists();

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = CliRunner.Run(
            ["--list-taxonomies", "--source", RepoPaths.AccessDpm20DatabasePath, "--cutoff-release", RepoPaths.Cutoff42ReleaseCode], stdout, stderr);

        Assert.Equal(CliRunner.ExitOk, exitCode);
        Assert.Empty(stderr.ToString());
        var expected = $"Total: {ReleasePins.Dpm20Cutoff42MTaxonomyRows} taxonomies.";
        Assert.True(
            stdout.ToString().Contains(expected, StringComparison.Ordinal),
            $"--list-taxonomies output lacks '{expected}' (cutoff {RepoPaths.Cutoff42ReleaseCode}, {ReleasePins.Dpm20Cutoff42}). Output: {stdout}");
    }

    [DataFact]
    public void ListTaxonomies_OnDpm10Source_Exits0_AndReportsThePinnedTaxonomyCount()
    {
        RepoPaths.EnsureAccessDatabaseExists();

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = CliRunner.Run(
            ["--list-taxonomies", "--source", RepoPaths.AccessDatabasePath], stdout, stderr);

        Assert.Equal(CliRunner.ExitOk, exitCode);
        Assert.Empty(stderr.ToString());
        var expected = $"Total: {ReleasePins.Dpm10All41ListedTaxonomies} taxonomies.";
        Assert.True(
            stdout.ToString().Contains(expected, StringComparison.Ordinal),
            $"--list-taxonomies output lacks '{expected}' ({ReleasePins.Dpm10All41}). Output: {stdout}");
    }

    /// <summary>
    /// A DPM 2.0 conversion through <c>--all</c> is COMPLETE: it exits with
    /// <see cref="CliRunner.ExitOk"/> (0) and the output file must exist. Earlier, while the
    /// conversion was still incomplete, this test asserted a "not implemented" exit code; that code
    /// reflected a missing piece, not correct behaviour, so the assertion was updated to the success
    /// code and no other assertion was relaxed.
    /// Column-by-column coverage of the skeleton lives in <c>Dpm20SkeletonLoaderTests</c>; that of
    /// <c>mConcept</c>/<c>mConceptTranslation</c> in <c>Dpm20ConceptTests</c>. Only the end-to-end
    /// check through the CLI is kept here.
    /// </summary>
    // The only "extra" full --all conversion left in the suite (about 28 s): an end-to-end test of
    // the CLI, deliberately independent of the shared fixtures, so it cannot be memoized or merged
    // without losing what it checks (that --source/--output/--all work on their own, without going
    // through any test fixture).
    [Trait("Tier", "RealData")]
    [DataFact]
    public void Convert_OnDpm20Source_Exits0_AndCreatesTheCompleteOutputFile()
    {
        RepoPaths.EnsureAccessDpm20DatabaseExists();

        var tempDirectory = Path.Join(Path.GetTempPath(), $"EbaDpm.Dpm2CliTests_{Environment.ProcessId}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        var outputPath = Path.Join(tempDirectory, "full-conversion.db");

        try
        {
            Assert.False(File.Exists(outputPath));

            using var stdout = new StringWriter();
            using var stderr = new StringWriter();

            var exitCode = CliRunner.Run(
                ["--source", RepoPaths.AccessDpm20DatabasePath, "--cutoff-release", RepoPaths.Cutoff42ReleaseCode, "--output", outputPath, "--all"], stdout, stderr);

            Assert.Equal(CliRunner.ExitOk, exitCode);
            Assert.True(File.Exists(outputPath), "The complete conversion must create the output file.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort.
            }
        }
    }
}
