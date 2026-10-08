using EbaDpm.Converter.Tests.PlaneC;
using EbaDpm.Converter.Tests.Support;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace EbaDpm.Converter.Tests.Dpm2;

/// <summary>
/// The 4.2.1 publication converted with its natural cutoff (no <c>--cutoff-release</c>): derived
/// cutoff, full <c>--all</c> conversion, plane A and plane C against the 4.2 layouts.
/// </summary>
[Collection("Dpm2042_1ValidateSuite")]
[Trait("Tier", "RealData")]
public sealed class Dpm2042_1NaturalCutoffTests(Dpm2042_1ValidateFixture fixture, ITestOutputHelper output)
{
    [DataFact]
    public void NaturalCutoff_IsRelease421_AndIsNotReportedAsRequested()
    {
        Assert.Equal("4.2.1", fixture.DerivedCutoffReleaseCode);
        Assert.False(fixture.DerivedCutoffRequested);
    }

    [DataFact]
    public void All_ExitsOk_AndTheLogShowsTheDerivedCutoffWithoutTheRequestedMarker()
    {
        Assert.True(fixture.ExitCode == 0, $"exit={fixture.ExitCode}; stderr: {fixture.Stderr}");
        Assert.Contains("Cutoff release: 4.2.1.", fixture.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("(requested)", fixture.Stdout, StringComparison.Ordinal);
    }

    [DataFact]
    public void All_EmitsTheTaxonomyPackageOfTheNaturalCutoff()
    {
        var version = ScalarString("SELECT Version FROM mTaxonomyPackage");
        output.WriteLine($"mTaxonomyPackage.Version={version}");
        Assert.False(string.IsNullOrWhiteSpace(version));
        Assert.StartsWith("4.2.1", version, StringComparison.Ordinal);
    }

    [DataFact]
    public void All_PlaneA_FinishesCleanAndNoCriticalFails()
    {
        Assert.Equal(0, fixture.ExitCode);
        var result = ValidatorRunCache.Run(fixture.GeneratedDatabasePath, referencePath: null);
        Assert.Equal(0, result.ExitCode);

        InvariantChecksAssertions.AssertNoCriticalFailures(result.Report, "DPM 2.0 4.2.1 (natural cutoff)");
        Assert.True(InvariantChecksAssertions.NewInvariantChecks(result.Report).Count > 0, "no 'I-*' check appeared in the report.");

        var critical = result.Report.Checks.Where(c => c.Layer == "critical").ToList();
        output.WriteLine($"plane A: critical checks={critical.Count}, failing={critical.Count(c => c.Status == "fail")}, " +
                         $"StaleExceptions={result.Report.Summary.StaleExceptions}");
        Assert.Equal(0, result.Report.Summary.StaleExceptions);
    }

    [DataFact]
    public void All_PlaneC_WithLayouts42_IntroducesNoViolation_AndReportsTheMeasuredFigures()
    {
        var result = ValidatorRunCache.Run(fixture.GeneratedDatabasePath, referencePath: null, PlaneCLayoutRepositoryHolder.LayoutsDatabasePath);

        var cDps01 = Assert.Single(result.Report.Checks, c => c.Id == "C-DPS-01");
        var coverage = Assert.Single(result.Report.Checks, c => c.Id == "C-COB-01");
        foreach (var c in result.Report.Checks.Where(c => c.Plane == "C"))
        {
            output.WriteLine($"{c.Id}: status={c.Status} examined={c.Examined} matched={c.Matched} failed={c.Failed} | {c.Statement}");
        }

        output.WriteLine($"Exceptions: {string.Join(" | ", result.Report.Exceptions.Select(e => $"{e.Id}:{e.BusinessKey} evaluated={e.Evaluated} stale={e.Stale}"))}");
        output.WriteLine($"PlaneCNotice={result.Report.PlaneCNotice}");

        var diag = $"Examined={cDps01.Examined} Failed={cDps01.Failed} Coverage={coverage.Statement} " +
                   $"Samples=[{string.Join(" || ", cDps01.Samples.Select(s => $"{s.BusinessKey}={s.Reference}"))}]";

        // Positive control of the universe, taken from the coverage check: the comparison really
        // happened (a zero violation over an empty comparison proves nothing).
        var match = System.Text.RegularExpressions.Regex.Match(
            coverage.Statement, @"(?<tables>\d+) tables compared, (?<compared>\d+) signatures compared \((?<contained>\d+) contained\)");
        Assert.True(match.Success, $"C-COB-01 statement has an unexpected shape: {coverage.Statement}");
        var compared = long.Parse(match.Groups["compared"].Value, System.Globalization.CultureInfo.InvariantCulture);
        var contained = long.Parse(match.Groups["contained"].Value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(compared > 50_000, $"only {compared} signatures compared. {diag}");
        Assert.Equal(compared, contained);

        // C-DPS-01 may be Skipped here (its census was measured on release 4.2, the cutoff is
        // 4.2.1): that is reported in the output, but it must never FAIL.
        Assert.NotEqual("fail", cDps01.Status);
        Assert.True(cDps01.Failed == 0, diag);
        Assert.Equal(0, result.Report.Summary.StaleExceptions);
    }

    [DataFact]
    public void All_PlaneC_WithAndWithoutLayouts_TheRestOfTheReportIsIdentical()
    {
        PlaneCFlagBehaviorAssertions.AssertRestOfReportUnaffectedByLayouts(
            fixture.GeneratedDatabasePath, PlaneCLayoutRepositoryHolder.LayoutsDatabasePath);
    }

    private string ScalarString(string sql)
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = fixture.GeneratedDatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString();
        using var connection = new SqliteConnection(cs);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }
}
