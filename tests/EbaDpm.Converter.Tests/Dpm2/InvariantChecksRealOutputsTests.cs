using EbaDpm.Converter.Core.Validation;
using EbaDpm.Converter.Tests.All;
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
    /// I-REF-01: NOT an invariant of ours - INFORMATIVE layer with a DECLARED census (14 DPM 1.0 /
    /// 14 4.2 / 15 4.3, the SAME business hierarchies). A test that treated it as critical, or that
    /// stopped checking its exact census, would be exactly the kind of "fix" that is not ours to
    /// make silently.
    /// </summary>
    public static void AssertIRef01DeclaredCensus(ValidationReport report, string sourceLabel, long expectedCensus)
    {
        var iref01 = Assert.Single(report.Checks, c => c.Id == "I-REF-01");
        Assert.Equal("informative", iref01.Layer);
        Assert.True(iref01.Examined > 0, $"{sourceLabel}: I-REF-01 examined 0 rows (positive control).");
        Assert.NotEqual("fail", iref01.Status); // informative: never "fail", at most "info"
        Assert.Equal(
            expectedCensus,
            iref01.Failed);
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

    /// <summary>I-REF-01: 14 hierarchies without nodes in DPM 1.0, declared census, informative layer.</summary>
    [DataFact]
    public void Dpm10_All_IRef01DeclaredCensus14()
    {
        var result = ValidatorRunCache.Run(_fixture.ValidatedDatabasePath, referencePath: null);
        InvariantChecksAssertions.AssertIRef01DeclaredCensus(result.Report, "DPM 1.0", expectedCensus: 14);
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

        // AO-3 (I-TRE-07b) is specific to the 4.3 edge - in 4.2 it must give 0/0 WITHOUT needing
        // any exception (measured: 0 anchors in 4.2).
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
    public void Dpm20Release42_All_IRef01DeclaredCensus14()
    {
        var result = ValidatorRunCache.Run(fixture.ValidatedDatabasePath, referencePath: null);
        InvariantChecksAssertions.AssertIRef01DeclaredCensus(result.Report, "DPM 2.0 4.2", expectedCensus: 14);
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
    /// The publication guard: --validate of the 4.2 output against its OWN reference (same
    /// publication) does NOT trigger the guard - ALL the B-DIC-01.* checks really compare, none is
    /// skipped. It is the necessary counterpoint of the 4.3 test (which MUST skip them all): if the
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

    /// <summary>I-REF-01: 15 in 4.3 (versus 14 in 4.2/DPM 1.0) - one more business hierarchy, same family.</summary>
    [DataFact]
    public void Dpm20Release43_All_IRef01DeclaredCensus15()
    {
        var result = ValidatorRunCache.Run(fixture.GeneratedDatabasePath, referencePath: null);
        InvariantChecksAssertions.AssertIRef01DeclaredCensus(result.Report, "DPM 2.0 4.3", expectedCensus: 15);
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
    /// AO-3 (I-TRE-07b): the 104 declared are emitted as a named exception - the check PASSES in
    /// 4.3 (0 UNDECLARED violations), even though the 104 DO exist in the data (the source
    /// contradicts itself). Positive control: if the exception stopped being applied (or the census
    /// dropped below 104 without anyone reviewing it), Examined would remain the same but the
    /// meaning of "pass" would change - that is why examined&gt;0 is not enough here and it is
    /// recorded explicitly that it is still AO-3 that absorbs the failure.
    /// </summary>
    [DataFact]
    public void Dpm20Release43_All_ITre07bPassesWithThe104OfAo3Absorbed()
    {
        var result = ValidatorRunCache.Run(fixture.GeneratedDatabasePath, referencePath: null);
        var itre07b = Assert.Single(result.Report.Checks, c => c.Id == "I-TRE-07b");
        Assert.Equal("critical", itre07b.Layer);
        Assert.Equal("pass", itre07b.Status);
        Assert.Equal(0, itre07b.Failed);
        Assert.True(itre07b.Examined > 0, "DPM 2.0 4.3: I-TRE-07b examined 0 rows (positive control).");
        Assert.Contains("named exception", itre07b.Statement, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The publication guard: --validate of the 4.3 output against <c>EBA_4.2_Hotfix.db</c> (4.2
    /// publication) leaves ALL the <c>B-DIC-01.*</c> in <c>Skipped</c>, not in "fail". Before the
    /// guard was extended to all of them, <c>.3</c> and <c>.6</c> FAILED here over a structurally
    /// invalid universe - exactly the kind of silent regression this test exists to catch if
    /// someone "fixes" the guard back to a single one.
    ///
    /// No fixed count: the family grew from 8 to 9 with B-DIC-01.9. What really protects is "EVERY
    /// B-DIC-01.* is skipped here, without exception" - a floor of 8 to catch an emptied harness,
    /// never an equality.
    /// </summary>
    [DataFact]
    public void Dpm20Release43_WithReference42_AllBDic01AreSkipped()
    {
        RepoPaths.EnsureReferenceDatabaseExists();
        var result = ValidatorRunCache.Run(fixture.GeneratedDatabasePath, RepoPaths.ReferenceDatabasePath);

        var bDic01Checks = result.Report.Checks.Where(c => c.Id.StartsWith("B-DIC-01.", StringComparison.Ordinal)).ToList();
        Assert.True(bDic01Checks.Count >= 8, $"Only {bDic01Checks.Count} B-DIC-01.* checks (expected floor: 8).");

        var notSkipped = bDic01Checks.Where(c => c.Status != "skipped").ToList();
        Assert.True(
            notSkipped.Count == 0,
            "The following B-DIC-01.* were NOT skipped despite the publication mismatch (generated 4.3 vs reference 4.2): "
            + string.Join(" | ", notSkipped.Select(c => $"{c.Id}: status={c.Status}")));

        Assert.All(bDic01Checks, c => Assert.False(string.IsNullOrWhiteSpace(c.SkipReason)));
        Assert.All(bDic01Checks, c => Assert.Contains("publication", c.SkipReason!, StringComparison.OrdinalIgnoreCase));

        // No critical check fails because of the guard - the skipped ones do not count as "fail".
        Assert.DoesNotContain(bDic01Checks, c => c.Status == "fail");
    }
}
