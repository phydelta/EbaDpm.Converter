using EbaDpm.Converter.Core.Validation;
using EbaDpm.Converter.Tests.All;
using EbaDpm.Converter.Tests.Dpm2;
using EbaDpm.Converter.Tests.Support;

namespace EbaDpm.Converter.Tests.PlaneC;

/// <summary>
/// <c>--validate</c> on the THREE real outputs, with and without <c>--layouts</c>: these two
/// classes cover two of the three real <c>--all</c> conversions the suite maintains -- DPM 1.0
/// (<see cref="AllFixture.ValidatedDatabasePath"/>) and DPM 2.0 4.3
/// (<see cref="Dpm2043ValidateFixture"/>). The third, DPM 2.0 4.2, lives in
/// <see cref="PlaneCRealDataTests"/> because it IS the corpus against which the plane C checks
/// were calibrated.
///
/// The only layout repository available in <c>Data/</c> is the one for release 4.2. The two
/// outputs behave differently:
/// <list type="bullet">
/// <item><b>DPM 1.0</b>: <c>TaxonomyCode</c> does not follow "{framework} {release}" (it is the
/// literal Access code) -- 0 tables compared, as <c>PlaneCComparer</c> foresees (defensive, not a
/// failure).</item>
/// <item><b>DPM 2.0 4.3</b>: the 4.3 Access carries, inside <c>--all</c>, tables whose own release
/// is STILL "4.2" (the model accumulates releases, it does not replace them). Result: a genuine
/// comparison of 743 tables, 90,419 signatures, 90,400 contained -- not an empty universe.</item>
/// </list>
///
/// Both outputs must introduce no violation: the known-divergence census is tied to the DPM 2.0 4.2
/// corpus, and it must not report its entries as stale when the compared corpus is a different one
/// ("not found" there means "this table is not the same table", not "the defect was fixed").
/// </summary>
[Collection("All")]
[Trait("Tier", "RealData")]
public sealed class PlaneCCrossOutputDpm10Tests(AllFixture fixture)
{
    [DataFact]
    public void WithLayouts42_OnTheDpm10Output_IntroducesNoViolation_AndExcludesInsteadOfViolating()
    {
        var result = ValidatorRunCache.Run(fixture.ValidatedDatabasePath, referencePath: null, PlaneCLayoutRepositoryHolder.LayoutsDatabasePath);

        var cDps01 = Assert.Single(result.Report.Checks, c => c.Id == "C-DPS-01");
        var coverage = Assert.Single(result.Report.Checks, c => c.Id == "C-COB-01");

        // The TaxonomyCode of DPM 1.0 does not follow "{framework} {release}" (PlaneCComparer,
        // "defensive, not a failure"): no table can be matched with a layout sheet.
        Assert.Contains("0 tables compared", coverage.Statement);

        // With 0 tables compared, `violatingKeys` is EMPTY, and the two-sided known-exception
        // mechanism must not interpret that as "the census entries stopped violating" (which would
        // report all of them as stale and leave C-DPS-01.Failed at the number of entries, not 0).
        Assert.True(cDps01.Failed == 0, $"C-DPS-01.Failed={cDps01.Failed} (expected 0) -- {coverage.Statement}. " +
            $"Samples: [{string.Join(" || ", cDps01.Samples.Select(s => s.BusinessKey))}]");
    }

    [DataFact]
    public void WithAndWithoutLayouts_OnTheDpm10Output_TheRestOfTheReportIsIdentical()
    {
        PlaneCFlagBehaviorAssertions.AssertRestOfReportUnaffectedByLayouts(
            fixture.ValidatedDatabasePath, PlaneCLayoutRepositoryHolder.LayoutsDatabasePath);
    }
}

[Collection("Dpm2043ValidateSuite")]
[Trait("Tier", "RealData")]
public sealed class PlaneCCrossOutputDpm2043Tests(Dpm2043ValidateFixture fixture)
{
    [DataFact]
    public void WithLayouts42_OnTheDpm2043Output_IntroducesNoViolation()
    {
        var result = ValidatorRunCache.Run(fixture.GeneratedDatabasePath, referencePath: null, PlaneCLayoutRepositoryHolder.LayoutsDatabasePath);

        var cDps01 = Assert.Single(result.Report.Checks, c => c.Id == "C-DPS-01");
        var coverage = Assert.Single(result.Report.Checks, c => c.Id == "C-COB-01");
        var diag = $"Examined={cDps01.Examined} Failed={cDps01.Failed} Coverage={coverage.Statement} " +
                   $"Samples=[{string.Join(" || ", cDps01.Samples.Select(s => $"{s.BusinessKey}={s.Reference}"))}]";
        Assert.True(cDps01.Failed == 0, diag);
    }

    [DataFact]
    public void WithAndWithoutLayouts_OnTheDpm2043Output_TheRestOfTheReportIsIdentical()
    {
        PlaneCFlagBehaviorAssertions.AssertRestOfReportUnaffectedByLayouts(
            fixture.GeneratedDatabasePath, PlaneCLayoutRepositoryHolder.LayoutsDatabasePath);
    }
}

/// <summary>Helper shared by the three collections (the property is asserted, never the figure).</summary>
internal static class PlaneCFlagBehaviorAssertions
{
    public static void AssertRestOfReportUnaffectedByLayouts(string generatedDatabasePath, string layoutsDatabasePath)
    {
        var withoutLayouts = ValidatorRunCache.Run(generatedDatabasePath, referencePath: null, layoutsPath: null);
        var withLayouts = ValidatorRunCache.Run(generatedDatabasePath, referencePath: null, layoutsPath: layoutsDatabasePath);

        var checksWithoutLayouts = withoutLayouts.Report.Checks.Where(c => c.Plane != "C")
            .Select(c => (c.Id, c.Plane, c.Layer, c.Status, c.Examined, c.Matched, c.Failed))
            .ToList();
        var checksWithLayouts = withLayouts.Report.Checks.Where(c => c.Plane != "C")
            .Select(c => (c.Id, c.Plane, c.Layer, c.Status, c.Examined, c.Matched, c.Failed))
            .ToList();

        Assert.Equal(checksWithoutLayouts, checksWithLayouts);
        Assert.Equal(withoutLayouts.ExitCode, withLayouts.ExitCode);
        Assert.Equal("plane C: not evaluated, --layouts missing.", withoutLayouts.Report.PlaneCNotice);
        Assert.StartsWith("plane C: evaluated against", withLayouts.Report.PlaneCNotice);
    }
}
