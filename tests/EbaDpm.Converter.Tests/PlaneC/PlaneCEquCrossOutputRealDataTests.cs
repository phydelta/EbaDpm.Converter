using EbaDpm.Converter.Core.Validation;
using EbaDpm.Converter.Core.Validation.PlaneC;
using EbaDpm.Converter.Tests.All;
using EbaDpm.Converter.Tests.Dpm2;
using EbaDpm.Converter.Tests.Support;

namespace EbaDpm.Converter.Tests.PlaneC;

/// <summary>
/// The <c>C-EQU-01</c> counterpart of <see cref="PlaneCCrossOutputDpm10Tests"/> -- the other two
/// of the three real <c>--all</c> conversions the suite maintains, DPM 1.0
/// (<see cref="AllFixture.ValidatedDatabasePath"/>) and DPM 2.0 4.3
/// (<see cref="Dpm2043ValidateFixture"/>), against the ONLY layout repository available in
/// <c>Data/</c> (4.2).
///
/// <c>C-EQU-01</c> resolves each TERM only if its <c>TableCode</c> matches the release of the sheet
/// the rule came from (<c>PlaneCReleaseMatcher</c>, the same mechanism <c>PlaneCComparer</c> uses).
/// On DPM 1.0 this gives <b>0 checkable rules</b> -- "different release" plus "table absent" over
/// all the rules -- and <c>C-EQU-01</c> comes out <c>Skipped</c> with the reason written, NEVER
/// <c>fail</c> nor <c>pass</c>.
///
/// The ordinate index carries the <c>TableID</c> in its key, and the resolution of each term
/// filters THAT set of <c>TableID</c>s by release BEFORE joining their <c>OrdinateID</c>s
/// (<see cref="PlaneCEqualityChecker"/>). In the CUMULATIVE cut of 4.3 (where <c>TableID</c>s of
/// 4.0/4.2/4.3 coexist under the same code) the cell lookup would otherwise be ambiguous even for
/// the rules that the release guard lets through. With the fix, 4.3 yields 4,780 checkable rules
/// and 0 divergent ones -- an entire new corpus, not just a guard that no longer bites.
/// <c>PlaneCEquRealDataTests</c> measures the same against 4.2.
/// </summary>
[Collection("All")]
[Trait("Tier", "RealData")]
public sealed class PlaneCEquCrossOutputDpm10Tests(AllFixture fixture)
{
    [DataFact]
    public void CEqu01_OnTheDpm10Output_ShouldNotProduceViolationsFromAForeignLayoutRepository()
    {
        var result = ValidatorRunCache.Run(fixture.ValidatedDatabasePath, referencePath: null, PlaneCLayoutRepositoryHolder.LayoutsDatabasePath);

        var cEqu01 = Assert.Single(result.Report.Checks, c => c.Id == "C-EQU-01");
        var cEqu02 = Assert.Single(result.Report.Checks, c => c.Id == "C-EQU-02");

        // The property that the rest of plane C demands of a CRITICAL check against a foreign
        // layout repository -- 0 violations, or an explicit declaration that it does not apply --
        // and it is dropped with the full evidence if it is not met.
        Assert.True(cEqu01.Failed == 0,
            $"C-EQU-01.Status={cEqu01.Status} Examined={cEqu01.Examined} Failed={cEqu01.Failed} " +
            $"(expected Failed=0, or -- better -- Skipped with a scope reason) on the " +
            $"DPM 1.0 output with the DPM 2.0 4.2 layout repository. Coverage: {cEqu02.Statement}. " +
            $"Samples: [{string.Join(" || ", cEqu01.Samples.Select(s => s.BusinessKey))}].");

        // Failed=0 is not enough -- it has to be Skipped (never pass, which would be
        // indistinguishable from "0 violations over a real universe") WITH the "different release"
        // reason written, and the ExitCode of the run (result.ExitCode, not only that of the
        // individual check) stays at 0.
        Assert.Equal("skipped", cEqu01.Status);
        Assert.Equal(0, cEqu01.Examined);
        Assert.Contains("0 checkable rules", cEqu01.SkipReason);
        Assert.Contains(cEqu02.Samples, s => s.BusinessKey == "different release");
        Assert.Equal(0, result.ExitCode);
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
public sealed class PlaneCEquCrossOutputDpm2043Tests(Dpm2043ValidateFixture fixture)
{
    /// <summary>
    /// 4.3 as a LIVE CORPUS -- a publication DIFFERENT from the one the layouts came from (4.2),
    /// and a second INDEPENDENT measurement of the whole plane C machinery. It comes out green
    /// because 4,780 rules were genuinely evaluated, with 0 divergent -- the exact figure, not a
    /// threshold. If this stops being 4,780 it is a FINDING.
    /// </summary>
    [DataFact]
    public void CEqu01_OnTheDpm2043Output_4780CheckableRulesZeroDivergent()
    {
        var result = ValidatorRunCache.Run(fixture.GeneratedDatabasePath, referencePath: null, PlaneCLayoutRepositoryHolder.LayoutsDatabasePath);

        var cEqu01 = Assert.Single(result.Report.Checks, c => c.Id == "C-EQU-01");
        var cEqu02 = Assert.Single(result.Report.Checks, c => c.Id == "C-EQU-02");
        var diag = $"Status={cEqu01.Status} Examined={cEqu01.Examined} Failed={cEqu01.Failed} Cov={cEqu02.Statement} " +
                   $"Samples=[{string.Join(" || ", cEqu01.Samples.Select(s => s.BusinessKey))}]";

        Assert.True(cEqu01.Examined == 4780, diag);
        Assert.True(cEqu01.Failed == 0, diag);
        Assert.NotEqual("skipped", cEqu01.Status);
    }

    [DataFact]
    public void WithAndWithoutLayouts_OnTheDpm2043Output_TheRestOfTheReportIsIdentical()
    {
        PlaneCFlagBehaviorAssertions.AssertRestOfReportUnaffectedByLayouts(
            fixture.GeneratedDatabasePath, PlaneCLayoutRepositoryHolder.LayoutsDatabasePath);
    }
}
