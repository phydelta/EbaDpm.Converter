using EbaDpm.Converter.Core.Validation;
using EbaDpm.Converter.Tests.Dpm2;
using EbaDpm.Converter.Tests.Support;

namespace EbaDpm.Converter.Tests.PlaneC;

/// <summary>
/// <c>C-EQU-01</c> on the REAL corpus: the DPM 2.0 4.2 <c>--all</c> output
/// (<see cref="Dpm20SkeletonFixture"/>, reused from <c>Dpm20ValidateSuiteTests</c>) against the
/// real repository of Annotated Table Layouts 4.2 (<see cref="PlaneCLayoutRepositoryHolder"/>).
///
/// The divergences that once motivated an exception census were the open-axis restriction bracket
/// (of the TABLE, not of the Variable that the <c>==</c> asserts). <c>C-EQU-01</c> relaxes that
/// bracket before comparing, and the divergences drop to 0 STRUCTURALLY -- no exception census is
/// needed for <c>C-EQU-01</c> (<c>KnownExceptions</c> declares none with
/// <c>Check == "C-EQU-01"</c>).
///
/// The PROPERTY is asserted, never a figure that can move with each layout re-extraction -- except
/// the counter of relaxations (<c>RulesRelaxedByOpenAxisBracket</c>), which is the positive control
/// that the relaxation is really being applied (if it were 0, the relaxation would have no effect
/// on the real corpus), and the exact figure of checkable rules (4,960), a deliberate exception:
/// it proves that restricting the ordinate index by <c>TableID</c> genuinely RECOVERS coverage
/// (4,849 without the restriction) and not just "more than 1,000". If it stops being 4,960 it is
/// a FINDING -- the test is not relaxed to make it pass again: it is documented and reported.
/// </summary>
[Collection("Dpm2Skeleton")]
[Trait("Tier", "RealData")]
public sealed class PlaneCEquRealDataTests(Dpm20SkeletonFixture fixture)
{
    [DataFact]
    public void CEqu01_OnTheRealDpm2042Corpus_AllCellsConsistentAfterRelaxing()
    {
        var result = ValidatorRunCache.Run(fixture.ValidatedDatabasePath, referencePath: null, PlaneCLayoutRepositoryHolder.LayoutsDatabasePath);

        var cEqu01 = Assert.Single(result.Report.Checks, c => c.Id == "C-EQU-01");

        // The exact FIGURE, not a threshold -- 4,960 checkable out of 5,120 total rules (versus
        // 4,849 without the restriction by TableID: the 111 recovered are exactly those that fell
        // into "ambiguous cell" because of table codes shared between taxonomies).
        Assert.Equal(4960, cEqu01.Examined);

        Assert.NotEqual("skipped", cEqu01.Status);
        Assert.Equal(0, cEqu01.Failed);

        // There is no exception census for this check -- the raw violations ARE the result, with no
        // exception in between that could mask a future regression.
        Assert.DoesNotContain(result.Report.Exceptions, e => e.Id == "DD-24");
    }

    [DataFact]
    public void CEqu02_OnTheRealDpm2042Corpus_ReportsCoverageAndHowManyNeededRelaxing()
    {
        var result = ValidatorRunCache.Run(fixture.ValidatedDatabasePath, referencePath: null, PlaneCLayoutRepositoryHolder.LayoutsDatabasePath);

        var coverage = Assert.Single(result.Report.Checks, c => c.Id == "C-EQU-02");

        Assert.Equal("info", coverage.Status);
        Assert.True(coverage.Examined > 1000, $"C-EQU-02 examined only {coverage.Examined} total rules.");
        Assert.Contains("checkable", coverage.Statement);

        // The positive control that the relaxation really has an effect on the real corpus: if
        // this were 0, the relaxation would never have had anything to fix and the original
        // divergences would remain unexplained.
        Assert.Contains("NEEDED the open-axis bracket", coverage.Statement);
        Assert.DoesNotContain("0 NEEDED the open-axis bracket", coverage.Statement);
    }
}
