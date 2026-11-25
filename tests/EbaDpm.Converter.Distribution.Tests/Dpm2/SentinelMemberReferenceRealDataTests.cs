using EbaDpm.Converter.Core.Validation;
using EbaDpm.Converter.Distribution.Tests.All;
using EbaDpm.Converter.Distribution.Tests.Support;
using Xunit.Abstractions;

namespace EbaDpm.Converter.Distribution.Tests.Dpm2;

/// <summary>
/// Issue 70, real data: plane A checks A-SEN-08.1..08.6 (the sentinel member is referenced only by
/// the categorisations of open axes) pass over the DPM 1.0 4.1 conversion and over the DPM 2.0 4.3
/// conversion. Each sub-check must be present in the report, critical, with no violation. The
/// positive controls (the checks do fail when the sentinel is planted) are synthetic:
/// <c>SentinelMemberReferenceChecksSyntheticTests</c>. The rows each sub-check examined are written
/// to the test output, so that an empty population is visible as such.
/// </summary>
internal static class SentinelMemberReferenceAssertions
{
    internal static readonly string[] Ids =
        ["A-SEN-08.1", "A-SEN-08.2", "A-SEN-08.3", "A-SEN-08.4", "A-SEN-08.5", "A-SEN-08.6"];

    internal static void AssertAllPass(ValidationReport report, string label, ITestOutputHelper output)
    {
        foreach (var id in Ids)
        {
            var check = Assert.Single(report.Checks, c => c.Id == id);
            output.WriteLine($"{label} {id}: status={check.Status} examined={check.Examined} failed={check.Failed}");
            Assert.Equal("critical", check.Layer);
            Assert.Equal("pass", check.Status);
            Assert.Equal(0, check.Failed);
        }

        // A-SEN-08.1 examines every categorisation on the sentinel member: one per open-axis
        // ordinate, never zero in a conversion that has open axes (A-SEN-07 equates it to them).
        Assert.True(
            report.Checks.Single(c => c.Id == "A-SEN-08.1").Examined > 0,
            $"{label}: A-SEN-08.1 examined 0 rows (positive control: the population must not be empty).");
    }
}

[Collection("All")]
[Trait("Tier", "RealData")]
public sealed class SentinelMemberReferenceDpm10RealDataTests(AllFixture fixture, ITestOutputHelper output)
{
    [DataFact]
    public void Dpm10_41_ASen08_AllSubChecksPass()
    {
        var result = ValidatorRunCache.Run(fixture.ValidatedDatabasePath, referencePath: null);
        SentinelMemberReferenceAssertions.AssertAllPass(result.Report, "DPM 1.0 4.1", output);
    }
}

[Collection("Dpm2043ValidateSuite")]
[Trait("Tier", "RealData")]
public sealed class SentinelMemberReferenceDpm20RealDataTests(Dpm2043ValidateFixture fixture, ITestOutputHelper output)
{
    [DataFact]
    public void Dpm20_43_ASen08_AllSubChecksPass()
    {
        Assert.Equal(0, fixture.ExitCode);
        var result = ValidatorRunCache.Run(fixture.GeneratedDatabasePath, referencePath: null);
        SentinelMemberReferenceAssertions.AssertAllPass(result.Report, "DPM 2.0 4.3", output);
    }
}
