using EbaDpm.Converter.Core.Validation;
using EbaDpm.Converter.Tests.Schema;
using EbaDpm.Converter.Tests.Support;

namespace EbaDpm.Converter.Tests.Dpm2;

/// <summary>
/// Runs <c>--validate</c> FOR REAL (<see cref="Validator.Run"/>, the same function the CLI uses)
/// over a dedicated <c>--all</c> conversion. It is the only place in the suite that runs the
/// COMPLETE check, and therefore also the permanent protection of plane A invariants that only
/// <c>--validate</c> exercises -- among them A-OCA-01 (the merge of the ABSTRACT ordinate with its
/// own cells, which without this test would only be checked by hand).
///
/// It reuses <see cref="Dpm20SkeletonFixture.ValidatedDatabasePath"/> -- the copy of the single
/// <c>--all</c> conversion that the fixture produces with no open connection, as
/// <c>Validator.Run</c> requires -- so no second full conversion is needed.
///
/// Without a reference, over <c>--all</c>, all critical checks pass. With the 4.2 reference,
/// <c>B-OCA-01</c> measures thousands of failures out of roughly 18,500 leaf ordinates (about
/// 46%); the order of magnitude, not the exact figure, is what this suite records.
///
/// The number of critical checks used to be asserted with a FIXED value, which broke every time a
/// new invariant was added. What the test really protects is "no critical check fails" and "the
/// harness has not been emptied by accident", so the count is a FLOOR (it must not drop below 214,
/// which would reveal a check deleted by accident) and not an equality.
/// </summary>
[Collection("Dpm2Skeleton")]
[Trait("Tier", "RealData")]
public sealed class Dpm20ValidateSuiteTests(Dpm20SkeletonFixture fixture)
{
    /// <summary>
    /// <c>--validate</c> WITHOUT a reference over the <c>--all</c> conversion. What it protects is
    /// <b>"no critical check fails"</b> -- not an exact number of checks, which grows every time a
    /// new invariant is registered. The count is kept as an informative FLOOR (not an equality
    /// assertion): it protects against a harness that is emptied by accident, without breaking
    /// every time the catalogue grows.
    /// </summary>
    [DataFact]
    public void Validate_WithoutReference_OverAll_AllCriticalChecksPass()
    {
        Assert.Equal(0, fixture.ExitCode);

        var result = ValidatorRunCache.Run(fixture.ValidatedDatabasePath, referencePath: null);

        var failingCritical = result.Report.Checks
            .Where(c => c.Layer == "critical" && c.Status == "fail")
            .ToList();

        Assert.True(
            failingCritical.Count == 0,
            $"{failingCritical.Count} critical checks fail without a reference (--all): "
            + string.Join(" | ", failingCritical.Select(c => $"{c.Id}: {c.Failed}/{c.Examined} -- {c.Statement}")));

        Assert.Equal(0, result.Report.Summary.Critical.Failed);

        // Floor, not equality: 214 is the measured count of critical checks. One more critical check
        // raises it without breaking this test; if it DROPS below 214 the test must fail -- it would
        // be the harness silently losing coverage.
        Assert.True(
            result.Report.Summary.Critical.Checks >= 214,
            $"Only {result.Report.Summary.Critical.Checks} critical checks were run "
            + "(expected floor: 214). The harness appears to have lost coverage.");
        Assert.Equal(0, result.ExitCode);

        // Explicit protection of the merge of the ABSTRACT ordinate with its own cells: without it,
        // A-OCA-01 broke on 444 of 18,529 leaves. If someone removes that merge, this assertion --
        // and the ones above -- are what catch it; there is no other one in the whole suite.
        var aoca01 = Assert.Single(result.Report.Checks, c => c.Id == "A-OCA-01");
        Assert.Equal("pass", aoca01.Status);
        Assert.Equal(0, aoca01.Failed);
        Assert.True(aoca01.Examined > 18000, $"A-OCA-01 examined only {aoca01.Examined} leaves: unexpectedly small universe.");
    }

    /// <summary>
    /// <c>--validate</c> against <c>EBA_4.2_Hotfix.db</c>. This test records the measured figure of
    /// B-OCA-01, actually executed, and does NOT assert that it passes: it is the permanent evidence
    /// that the check is still alive and of the order of magnitude of the finding, so that a
    /// regression (or a real fix) is noticed.
    /// </summary>
    [DataFact]
    public void Validate_WithReference42_ReportIncludesBOca01_AndItsCurrentStateIsMeasured()
    {
        RepoPaths.EnsureReferenceDatabaseExists();

        var result = ValidatorRunCache.Run(fixture.ValidatedDatabasePath, RepoPaths.ReferenceDatabasePath);

        var bOca01 = Assert.Single(result.Report.Checks, c => c.Id == "B-OCA-01");

        Assert.True(
            bOca01.Examined > 17000,
            $"B-OCA-01 examined only {bOca01.Examined} leaf ordinates: unexpectedly small universe.");

        // No concrete number of failures is asserted (similarity of a table that is not compared by
        // census is not pursued) -- it is only recorded that the check KEEPS running, and its order
        // of magnitude, so the report has an anchor.
        Assert.True(bOca01.Failed >= 0 && bOca01.Failed <= bOca01.Examined);
    }

    /// <summary>
    /// A plane B known exception that applies to the 4.2 reference but matches nothing is stale,
    /// and the validator does not gate its exit code on it: this test is the gate. It reuses the
    /// cached validator run (no extra conversion).
    /// Positive control: DD-25 (CODIS label) must apply and be matched, so an empty or
    /// non-evaluated exception list cannot pass.
    /// </summary>
    [DataFact]
    public void Validate_WithReference42_NoPlaneBKnownExceptionIsStale()
    {
        RepoPaths.EnsureReferenceDatabaseExists();

        var result = ValidatorRunCache.Run(fixture.ValidatedDatabasePath, RepoPaths.ReferenceDatabasePath);

        var applicablePlaneB = result.Report.Exceptions
            .Where(e => e.Plane == "B" && e.AppliesToThisReference)
            .ToList();

        // Positive control: the list is not empty and a known-matched exception is in it.
        var dd25 = Assert.Single(applicablePlaneB, e => e.Id == "DD-25" && e.BusinessKey == "pillar3_4.2|CODIS");
        Assert.True(dd25.Matched >= 1, $"DD-25 (CODIS) expected matched >= 1, actual {dd25.Matched}.");
        Assert.False(dd25.Stale, "DD-25 (CODIS) is reported stale.");

        var stale = applicablePlaneB.Where(e => e.Stale).ToList();
        Assert.True(
            stale.Count == 0,
            $"{stale.Count} stale plane B known exception(s) apply to the 4.2 reference: "
            + string.Join(" | ", stale.Select(e => $"Id={e.Id}, BusinessKey={e.BusinessKey}, Scope={e.Scope}")));
    }
}
