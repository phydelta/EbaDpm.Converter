using EbaDpm.Converter.Core.Validation;
using EbaDpm.Converter.Tests.Dpm2;
using EbaDpm.Converter.Tests.Support;

namespace EbaDpm.Converter.Tests.PlaneC;

/// <summary>
/// Plane C on the REAL corpus: the DPM 2.0 4.2 <c>--all</c> output (<see cref="Dpm20SkeletonFixture"/>,
/// already built by <c>Dpm20ValidateSuiteTests</c> -- reused here at no additional conversion
/// cost) against the real repository of Annotated Table Layouts 4.2
/// (<see cref="PlaneCLayoutRepositoryHolder"/>).
///
/// The PROPERTY is asserted, never the figure. Several tests in this project once expired because
/// they asserted the number of a broken state -- what is asserted here is "no violation OUTSIDE
/// the known-divergence census" and "no stale census entry", not "30". If the measurement
/// changes -- the code, the layout or the Access move --, this test has to START FAILING so that
/// someone reviews <c>plane-c-known-divergences-4.2.tsv</c>, instead of staying green with a
/// different number.
/// </summary>
[Collection("Dpm2Skeleton")]
[Trait("Tier", "RealData")]
public sealed class PlaneCRealDataTests(Dpm20SkeletonFixture fixture)
{
    [DataFact]
    public void CDps01_OnTheRealDpm2042Corpus_NoViolationOutsideTheKnownDivergenceCensus()
    {
        var result = ValidatorRunCache.Run(fixture.ValidatedDatabasePath, referencePath: null, PlaneCLayoutRepositoryHolder.LayoutsDatabasePath);

        var cDps01 = Assert.Single(result.Report.Checks, c => c.Id == "C-DPS-01");

        // Universe positive control: if this is 0, the layout is not really being read and the
        // rest of the assertions of this test prove nothing.
        Assert.True(cDps01.Examined > 50_000, $"C-DPS-01 examined only {cDps01.Examined} signatures -- unexpectedly small universe (91,078 were measured).");

        Assert.Equal("pass", cDps01.Status);
        Assert.Equal(0, cDps01.Failed);
    }

    [DataFact]
    public void KnownDivergenceCensus_OnTheRealDpm2042Corpus_IsEvaluatedAndNoEntryIsStale()
    {
        var result = ValidatorRunCache.Run(fixture.ValidatedDatabasePath, referencePath: null, PlaneCLayoutRepositoryHolder.LayoutsDatabasePath);

        var census = result.Report.Exceptions.Where(e => e.Id == "DD-23").ToList();

        // The census has to exist AND be evaluated -- if this is empty, it stopped being loaded
        // (the embedded resource) and C-DPS-01 would pass by default for the wrong reason.
        Assert.NotEmpty(census);
        Assert.All(census, e => Assert.True(e.Evaluated, $"Census entry '{e.BusinessKey}' was never evaluated -- C-DPS-01 never consulted the census."));
        Assert.All(census, e => Assert.True(e.AppliesToThisReference, $"Census entry '{e.BusinessKey}' with AppliesToThisReference=false -- Reference should be \"-\" (plane A)."));

        // The property, not the figure: no declared entry stops being violated -- if one did, it
        // has to be reported as stale, and that makes the test fail.
        var stale = census.Where(e => e.Stale).ToList();
        Assert.True(
            stale.Count == 0,
            "The census has stale entries (the reference/the Access changed and nobody reviewed " +
            $"plane-c-known-divergences-4.2.tsv): {string.Join(" | ", stale.Select(e => e.BusinessKey))}");

        Assert.Equal(0, result.Report.Summary.StaleExceptions);
    }

    [DataFact]
    public void CCob01_OnTheRealDpm2042Corpus_ReportsTheExaminedUnitsAlwaysUpFront()
    {
        var result = ValidatorRunCache.Run(fixture.ValidatedDatabasePath, referencePath: null, PlaneCLayoutRepositoryHolder.LayoutsDatabasePath);

        var coverage = Assert.Single(result.Report.Checks, c => c.Id == "C-COB-01");

        Assert.Equal("info", coverage.Status);
        Assert.True(coverage.Examined > 50_000, $"C-COB-01 examined only {coverage.Examined} layout cells.");
        Assert.Contains("tables compared", coverage.Statement);
    }

    [DataFact]
    public void CCls01_OnTheRealDpm2042Corpus_BreaksDownTheThreeClasses()
    {
        var result = ValidatorRunCache.Run(fixture.ValidatedDatabasePath, referencePath: null, PlaneCLayoutRepositoryHolder.LayoutsDatabasePath);

        var breakdown = Assert.Single(result.Report.Checks, c => c.Id == "C-CLS-01");
        var labels = breakdown.Samples.Select(s => s.BusinessKey).ToList();

        Assert.Contains("REGULAR", labels);
        Assert.Contains("OPEN_AXIS", labels);
        Assert.Contains("REAL_Z", labels);
    }

    /// <summary>
    /// The <c>REL.n -> REL</c> tolerance names the sheets to which it was applied -- on the real
    /// corpus there is at least one (two FINREP sheets are published as <c>4.2.1</c>). Property:
    /// if the counter is greater than 0, it has to come with at least one named sample (never
    /// silent) -- the exact number of sheets is not asserted.
    /// </summary>
    [DataFact]
    public void CRel01_OnTheRealDpm2042Corpus_IfThereIsToleranceItComesNamed()
    {
        var result = ValidatorRunCache.Run(fixture.ValidatedDatabasePath, referencePath: null, PlaneCLayoutRepositoryHolder.LayoutsDatabasePath);

        var tolerance = Assert.Single(result.Report.Checks, c => c.Id == "C-REL-01");

        Assert.Equal("info", tolerance.Status);
        if (tolerance.Examined > 0)
        {
            Assert.NotEmpty(tolerance.Samples);
        }
    }

    /// <summary>
    /// The REAL version (with real data, not the empty schema of <see cref="PlaneCValidateFlagTests"/>):
    /// on the DPM 2.0 4.2 corpus -- which passes today 0/0 critical without a reference
    /// (<c>Dpm20ValidateSuiteTests</c>) --, adding <c>--layouts</c> does not change a single field
    /// of the rest of the report except for the appearance of the plane C checks.
    /// </summary>
    [DataFact]
    public void Validate_WithAndWithoutLayouts_OnTheRealCorpus_TheRestOfTheReportIsIdentical()
    {
        var withoutLayouts = ValidatorRunCache.Run(fixture.ValidatedDatabasePath, referencePath: null, layoutsPath: null);
        var withLayouts = ValidatorRunCache.Run(fixture.ValidatedDatabasePath, referencePath: null, PlaneCLayoutRepositoryHolder.LayoutsDatabasePath);

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
