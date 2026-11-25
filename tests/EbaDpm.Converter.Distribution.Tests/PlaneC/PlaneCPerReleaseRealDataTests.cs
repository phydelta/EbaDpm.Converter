using System.Text.RegularExpressions;
using EbaDpm.Converter.Core.Validation;
using EbaDpm.Converter.Core.Validation.PlaneC;
using EbaDpm.Converter.Distribution.Tests.Dpm2;
using EbaDpm.Converter.Distribution.Tests.Support;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace EbaDpm.Converter.Distribution.Tests.PlaneC;

/// <summary>
/// Plane C of ONE release file against the layouts of the SAME release, under the per-release census
/// rule (#67): the census of the output's release when one exists, an EMPTY census otherwise (every
/// violation fails). The failure message and the test output list EVERY violation of C-DPS-01 (table,
/// layout signature, type) and of C-MHR-01 (unit), not only the first 50 samples of the report, so the
/// orchestrator can arbitrate each one against the Access database. No census is created and no
/// violation is excluded by this test.
/// </summary>
internal static class PlaneCPerReleaseAssertions
{
    public static void AssertNoUnlistedViolation(
        string generatedPath, string layoutsPath, string release, ITestOutputHelper output)
    {
        var result = ValidatorRunCache.Run(generatedPath, referencePath: null, layoutsPath);
        var cDps01 = Assert.Single(result.Report.Checks, c => c.Id == "C-DPS-01");
        var cMhr01 = Assert.Single(result.Report.Checks, c => c.Id == "C-MHR-01");
        var coverage = Assert.Single(result.Report.Checks, c => c.Id == "C-COB-01");

        var census = KnownExceptions.PlaneCCensusForRelease(release);
        output.WriteLine($"release {release}: embedded census {(census is null ? "none (evaluated with an EMPTY census)" : $"{census.Exceptions.Count} entries")}");
        foreach (var c in result.Report.Checks.Where(c => c.Plane == "C"))
        {
            output.WriteLine($"{c.Id}: status={c.Status} examined={c.Examined} matched={c.Matched} failed={c.Failed} | {c.SkipReason} {c.Statement}");
        }

        // The full lists, straight from the checkers (the report keeps only MaxSamples samples).
        var lines = new List<string>();
        using (var generated = Open(generatedPath))
        using (var layouts = Open(layoutsPath))
        {
            foreach (var v in PlaneCComparer.Compare(generated, layouts).Violations
                         .OrderBy(v => v.TableCode, StringComparer.Ordinal).ThenBy(v => v.CanonicalSignature, StringComparer.Ordinal))
            {
                lines.Add($"C-DPS-01 | table={v.TableCode} | type={v.TypeText} | signature={v.CanonicalSignature} | class={v.TableClass} | {v.DetailText}");
            }

            var mhr = PlaneCMetricHierarchyChecker.Check(generated, layouts);
            foreach (var v in mhr.Violations.OrderBy(v => v.Unit.BusinessKey, StringComparer.Ordinal))
            {
                lines.Add($"C-MHR-01 | unit={v.Unit.BusinessKey} | generated hierarchy={v.GeneratedHierarchy} | source={v.Source}");
            }
        }

        foreach (var line in lines)
        {
            output.WriteLine(line);
        }

        // Positive control of the universe: the comparison really happened (a zero over nothing proves nothing).
        var match = Regex.Match(coverage.Statement, @"(?<tables>\d+) tables compared, (?<compared>\d+) signatures compared");
        Assert.True(match.Success, $"C-COB-01 statement has an unexpected shape: {coverage.Statement}");
        Assert.True(int.Parse(match.Groups["tables"].Value, System.Globalization.CultureInfo.InvariantCulture) > 0, $"No table compared: {coverage.Statement}");
        Assert.True(cDps01.Status != "skipped", $"C-DPS-01 was skipped: {cDps01.SkipReason}");
        Assert.True(cDps01.Examined > 0, $"C-DPS-01 examined nothing: {cDps01.Statement}");

        var dps = lines.Where(l => l.StartsWith("C-DPS-01", StringComparison.Ordinal)).ToList();
        var mhrLines = lines.Where(l => l.StartsWith("C-MHR-01", StringComparison.Ordinal)).ToList();

        // Coverage is part of the acceptance: the layouts of a release are exactly the module versions of its file, so
        // no table may be left without a layout sheet, without output, and no layout table code without a table.
        var problems = new List<string>();
        foreach (var (label, pattern) in new[]
                 {
                     ("table(s) without a layout sheet of their release", @"(\d+) table\(s\) without a layout sheet of their release"),
                     ("table(s) without output", @"(\d+) table\(s\) without output"),
                     ("layout TableCode(s) without a table in the output", @"(\d+) layout TableCode\(s\) without a table in the output"),
                 })
        {
            var m = Regex.Match(coverage.Statement, pattern);
            Assert.True(m.Success, $"C-COB-01 statement lacks '{label}': {coverage.Statement}");
            var count = int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            if (count != 0)
            {
                problems.Add($"C-COB-01: {count} {label}");
            }
        }

        if (cDps01.Failed != 0 || cMhr01.Failed != 0)
        {
            problems.Add(
                $"C-DPS-01 failed {cDps01.Failed} of {cDps01.Examined} (comparer lists {dps.Count} violation(s)), " +
                $"C-MHR-01 {cMhr01.Status} failed {cMhr01.Failed} (checker lists {mhrLines.Count})");
        }

        Assert.True(
            problems.Count == 0,
            $"release {release}: {string.Join("; ", problems)}.{Environment.NewLine}{string.Join(Environment.NewLine, lines)}");
    }

    private static SqliteConnection Open(string path)
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        c.Open();
        return c;
    }
}

[Collection("Dpm2044ValidateSuite")]
[Trait("Tier", "RealData")]
public sealed class PlaneC44RealDataTests(Dpm2044ValidateFixture fixture, ITestOutputHelper output)
{
    [DataFact]
    public void Release44File_AgainstThe44Layouts_CDps01AndCMhr01_HaveNoUnlistedViolation()
    {
        Assert.Equal(0, fixture.ExitCode);
        PlaneCPerReleaseAssertions.AssertNoUnlistedViolation(
            fixture.GeneratedDatabasePath, fixture.Layouts44DatabasePath, Dpm2044ValidateFixture.Release44Code, output);
    }
}

[Collection("Dpm2043ValidateSuite")]
[Trait("Tier", "RealData")]
public sealed class PlaneC43RealDataTests(Dpm2043ValidateFixture fixture, ITestOutputHelper output)
{
    [DataFact]
    public void Release43File_AgainstThe43Layouts_CDps01AndCMhr01_HaveNoUnlistedViolation()
    {
        Assert.Equal(0, fixture.ExitCode);
        PlaneCPerReleaseAssertions.AssertNoUnlistedViolation(
            fixture.GeneratedDatabasePath, fixture.Layouts43DatabasePath, Dpm2043ValidateFixture.Release43Code, output);
    }
}
