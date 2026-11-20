using EbaDpm.Converter.Core.Validation;
using EbaDpm.Converter.Tests.All;
using EbaDpm.Converter.Tests.Infrastructure;
using EbaDpm.Converter.Tests.Schema;
using EbaDpm.Converter.Tests.Support;

namespace EbaDpm.Converter.Tests.Dpm2;

/// <summary>
/// PERMANENT tests of the 27 new <c>I-*</c> invariants (plus B-DIC-01.8), measured over the THREE
/// outputs the converter itself produces (DPM 1.0 4.1, DPM 2.0 4.2 and DPM 2.0 4.3). There are no
/// fixed counts of "how many checks there are": the PROPERTY is asserted - 0 critical violations -
/// never "examines exactly N rows".
///
/// Positive control (the trap of <c>OleDbDataAdapter.Fill</c> returning <c>Rows.Count=0</c> on a
/// row that DID exist): each "0 failures" assertion is accompanied by <c>Examined &gt; 0</c> on the
/// SAME check (limited to the new invariants - see
/// <see cref="InvariantChecksAssertions.AssertNoCriticalFailures"/>), to tell "0 because it is
/// right" from "0 because it looks at nothing".
///
/// The THREE outputs reuse the "offline" copy that their conversion fixture already produces -
/// <see cref="All.AllFixture.ValidatedDatabasePath"/> for DPM 1.0 (collection "All"),
/// <see cref="Dpm20SkeletonFixture.ValidatedDatabasePath"/> for DPM 2.0 4.2 (collection
/// "Dpm2Skeleton") and <see cref="Dpm2043ValidateFixture.GeneratedDatabasePath"/> for DPM 2.0 4.3
/// (collection "Dpm2043ValidateSuite", the only publication without another fixture that already
/// converted the same Access database). Each of them leaves the file free of any open connection
/// for <c>Validator.Run</c>, so no second conversion is needed - see the class comments of
/// <see cref="All.AllFixture"/> and <see cref="Dpm20SkeletonFixture"/>.
/// </summary>
public static class InvariantChecksAssertions
{
    /// <summary>
    /// The 27 new invariants all carry the <c>I-</c> prefix in the registry of
    /// <c>IntegrityChecks</c>/<c>DictionaryChecks</c>/<c>ConceptChecks</c>. They are located by
    /// prefix, not by a closed list of ids: a new check that someone adds under the same prefix is
    /// covered by construction, without touching this file (this is not an exception, it is how
    /// the checks to protect are discovered).
    /// </summary>
    public static List<CheckInfo> NewInvariantChecks(ValidationReport report) =>
        report.Checks.Where(c => c.Id.StartsWith("I-", StringComparison.Ordinal)).ToList();

    /// <summary>
    /// ALL the critical checks (not only the 27 new ones) give 0 violations over <c>--all</c>
    /// without a reference.
    ///
    /// The positive control "Examined&gt;0" CANNOT be applied in bulk, not even limited to the new
    /// 27 ("I-*"): <c>A-DIC-05</c>/<c>A-OCA-01b</c>/<c>A-OCA-08</c> and <c>I-REF-07</c>
    /// (mDomainUnion, empty in DPM 1.0 - that concept belongs to DPM 2.0) legitimately examine 0
    /// rows in some of the three outputs: it is the real population of the output, not "I look at
    /// nothing". The positive control is left where it CAN be asserted with evidence - the specific
    /// tests of I-XBR-01c/I-TRE-07b/I-CPT-02b/I-REF-01 below, each with its own Examined&gt;0
    /// measured over the three outputs.
    /// </summary>
    public static void AssertNoCriticalFailures(ValidationReport report, string sourceLabel)
    {
        var criticalChecks = report.Checks.Where(c => c.Layer == "critical").ToList();
        Assert.True(criticalChecks.Count > 0, $"{sourceLabel}: 0 critical checks in the report - the harness did not run.");

        var failing = criticalChecks.Where(c => c.Status == "fail").ToList();
        Assert.True(
            failing.Count == 0,
            $"{sourceLabel}: {failing.Count} critical checks fail - "
            + string.Join(" | ", failing.Select(c => $"{c.Id}: {c.Failed}/{c.Examined} -- {c.Statement}")));
    }

    /// <summary>
    /// I-XBR-01c (exception AO-1): critical in BOTH models. <c>A-UNQ-03-AO1</c> (same Run) is the
    /// CLOSED census - 50 <c>MemberCode</c> with ':' / 49 duplicated <c>MemberXBRLCode</c> in
    /// DPM 1.0, 0/0 in DPM 2.0 - encoded as an EXACT MATCH inside the production check itself: it
    /// passes if and only if the current census equals the one expected for the model. That both
    /// checks give "pass" here ALREADY protects those exact numbers - if the Access database
    /// changed and the census stopped being 50/49 (or 0/0), <c>A-UNQ-03-AO1</c> would become "fail"
    /// and this assertion would catch it, without duplicating the numbers in the test (they live in
    /// <c>IntegrityChecks.Ao1Census</c>).
    /// </summary>
    public static void AssertAo1(ValidationReport report, string sourceLabel)
    {
        var ixbr01c = Assert.Single(report.Checks, c => c.Id == "I-XBR-01c");
        Assert.Equal("critical", ixbr01c.Layer);
        Assert.Equal("pass", ixbr01c.Status);
        Assert.Equal(0, ixbr01c.Failed);
        Assert.True(ixbr01c.Examined > 0, $"{sourceLabel}: I-XBR-01c examined 0 rows (positive control).");

        var census = Assert.Single(report.Checks, c => c.Id == "A-UNQ-03-AO1");
        Assert.Equal("critical", census.Layer);
        Assert.Equal("pass", census.Status);
        Assert.Equal(0, census.Failed);
    }

    /// <summary>
    /// I-REF-01: NOT an invariant of ours - INFORMATIVE layer with a DECLARED census (pinned per
    /// output in <see cref="ReleasePins"/>: <c>Dpm10All41</c>, <c>Dpm20Cutoff42</c> and
    /// <c>Dpm20Natural43</c>; the SAME business hierarchies). A test that treated it as critical, or that
    /// stopped checking its exact census, would be exactly the kind of "fix" that is not ours to
    /// make silently.
    /// </summary>
    internal static void AssertIRef01DeclaredCensus(ValidationReport report, string sourceLabel, ReleasePin pin, long expectedCensus)
    {
        var iref01 = Assert.Single(report.Checks, c => c.Id == "I-REF-01");
        Assert.Equal("informative", iref01.Layer);
        Assert.True(iref01.Examined > 0, $"{sourceLabel}: I-REF-01 examined 0 rows (positive control).");
        Assert.NotEqual("fail", iref01.Status); // informative: never "fail", at most "info"
        ReleasePins.AssertPinned(pin, $"{sourceLabel}: I-REF-01 hierarchies without nodes (declared census)", expectedCensus, iref01.Failed);
    }
}

// ---------------------------------------------------------------------------------------------
// DPM 1.0 - reuses AllFixture.ValidatedDatabasePath (collection "All"), which already carries the
// SAME --all conversion with no open connection on top of it. See the class comment of AllFixture.
// ---------------------------------------------------------------------------------------------

[Collection("All")]
[Trait("Tier", "RealData")]
public sealed class InvariantChecksDpm10Tests(AllFixture fixture)
{
    private readonly AllFixture _fixture = fixture;

    /// <summary>
    /// Over DPM 1.0 --all, the 27 new invariants (those that apply to this model - the ones marked
    /// "critical ONLY in DPM 2.0", I-REF-15/I-XBR-02/I-CPT-02b, do not even appear here, guarded by
    /// <c>model == Dpm2</c> in the production code itself) give 0 violations.
    /// </summary>
    [DataFact]
    public void Dpm10_All_NoCriticalFails()
    {
        var result = ValidatorRunCache.Run(_fixture.ValidatedDatabasePath, referencePath: null);
        Assert.Equal(0, result.ExitCode);

        InvariantChecksAssertions.AssertNoCriticalFailures(result.Report, "DPM 1.0");

        var newChecks = InvariantChecksAssertions.NewInvariantChecks(result.Report);
        Assert.True(newChecks.Count > 0, "DPM 1.0: no 'I-*' check appeared in the report - the invariant catalogue did not run.");

        // Guarded to DPM 2.0 in the code itself: they must not appear over DPM 1.0.
        Assert.DoesNotContain(result.Report.Checks, c => c.Id is "I-REF-15" or "I-XBR-02" or "I-CPT-02b" or "I-TRE-07b");
    }

    /// <summary>I-XBR-01c: the 50 of AO-1 are the NAMED exception of DPM 1.0, and the check passes.</summary>
    [DataFact]
    public void Dpm10_All_Ao1IsTheOnlyViolationAndIsRegisteredAsAnException()
    {
        var result = ValidatorRunCache.Run(_fixture.ValidatedDatabasePath, referencePath: null);
        InvariantChecksAssertions.AssertAo1(result.Report, "DPM 1.0");
    }

    /// <summary>I-REF-01: hierarchies without nodes in DPM 1.0, declared census, informative layer.</summary>
    [DataFact]
    public void Dpm10_All_IRef01DeclaredCensusMatchesThePin()
    {
        var result = ValidatorRunCache.Run(_fixture.ValidatedDatabasePath, referencePath: null);
        InvariantChecksAssertions.AssertIRef01DeclaredCensus(result.Report, "DPM 1.0", ReleasePins.Dpm10All41, expectedCensus: 14);
    }
}

// ---------------------------------------------------------------------------------------------
// DPM 2.0 4.2 - reuses Dpm20SkeletonFixture.ValidatedDatabasePath (collection "Dpm2Skeleton"),
// without reconverting. See the class comment of Dpm20SkeletonFixture.
// ---------------------------------------------------------------------------------------------

[Collection("Dpm2Skeleton")]
[Trait("Tier", "RealData")]
public sealed class InvariantChecksDpm2042Tests(Dpm20SkeletonFixture fixture)
{
    [DataFact]
    public void Dpm20Release42_All_NoCriticalFails()
    {
        Assert.Equal(0, fixture.ExitCode);
        var result = ValidatorRunCache.Run(fixture.ValidatedDatabasePath, referencePath: null);
        Assert.Equal(0, result.ExitCode);

        InvariantChecksAssertions.AssertNoCriticalFailures(result.Report, "DPM 2.0 4.2");

        var newChecks = InvariantChecksAssertions.NewInvariantChecks(result.Report);
        Assert.True(newChecks.Count > 0, "DPM 2.0 4.2: no 'I-*' check appeared in the report.");

        // I-TRE-07b is a plain critical check (no exception exists): in 4.2 it gives 0 failures
        // (measured: 0 re-anchored nodes at --release 4.2).
        var itre07b = Assert.Single(result.Report.Checks, c => c.Id == "I-TRE-07b");
        Assert.Equal("pass", itre07b.Status);
        Assert.Equal(0, itre07b.Failed);
        Assert.True(itre07b.Examined > 0, "DPM 2.0 4.2: I-TRE-07b examined 0 rows (positive control).");
    }

    [DataFact]
    public void Dpm20Release42_All_Ao1IsInert()
    {
        var result = ValidatorRunCache.Run(fixture.ValidatedDatabasePath, referencePath: null);
        // AO-1 is an anomaly of the DPM 1.0 Access database - in DPM 2.0 the expected census is 0/0.
        InvariantChecksAssertions.AssertAo1(result.Report, "DPM 2.0 4.2");
    }

    [DataFact]
    public void Dpm20Release42_All_IRef01DeclaredCensusMatchesThePin()
    {
        var result = ValidatorRunCache.Run(fixture.ValidatedDatabasePath, referencePath: null);
        InvariantChecksAssertions.AssertIRef01DeclaredCensus(result.Report, "DPM 2.0 4.2", ReleasePins.Dpm20Cutoff42, expectedCensus: 14);
    }

    /// <summary>
    /// I-CPT-02b: critical ONLY in DPM 2.0 - confirms that it appears and passes in 4.2 (the
    /// corrected form of the sentinel discount, only in Domain).
    /// </summary>
    [DataFact]
    public void Dpm20Release42_All_ICpt02bPasses()
    {
        var result = ValidatorRunCache.Run(fixture.ValidatedDatabasePath, referencePath: null);
        var icpt02b = Assert.Single(result.Report.Checks, c => c.Id == "I-CPT-02b");
        Assert.Equal("critical", icpt02b.Layer);
        Assert.Equal("pass", icpt02b.Status);
        Assert.Equal(0, icpt02b.Failed);
        Assert.True(icpt02b.Examined > 0, "DPM 2.0 4.2: I-CPT-02b examined 0 rows (positive control).");
    }

    /// <summary>
    /// Positive control of the empty-universe test of the 4.3 file: the 4.2 file against the 4.2 reference
    /// shares taxonomies, so the comparable universe is NOT empty (taxonomies, tables, ordinates, cells
    /// all above 0) and plane B checks are emitted.
    /// </summary>
    [DataFact]
    public void Dpm20Release42_WithReference42_ComparableUniverseIsNotEmpty()
    {
        RepoPaths.EnsureReferenceDatabaseExists();
        var result = ValidatorRunCache.Run(fixture.ValidatedDatabasePath, RepoPaths.ReferenceDatabasePath);
        var universe = result.Report.Universe;

        Assert.NotEmpty(universe.ComparableTaxonomies);
        Assert.True(universe.Tables > 0, "Comparable tables: 0 for the 4.2 file against the 4.2 reference.");
        Assert.True(universe.Ordinates > 0, "Comparable ordinates: 0 for the 4.2 file against the 4.2 reference.");
        Assert.True(universe.Cells > 0, "Comparable cells: 0 for the 4.2 file against the 4.2 reference.");
        Assert.Contains(result.Report.Checks, c => c.Id.StartsWith("B-DIC-01.", StringComparison.Ordinal));
    }

    /// <summary>
    /// The publication guard: --validate of the 4.2 output against its OWN reference (same
    /// publication) does NOT trigger the guard - ALL the B-DIC-01.* checks really compare, none is
    /// skipped. If the
    /// guard also skipped here, it would protect nothing, it would hide the real comparison.
    ///
    /// No fixed count: the family grew from 8 to 9 with B-DIC-01.9 - the property it protects is
    /// "no B-DIC-01.* is skipped here", not a count. A FLOOR is required (at least the 8 that
    /// already existed) to catch a harness emptied by accident, and "none skipped" is not
    /// conditioned on the total.
    /// </summary>
    [DataFact]
    public void Dpm20Release42_WithReference42_NoBDic01IsSkipped()
    {
        RepoPaths.EnsureReferenceDatabaseExists();
        var result = ValidatorRunCache.Run(fixture.ValidatedDatabasePath, RepoPaths.ReferenceDatabasePath);

        var bDic01Checks = result.Report.Checks.Where(c => c.Id.StartsWith("B-DIC-01.", StringComparison.Ordinal)).ToList();
        Assert.True(bDic01Checks.Count >= 8, $"Only {bDic01Checks.Count} B-DIC-01.* checks (expected floor: 8).");

        var skipped = bDic01Checks.Where(c => c.Status == "skipped").ToList();
        Assert.True(
            skipped.Count == 0,
            "The following B-DIC-01.* were skipped despite comparing against their OWN publication (generated 4.2 vs reference 4.2): "
            + string.Join(" | ", skipped.Select(c => $"{c.Id}: {c.SkipReason}")));
    }
}

// ---------------------------------------------------------------------------------------------
// DPM 2.0 4.3 - own fixture, the only publication that had no --validate in the suite.
// ---------------------------------------------------------------------------------------------

[Collection("Dpm2043ValidateSuite")]
[Trait("Tier", "RealData")]
public sealed class InvariantChecksDpm2043Tests(Dpm2043ValidateFixture fixture)
{
    [DataFact]
    public void Dpm20Release43_All_FinishesCleanAndNoCriticalFails()
    {
        Assert.Equal(0, fixture.ExitCode);
        var result = ValidatorRunCache.Run(fixture.GeneratedDatabasePath, referencePath: null);
        Assert.Equal(0, result.ExitCode);

        InvariantChecksAssertions.AssertNoCriticalFailures(result.Report, "DPM 2.0 4.3");

        var newChecks = InvariantChecksAssertions.NewInvariantChecks(result.Report);
        Assert.True(newChecks.Count > 0, "DPM 2.0 4.3: no 'I-*' check appeared in the report.");
    }

    [DataFact]
    public void Dpm20Release43_All_Ao1IsInert()
    {
        var result = ValidatorRunCache.Run(fixture.GeneratedDatabasePath, referencePath: null);
        InvariantChecksAssertions.AssertAo1(result.Report, "DPM 2.0 4.3");
    }

    /// <summary>I-REF-01: declared census of 4.3 (see the pins: it differs from 4.2 and DPM 1.0, same family).</summary>
    [DataFact]
    public void Dpm20Release43_All_IRef01DeclaredCensusMatchesThePin()
    {
        var result = ValidatorRunCache.Run(fixture.GeneratedDatabasePath, referencePath: null);
        InvariantChecksAssertions.AssertIRef01DeclaredCensus(result.Report, "DPM 2.0 4.3", ReleasePins.Dpm20Natural43, expectedCensus: 15);
    }

    [DataFact]
    public void Dpm20Release43_All_ICpt02bPasses()
    {
        var result = ValidatorRunCache.Run(fixture.GeneratedDatabasePath, referencePath: null);
        var icpt02b = Assert.Single(result.Report.Checks, c => c.Id == "I-CPT-02b");
        Assert.Equal("pass", icpt02b.Status);
        Assert.Equal(0, icpt02b.Failed);
        Assert.True(icpt02b.Examined > 0, "DPM 2.0 4.3: I-CPT-02b examined 0 rows (positive control).");
    }

    /// <summary>
    /// Old: <c>Dpm20Release43_All_ITre07bPassesWithThe104OfAo3Absorbed</c> (passed because 104 nodes were
    /// absorbed as the named exception AO-3; asserted "named exception" in the statement).
    /// New: I-TRE-07b passes in 4.3 BECAUSE the loader re-anchors those 104 nodes (tie-break by
    /// consistency): 0 failures, nodes examined, and NO exception of any kind attached to the check
    /// (the statement does not mention an exception and the report lists none for I-TRE-07b / AO-3).
    /// Positive control: the synthetic <c>ITre07b_AFormerlyDeclaredKeyAlone_NowFails</c> shows the same
    /// check fails on a cross-domain node, and <c>Dpm20HierarchyNodeLoaderSyntheticTests</c> shows the
    /// loader does the re-anchoring.
    /// </summary>
    [DataFact]
    public void Dpm20Release43_All_ITre07bPassesWithNoExceptionApplied()
    {
        var result = ValidatorRunCache.Run(fixture.GeneratedDatabasePath, referencePath: null);
        var itre07b = Assert.Single(result.Report.Checks, c => c.Id == "I-TRE-07b");
        Assert.Equal("critical", itre07b.Layer);
        Assert.Equal("pass", itre07b.Status);
        Assert.Equal(0, itre07b.Failed);
        Assert.True(itre07b.Examined > 0, "DPM 2.0 4.3: I-TRE-07b examined 0 rows (positive control).");
        Assert.DoesNotContain("exception", itre07b.Statement, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(result.Report.Exceptions, e => e.Scope == "I-TRE-07b" || e.Id == "AO-3");
    }

    /// <summary>
    /// A reference of ANOTHER release: since #73 the 4.3 file holds only taxonomies starting in releases up
    /// to 4.3 under their own codes and shares no taxonomy code with the 4.2 reference export. The
    /// validator then reports an empty comparable universe (0 taxonomies, tables, ordinates, cells), emits
    /// NO plane B check at all (so no <c>B-DIC-01.*</c> is evaluated over a structurally invalid
    /// universe) and counts no plane B failure. Replaces the previous premise ("the 4.3 file shares
    /// taxonomies with the 4.2 reference and every B-DIC-01.* is skipped"), which no longer holds.
    ///
    /// Positive control that a shared universe yields plane B checks: the 4.2 file against the same
    /// reference, <c>InvariantChecksDpm2042Tests.Dpm20Release42_WithReference42_ComparableUniverseIsNotEmpty</c>
    /// (and, in that class, <c>Dpm20Release42_WithReference42_NoBDic01IsSkipped</c>).
    /// </summary>
    [DataFact]
    public void Dpm20Release43_WithReference42_HasAnEmptyComparableUniverse_AndNoPlaneBFailure()
    {
        RepoPaths.EnsureReferenceDatabaseExists();
        var result = ValidatorRunCache.Run(fixture.GeneratedDatabasePath, RepoPaths.ReferenceDatabasePath);
        var universe = result.Report.Universe;

        Assert.Empty(universe.ComparableTaxonomies);
        Assert.Equal(0, universe.Tables);
        Assert.Equal(0, universe.Ordinates);
        Assert.Equal(0, universe.Cells);

        // The universes are really disjoint and non-empty (not "empty because one side is empty").
        Assert.NotEmpty(universe.GeneratedOnlyTaxonomies);
        Assert.NotEmpty(universe.ReferenceOnlyTaxonomies);

        var planeB = result.Report.Checks.Where(c => c.Id.StartsWith("B-", StringComparison.Ordinal)).ToList();
        Assert.True(
            planeB.Count == 0,
            "Plane B checks were emitted over an empty comparable universe: " + string.Join(" | ", planeB.Select(c => $"{c.Id}: {c.Status}")));
        Assert.DoesNotContain(result.Report.Checks, c => c.Id.StartsWith("B-DIC-01.", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Report.Checks, c => c.Layer == "critical" && c.Status == "fail" && c.Id.StartsWith("B-", StringComparison.Ordinal));
    }
}
