using EbaDpm.Converter.Core.Sqlite;
using EbaDpm.Converter.Core.Validation;
using EbaDpm.Converter.Core.Validation.Checks;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Tests.Dpm2;

/// <summary>
/// White-box tests on MINIMAL SQLite databases (the empty 45-table schema plus a handful of rows
/// built by hand, without touching the Access database or <c>Data/</c>) that call the check
/// classes (<c>ConceptChecks</c>, <c>DictionaryChecks</c>, <c>DictionaryCensusChecks</c>) DIRECTLY
/// instead of going through <c>Validator.Run</c>/the CLI.
///
/// They protect the decisions that do NOT hold "as is" in the real outputs and that those outputs
/// do not fully exercise - none of the three conversions today has a case of "one of the 59 DD-16
/// hierarchies stops missing". That is a property of the CODE, not of today's data, so they are tested with
/// data fabricated on purpose.
/// </summary>
public sealed class InvariantChecksSyntheticTests : IDisposable
{
    private readonly string _dir;

    public InvariantChecksSyntheticTests()
    {
        _dir = Path.Join(Path.GetTempPath(), $"EbaDpm.InvariantChecksSynthetic_{Environment.ProcessId}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort: a late SQLite handle must not bring down the test suite.
        }
    }

    private SqliteConnection CreateAndOpen(string fileName)
    {
        var path = Path.Join(_dir, fileName);
        SchemaCreator.Create(path, overwrite: true);
        var connectionString = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite }.ToString();
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        // These databases are MINIMAL fixtures on purpose (only the columns each test needs):
        // no active FKs, same as the real output while loading (SchemaCreator.Create leaves
        // "foreign_keys" disabled - a per-connection pragma, it does not persist in the file).
        SqlHelpers.Execute(connection, "PRAGMA foreign_keys = OFF");
        return connection;
    }

    private static void Exec(SqliteConnection c, string sql) => SqlHelpers.Execute(c, sql);

    // -----------------------------------------------------------------------------------------
    // I-CPT-02b: the sentinel 9999 is discounted ONLY in Domain, NEVER in Member.
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// CORRECT form (what is implemented today): mDomain carries the sentinel 9999 but mConcept
    /// 'Domain' does NOT cover it (discount); mMember carries the sentinel 9999 and mConcept
    /// 'Member' DOES cover it (no discount). The check must pass.
    /// </summary>
    [Fact]
    public void ICpt02b_DiscountsTheSentinelOnlyInDomain_NotInMember()
    {
        using var c = CreateAndOpen("concept-sentinel-ok.db");

        Exec(c, "INSERT INTO mDomain (DomainID, DomainCode) VALUES (1, 'D1'), (9999, 'DSENTINEL')");
        Exec(c, "INSERT INTO mConcept (ConceptID, ConceptType) VALUES (1, 'Domain')"); // only the real one: 1 == (2-1)

        Exec(c, "INSERT INTO mMember (MemberID, DomainID, MemberCode) VALUES (1, 1, 'M1'), (9999, 1, 'MSENTINEL')");
        Exec(c, "INSERT INTO mConcept (ConceptID, ConceptType) VALUES (2, 'Member'), (3, 'Member')"); // both: 2 == 2

        var results = ConceptChecks.Run(c, ValidationSourceModel.Dpm2).ToList();
        var icpt02b = Assert.Single(results, r => r.Id == "I-CPT-02b");

        Assert.Equal(CheckStatus.Pass, icpt02b.Status);
        Assert.Equal(0, icpt02b.Failed);
        Assert.True(icpt02b.Examined > 0, "positive control: I-CPT-02b did not examine any of the 13 pairs.");
    }

    /// <summary>
    /// Control: if someone "fixed" the discount back to ALSO covering the sentinel in Domain, the
    /// check must CATCH it - it fails.
    /// </summary>
    [Fact]
    public void ICpt02b_IfTheSentinelStopsBeingDiscountedInDomain_TheCheckFails()
    {
        using var c = CreateAndOpen("concept-sentinel-domain-bug.db");

        Exec(c, "INSERT INTO mDomain (DomainID, DomainCode) VALUES (1, 'D1'), (9999, 'DSENTINEL')");
        // 2 'Domain' concepts == 2 rows of mDomain INCLUDING the sentinel -> no longer discounts.
        Exec(c, "INSERT INTO mConcept (ConceptID, ConceptType) VALUES (1, 'Domain'), (2, 'Domain')");

        var results = ConceptChecks.Run(c, ValidationSourceModel.Dpm2).ToList();
        var icpt02b = Assert.Single(results, r => r.Id == "I-CPT-02b");

        Assert.Equal(CheckStatus.Fail, icpt02b.Status);
        Assert.Equal(1, icpt02b.Failed);
        Assert.Contains(icpt02b.Samples, s => s.BusinessKey.StartsWith("Domain:", StringComparison.Ordinal));
    }

    /// <summary>
    /// Inverse control: if someone EXTENDED the discount to Member (which must not be discounted),
    /// the check must CATCH it - it fails.
    /// </summary>
    [Fact]
    public void ICpt02b_IfTheSentinelIsAlsoDiscountedInMember_TheCheckFails()
    {
        using var c = CreateAndOpen("concept-sentinel-member-bug.db");

        Exec(c, "INSERT INTO mMember (MemberID, DomainID, MemberCode) VALUES (1, 1, 'M1'), (9999, 1, 'MSENTINEL')");
        // 1 'Member' concept != 2 rows of mMember -> as if the sentinel had been discounted.
        Exec(c, "INSERT INTO mConcept (ConceptID, ConceptType) VALUES (2, 'Member')");

        var results = ConceptChecks.Run(c, ValidationSourceModel.Dpm2).ToList();
        var icpt02b = Assert.Single(results, r => r.Id == "I-CPT-02b");

        Assert.Equal(CheckStatus.Fail, icpt02b.Status);
        Assert.Equal(1, icpt02b.Failed);
        Assert.Contains(icpt02b.Samples, s => s.BusinessKey.StartsWith("Member:", StringComparison.Ordinal));
    }

    // -----------------------------------------------------------------------------------------
    // I-TRE-07b: a plain critical check. AO-3 (104 declared nodes at 4.3) no longer exists: the
    // loader re-anchors them (tie-break by consistency), so EVERY node outside its hierarchy's
    // domain (or a domain united to it) is a failure and nothing absorbs it.
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Old: <c>ITre07b_OnlyTheUndeclaredCountsAsAFailure_TheDeclaredOneIsAbsorbed</c> (the former AO-3 key
    /// "GA4:qIO:q1B" was absorbed, only the undeclared one failed: Failed 1).
    /// New: the SAME two nodes now BOTH fail (Failed 2) - the former declared key is not absorbed -
    /// and no exception outcome is reported for I-TRE-07b.
    /// </summary>
    [Fact]
    public void ITre07b_EveryCrossDomainNodeFails_TheFormerlyDeclaredKeyIsNotAbsorbed()
    {
        using var c = CreateAndOpen("itre07b-mixed.db");

        Exec(c, "INSERT INTO mDomain (DomainID, DomainCode) VALUES (1, 'GA4dom'), (2, 'qIO')");
        Exec(c, "INSERT INTO mHierarchy (HierarchyID, HierarchyCode, DomainID) VALUES (1, 'GA4', 1)");
        Exec(c, "INSERT INTO mMember (MemberID, DomainID, MemberCode) VALUES (101, 2, 'q1B'), (102, 2, 'ZZZ_NOT_DECLARED')");
        Exec(c, "INSERT INTO mHierarchyNode (HierarchyID, MemberID, ParentMemberID, Level, Path) VALUES (1, 101, NULL, 1, '101.'), (1, 102, NULL, 1, '102.')");

        var sink = new List<KnownExceptions.Outcome>();
        var results = DictionaryChecks.Run(c, ValidationSourceModel.Dpm2, sink).ToList();
        var itre07b = Assert.Single(results, r => r.Id == "I-TRE-07b");

        Assert.Equal(CheckStatus.Fail, itre07b.Status);
        Assert.Equal(2, itre07b.Failed);
        Assert.Contains(itre07b.Samples, s => s.BusinessKey == "GA4:qIO:ZZZ_NOT_DECLARED");
        Assert.Contains(itre07b.Samples, s => s.BusinessKey == "GA4:qIO:q1B");

        // No exception is consulted by this check.
        Assert.DoesNotContain(sink, o => o.Exception.Check == "I-TRE-07b");
        Assert.DoesNotContain(sink, o => o.Exception.Id == "AO-3");
        Assert.DoesNotContain(KnownExceptions.All, e => e.Id == "AO-3" || e.Check == "I-TRE-07b");
    }

    /// <summary>
    /// Old: <c>ITre07b_DeclaredKeysThatDoNotViolate_AreNotStale</c> (104 AO-3 outcomes, all Matched 0, none
    /// stale). New: with no violating node the check passes (and examines the node: positive control is
    /// the previous test, which makes the same check fail) and the sink carries no I-TRE-07b outcome.
    /// </summary>
    [Fact]
    public void ITre07b_NoCrossDomainNode_Passes_AndReportsNoExceptionOutcome()
    {
        using var c = CreateAndOpen("itre07b-none-violating.db");

        Exec(c, "INSERT INTO mDomain (DomainID, DomainCode) VALUES (1, 'GA4dom')");
        Exec(c, "INSERT INTO mHierarchy (HierarchyID, HierarchyCode, DomainID) VALUES (1, 'GA4', 1)");
        Exec(c, "INSERT INTO mMember (MemberID, DomainID, MemberCode) VALUES (101, 1, 'q1B')");
        Exec(c, "INSERT INTO mHierarchyNode (HierarchyID, MemberID, ParentMemberID, Level, Path) VALUES (1, 101, NULL, 1, '101.')");

        var sink = new List<KnownExceptions.Outcome>();
        var results = DictionaryChecks.Run(c, ValidationSourceModel.Dpm2, sink).ToList();
        var itre07b = Assert.Single(results, r => r.Id == "I-TRE-07b");

        Assert.Equal(CheckStatus.Pass, itre07b.Status);
        Assert.Equal(0, itre07b.Failed);
        Assert.True(itre07b.Examined > 0, "positive control: I-TRE-07b did not examine any mHierarchyNode.");
        Assert.DoesNotContain(sink, o => o.Exception.Check == "I-TRE-07b");
    }

    /// <summary>
    /// Old: <c>ITre07b_IfOnlyTheDeclaredViolationRemains_ItPassesEntirely</c> (the lone former AO-3 key
    /// passed). New: that same lone node, with nothing else in the database, FAILS.
    /// </summary>
    [Fact]
    public void ITre07b_AFormerlyDeclaredKeyAlone_NowFails()
    {
        using var c = CreateAndOpen("itre07b-only-former.db");

        Exec(c, "INSERT INTO mDomain (DomainID, DomainCode) VALUES (1, 'GA4dom'), (2, 'qIO')");
        Exec(c, "INSERT INTO mHierarchy (HierarchyID, HierarchyCode, DomainID) VALUES (1, 'GA4', 1)");
        Exec(c, "INSERT INTO mMember (MemberID, DomainID, MemberCode) VALUES (101, 2, 'q1B')");
        Exec(c, "INSERT INTO mHierarchyNode (HierarchyID, MemberID, ParentMemberID, Level, Path) VALUES (1, 101, NULL, 1, '101.')");

        var results = DictionaryChecks.Run(c, ValidationSourceModel.Dpm2, []).ToList();
        var itre07b = Assert.Single(results, r => r.Id == "I-TRE-07b");

        Assert.Equal(CheckStatus.Fail, itre07b.Status);
        Assert.Equal(1, itre07b.Failed);
        Assert.Equal("GA4:qIO:q1B", Assert.Single(itre07b.Samples).BusinessKey);
    }

    /// <summary>
    /// The mDomainUnion allowance is KEPT: the same node, with the member's domain united to the
    /// hierarchy's, passes. Positive control: <see cref="ITre07b_AFormerlyDeclaredKeyAlone_NowFails"/> is
    /// the identical database without the mDomainUnion row and fails.
    /// </summary>
    [Fact]
    public void ITre07b_MemberInADomainUnitedToTheHierarchyDomain_Passes()
    {
        using var c = CreateAndOpen("itre07b-union.db");

        Exec(c, "INSERT INTO mDomain (DomainID, DomainCode) VALUES (1, 'GA4dom'), (2, 'qIO')");
        Exec(c, "INSERT INTO mDomainUnion (UnionDomainID, UnitedDomainID) VALUES (1, 2)");
        Exec(c, "INSERT INTO mHierarchy (HierarchyID, HierarchyCode, DomainID) VALUES (1, 'GA4', 1)");
        Exec(c, "INSERT INTO mMember (MemberID, DomainID, MemberCode) VALUES (101, 2, 'q1B')");
        Exec(c, "INSERT INTO mHierarchyNode (HierarchyID, MemberID, ParentMemberID, Level, Path) VALUES (1, 101, NULL, 1, '101.')");

        var results = DictionaryChecks.Run(c, ValidationSourceModel.Dpm2, []).ToList();
        var itre07b = Assert.Single(results, r => r.Id == "I-TRE-07b");

        Assert.Equal(CheckStatus.Pass, itre07b.Status);
        Assert.Equal(0, itre07b.Failed);
        Assert.True(itre07b.Examined > 0);
    }

    // -----------------------------------------------------------------------------------------
    // B-DIC-01.8 / DD-16: BOTH sides count - growing (an undeclared one is missing)
    // AND shrinking (one of the 59 declared stops missing) are real signals.
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// LITERAL copy of <c>DictionaryCensusChecks.Dd16LegacyDpm10Hierarchies</c>: if the production
    /// registry changes, this test must be updated at the same time - it is the same deliberately
    /// closed relationship that already exists between <c>AO-1</c> and its census.
    /// </summary>
    private static readonly string[] Dd16LegacyDpm10HierarchiesMirror =
    [
        "AP:AP15", "AP:AP18", "AP:AP24", "AP:AP26_1", "AP:AP30_REL_2",
        "CS:CS4",
        "CU:CU3_1", "CU:CU3_2", "CU:CU3_3", "CU:CU_ALL",
        "GA:GA10", "GA:GA10_1", "GA:GA2", "GA:GA3", "GA:GA5_1", "GA:GA5_2", "GA:GA6_1", "GA:GA9_REL_2",
        "IM:IM50",
        "MC:MC105", "MC:MC140", "MC:MC26", "MC:MC26_1", "MC:MC74", "MC:MC80",
        "MET:AT106", "MET:AT11", "MET:AT6", "MET:AT81", "MET:AT9",
        "NC:NC20", "NC:NC21", "NC:NC30",
        "RP:RP4", "RP:RP7_REL_2",
        "RT:RT1", "RT:RT4",
        "TA:TA32",
        "TI:TI10", "TI:TI11",
        "TR:TR4", "TR:TR7",
        "UE:UE4",
        "ZZ:ZZ13", "ZZ:ZZ14", "ZZ:ZZ18", "ZZ:ZZ23", "ZZ:ZZ27", "ZZ:ZZ3", "ZZ:ZZ33", "ZZ:ZZ50",
        "ZZ:ZZ55", "ZZ:ZZ57", "ZZ:ZZ6", "ZZ:ZZ65", "ZZ:ZZ6_1", "ZZ:ZZ7", "ZZ:ZZ71", "ZZ:ZZ72",
    ];

    private static void InsertHierarchies(SqliteConnection c, IEnumerable<string> businessKeys)
    {
        var domainIds = new Dictionary<string, int>(StringComparer.Ordinal);
        var nextDomainId = 1;
        var nextHierarchyId = 1;
        foreach (var key in businessKeys)
        {
            var parts = key.Split(':', 2);
            var domainCode = parts[0];
            var hierarchyCode = parts[1];
            if (!domainIds.TryGetValue(domainCode, out var domainId))
            {
                domainId = nextDomainId++;
                domainIds[domainCode] = domainId;
                Exec(c, $"INSERT INTO mDomain (DomainID, DomainCode) VALUES ({domainId}, '{domainCode}')");
            }

            Exec(c, $"INSERT INTO mHierarchy (HierarchyID, HierarchyCode, DomainID) VALUES ({nextHierarchyId++}, '{hierarchyCode}', {domainId})");
        }
    }

    /// <summary>
    /// Reference = the 59 declared DD-16 + 1 new UNDECLARED key. Generated = ONLY "CS:CS4" (one of
    /// the 59, which "stops missing" BECAUSE WE ALREADY GENERATE IT) and the mRelease 4.2 IsCurrent,
    /// to pass the publication guard. The other 57 declared keep missing - they must not count. The
    /// new UNDECLARED one MUST count (it grows). "CS:CS4" (declared, stops missing because we
    /// generate it) MUST ALSO count (two-sided census).
    ///
    /// "No longer missing" has THREE states, not two - the message distinguishes "we already
    /// generate it" (this case) from "it is no longer in the reference"
    /// (<see cref="BDic018_Dd16DistinguishesTheThirdState_NoLongerInTheReference"/>), which is a
    /// different signal (the reference changed, not us).
    /// </summary>
    [Fact]
    public void BDic018_Dd16HasBothSides_GrowingAndShrinkingCountAsAFailure()
    {
        using var generated = CreateAndOpen("dd16-generated.db");
        using var reference = CreateAndOpen("dd16-reference.db");

        Exec(generated, "INSERT INTO mRelease (ReleaseID, ReleaseCode, IsCurrent) VALUES (1, '4.2', 1)");

        InsertHierarchies(reference, Dd16LegacyDpm10HierarchiesMirror.Append("ZZ:ZZ_NOT_DECLARED"));
        InsertHierarchies(generated, ["CS:CS4"]);

        var sink = new List<KnownExceptions.Outcome>();
        var results = DictionaryCensusChecks.Run(generated, reference, ReferenceRole.Reference42, sink).ToList();
        var bdic018 = Assert.Single(results, r => r.Id == "B-DIC-01.8");

        Assert.Equal(CheckStatus.Fail, bdic018.Status);
        Assert.Equal(2, bdic018.Failed);
        Assert.Contains(bdic018.Samples, s => s.BusinessKey == "ZZ:ZZ_NOT_DECLARED");
        Assert.Contains(bdic018.Samples, s => s.BusinessKey.Contains("CS:CS4", StringComparison.Ordinal) && s.BusinessKey.Contains("we now generate it", StringComparison.Ordinal));

        // The other 57 declared ones keep missing (not generated) - they must not appear as a failure.
        foreach (var stillMissing in Dd16LegacyDpm10HierarchiesMirror.Where(k => k != "CS:CS4"))
        {
            Assert.DoesNotContain(bdic018.Samples, s => s.BusinessKey == stillMissing);
        }
    }

    /// <summary>
    /// The THIRD state: one of the 59 declared stops missing because THE REFERENCE stopped
    /// publishing it - not because we generate it. Reference = 58 of the 59 ("CS:CS4" removed
    /// entirely, as if the reference withdrew it). Generated = none (we still do not produce
    /// "CS:CS4", as before). The message must distinguish this case from "we now generate it":
    /// they are different signals - here the reference changed, not our mapping.
    /// </summary>
    [Fact]
    public void BDic018_Dd16DistinguishesTheThirdState_NoLongerInTheReference()
    {
        using var generated = CreateAndOpen("dd16-generated-3state.db");
        using var reference = CreateAndOpen("dd16-reference-3state.db");

        Exec(generated, "INSERT INTO mRelease (ReleaseID, ReleaseCode, IsCurrent) VALUES (1, '4.2', 1)");

        InsertHierarchies(reference, Dd16LegacyDpm10HierarchiesMirror.Where(k => k != "CS:CS4"));
        // Generated: empty - "CS:CS4" is NOT generated (unlike the previous test).

        var sink = new List<KnownExceptions.Outcome>();
        var results = DictionaryCensusChecks.Run(generated, reference, ReferenceRole.Reference42, sink).ToList();
        var bdic018 = Assert.Single(results, r => r.Id == "B-DIC-01.8");

        Assert.Equal(CheckStatus.Fail, bdic018.Status);
        Assert.Equal(1, bdic018.Failed);
        Assert.Contains(
            bdic018.Samples,
            s => s.BusinessKey.Contains("CS:CS4", StringComparison.Ordinal)
                 && s.BusinessKey.Contains("no longer in the reference", StringComparison.Ordinal));

        // The exact distinction: it must NOT be confused with "we now generate it" - we still
        // do not produce "CS:CS4", the change is in the reference.
        Assert.DoesNotContain(bdic018.Samples, s => s.BusinessKey.Contains("we now generate it", StringComparison.Ordinal));

        // The other 58 declared ones keep missing (still in the reference, still not generated) - they must not appear.
        foreach (var stillMissing in Dd16LegacyDpm10HierarchiesMirror.Where(k => k != "CS:CS4"))
        {
            Assert.DoesNotContain(bdic018.Samples, s => s.BusinessKey == stillMissing);
        }
    }

    /// <summary>
    /// Positive-control counterpoint: the reference carries ONLY the 59 declared and the generated
    /// NONE of them - exactly the real state measured today in the DPM 2.0 4.2 output ("0 of 59
    /// present"). The 59 remain legitimately absent (neither side activated), so the check passes
    /// entirely. It confirms that the <c>Failed=2</c> of the first test comes specifically from the
    /// two sides (growing/shrinking) and not from publication-guard noise or a latent "always
    /// fails".
    ///
    /// Deliberately NOT tested here: "reference and generated with the same 59" (the 59 fully
    /// generated): with the current reading of <c>HierarchyCensus</c>, that scenario MUST fail with
    /// the 59 as "no longer missing" - it is exactly the signal that must speak up, not a case to
    /// whitewash as if it should pass.
    /// </summary>
    [Fact]
    public void BDic018_WithNeitherSideActivated_Passes()
    {
        using var generated = CreateAndOpen("dd16-generated-clean.db");
        using var reference = CreateAndOpen("dd16-reference-clean.db");

        Exec(generated, "INSERT INTO mRelease (ReleaseID, ReleaseCode, IsCurrent) VALUES (1, '4.2', 1)");

        InsertHierarchies(reference, Dd16LegacyDpm10HierarchiesMirror);
        // Generated: none of the 59 (real state measured today) - InsertHierarchies is not
        // called, mHierarchy/mDomain stay empty in the generated database.

        var sink = new List<KnownExceptions.Outcome>();
        var results = DictionaryCensusChecks.Run(generated, reference, ReferenceRole.Reference42, sink).ToList();
        var bdic018 = Assert.Single(results, r => r.Id == "B-DIC-01.8");

        Assert.Equal(CheckStatus.Pass, bdic018.Status);
        Assert.Equal(0, bdic018.Failed);
        Assert.True(bdic018.Examined > 0, "positive control: B-DIC-01.8 did not examine any reference mHierarchy.");
    }

    // -----------------------------------------------------------------------------------------
    // B-DIC-01.9 / E-7: GeneratedCensus mode - neither containment nor divergence. BOTH halves
    // are a real signal (Sidedness.TwoSided): ceasing to emit one of the 109 is a REGRESSION
    // (exactly what the entry exists to prevent); the reference starting to publish one of the 109
    // makes the entry OBSOLETE (the reference defect was fixed).
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// LITERAL copy of <c>KnownExceptions.E7Keys</c> (109 entries): if the production registry
    /// changes, this test must be updated at the same time - same deliberate relationship that
    /// already exists between <c>AO-1</c>/<c>DD-16</c> and their censuses.
    /// </summary>
    private static readonly string[] E7KeysMirror =
    [
        "eba_qIO:qx2000", "eba_qIO:qx2001", "eba_qIO:qx2002", "eba_qIO:qx2003", "eba_qIO:qx2004", "eba_qIO:qx2005",
        "eba_qIO:qx2006", "eba_qIO:qx2007", "eba_qIO:qx2008", "eba_qIO:qx2009", "eba_qIO:qx2010", "eba_qIO:qx2011",
        "eba_qIO:qx2012", "eba_qIO:qx2013", "eba_qIO:qx2014", "eba_qIO:qx2015", "eba_qIO:qx2016", "eba_qIO:qx2017",
        "eba_qIO:qx2018", "eba_qIO:qx2019", "eba_qIO:qx2020", "eba_qIO:qx2021", "eba_qIO:qx2022", "eba_qIO:qx2023",
        "eba_qIO:qx2024", "eba_qIO:qx2025", "eba_qIO:qx2026", "eba_qIO:qx2027", "eba_qIO:qx2028", "eba_qIO:qx2029",
        "eba_qIO:qx2030", "eba_qIO:qx2031", "eba_qIO:qx2032", "eba_qIO:qx2033", "eba_qIO:qx2034", "eba_qIO:qx2035",
        "eba_qIO:qx2036", "eba_qIO:qx2037", "eba_qIO:qx2038", "eba_qIO:qx2039", "eba_qIO:qx2040", "eba_qIO:qx2041",
        "eba_qIO:qx2042", "eba_qIO:qx2043", "eba_qIO:qx2044", "eba_qIO:qx2045", "eba_qIO:qx2046", "eba_qIO:qx2047",
        "eba_qIO:qx2048", "eba_qIO:qx2049", "eba_qIO:qx2050", "eba_qIO:qx2051", "eba_qIO:qx2052", "eba_qIO:qx2053",
        "eba_qIO:qx2054", "eba_qIO:qx2055", "eba_qIO:qx2056", "eba_qIO:qx2057", "eba_qIO:qx2058", "eba_qIO:qx2059",
        "eba_qIO:qx2060", "eba_qIO:qx2061", "eba_qIO:qx2062", "eba_qIO:qx2063", "eba_qIO:qx2064", "eba_qIO:qx2065",
        "eba_qIO:qx2066", "eba_qIO:qx2067", "eba_qIO:qx2068", "eba_qIO:qx2069", "eba_qIO:qx2070", "eba_qIO:qx2071",
        "eba_qIO:qx2072", "eba_qIO:qx2073", "eba_qIO:qx2074", "eba_qIO:qx2075", "eba_qIO:qx2076", "eba_qIO:qx2077",
        "eba_qIO:qx2078", "eba_qIO:qx2079", "eba_qIO:qx2080", "eba_qIO:qx2081", "eba_qIO:qx2082", "eba_qIO:qx2083",
        "eba_qIO:qx2084", "eba_qIO:qx2085", "eba_qIO:qx2086", "eba_qIO:qx2087", "eba_qIO:qx2088", "eba_qIO:qx2089",
        "eba_qIO:qx2090", "eba_qIO:qx2091", "eba_qIO:qx2092", "eba_qIO:qx2093", "eba_qIO:qx2094", "eba_qIO:qx2095",
        "eba_qIO:qx2096", "eba_qIO:qx2097", "eba_qIO:qx2098", "eba_qIO:qx2099", "eba_qIO:qx2100", "eba_qIO:qx2101",
        "eba_qIO:qx2102", "eba_qIO:qx2103", "eba_qIO:qx2104", "eba_qIO:qx2105", "eba_qIO:qx2106", "eba_qIO:qx2107",
        "eba_qIO:qx0",
    ];

    /// <summary>Inserts a minimal <c>mDomain</c> (once) and one <c>mMember</c> for each given <c>MemberXBRLCode</c>.</summary>
    private static void InsertMembersWithXbrlCodes(SqliteConnection c, IEnumerable<string> xbrlCodes)
    {
        Exec(c, "INSERT INTO mDomain (DomainID, DomainCode) VALUES (1, 'QIODOM')");
        var nextMemberId = 1;
        foreach (var code in xbrlCodes)
        {
            Exec(c, $"INSERT INTO mMember (MemberID, DomainID, MemberCode, MemberXBRLCode) VALUES ({nextMemberId}, 1, 'M{nextMemberId}', '{code}')");
            nextMemberId++;
        }
    }

    /// <summary>
    /// CLEAN state: the 109 of E-7 emitted in the generated database, none published by the
    /// reference - exactly what was measured on the real DPM 2.0 4.2 output (109/109). Positive
    /// control for the rest of the tests of this block: if this did not pass, "Failed" in the others
    /// would not distinguish the phenomenon each one tests from background noise.
    /// </summary>
    [Fact]
    public void BDic019_E7GeneratedCensus_State109Of109_Passes()
    {
        using var generated = CreateAndOpen("e7-generated-clean.db");
        using var reference = CreateAndOpen("e7-reference-clean.db");

        Exec(generated, "INSERT INTO mRelease (ReleaseID, ReleaseCode, IsCurrent) VALUES (1, '4.2', 1)");
        InsertMembersWithXbrlCodes(generated, E7KeysMirror);
        // Reference: none of the 109 (real state measured) - mMember stays empty.

        var sink = new List<KnownExceptions.Outcome>();
        var results = DictionaryCensusChecks.Run(generated, reference, ReferenceRole.Reference42, sink).ToList();
        var bdic019 = Assert.Single(results, r => r.Id == "B-DIC-01.9");

        Assert.Equal(CheckStatus.Pass, bdic019.Status);
        Assert.Equal(0, bdic019.Failed);
        Assert.True(bdic019.Examined > 0, "positive control: B-DIC-01.9 did not examine any declared E-7 key.");
    }

    /// <summary>
    /// Half 1 - REGRESSION: we stop emitting one of the 109 ("eba_qIO:qx2050"). It is exactly what
    /// the E-7 entry exists to prevent: it must fail, with a message that names it a regression,
    /// not "obsolete".
    /// </summary>
    [Fact]
    public void BDic019_E7GeneratedCensus_CeasingToEmitOne_IsARegressionAndFails()
    {
        using var generated = CreateAndOpen("e7-generated-regression.db");
        using var reference = CreateAndOpen("e7-reference-regression.db");

        Exec(generated, "INSERT INTO mRelease (ReleaseID, ReleaseCode, IsCurrent) VALUES (1, '4.2', 1)");
        InsertMembersWithXbrlCodes(generated, E7KeysMirror.Where(k => k != "eba_qIO:qx2050"));

        var sink = new List<KnownExceptions.Outcome>();
        var results = DictionaryCensusChecks.Run(generated, reference, ReferenceRole.Reference42, sink).ToList();
        var bdic019 = Assert.Single(results, r => r.Id == "B-DIC-01.9");

        Assert.Equal(CheckStatus.Fail, bdic019.Status);
        Assert.Equal(1, bdic019.Failed);
        Assert.Contains(
            bdic019.Samples,
            s => s.BusinessKey.Contains("eba_qIO:qx2050", StringComparison.Ordinal)
                 && s.BusinessKey.Contains("regression", StringComparison.Ordinal)
                 && s.BusinessKey.Contains("NO LONGER emit", StringComparison.Ordinal));

        // The other 108 are still emitted - they must not appear as a failure.
        foreach (var stillEmitted in E7KeysMirror.Where(k => k != "eba_qIO:qx2050"))
        {
            Assert.DoesNotContain(bdic019.Samples, s => s.BusinessKey.Contains(stillEmitted, StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// Half 2 - OBSOLETE: the reference starts publishing one of the 109 ("eba_qIO:qx2050"), without
    /// us ceasing to emit it. The reference defect was fixed: the entry became obsolete, a real
    /// signal, with a message different from the regression one.
    /// </summary>
    [Fact]
    public void BDic019_E7GeneratedCensus_ReferenceStartsPublishingOne_BecomesObsoleteAndFails()
    {
        using var generated = CreateAndOpen("e7-generated-obsolete.db");
        using var reference = CreateAndOpen("e7-reference-obsolete.db");

        Exec(generated, "INSERT INTO mRelease (ReleaseID, ReleaseCode, IsCurrent) VALUES (1, '4.2', 1)");
        InsertMembersWithXbrlCodes(generated, E7KeysMirror);
        InsertMembersWithXbrlCodes(reference, ["eba_qIO:qx2050"]);

        var sink = new List<KnownExceptions.Outcome>();
        var results = DictionaryCensusChecks.Run(generated, reference, ReferenceRole.Reference42, sink).ToList();
        var bdic019 = Assert.Single(results, r => r.Id == "B-DIC-01.9");

        Assert.Equal(CheckStatus.Fail, bdic019.Status);
        Assert.Equal(1, bdic019.Failed);
        Assert.Contains(
            bdic019.Samples,
            s => s.BusinessKey.Contains("eba_qIO:qx2050", StringComparison.Ordinal)
                 && s.BusinessKey.Contains("obsolete", StringComparison.Ordinal)
                 && s.BusinessKey.Contains("ALREADY publishes", StringComparison.Ordinal));

        // The exact distinction that matters: it must NOT be read as a regression - we still emit it.
        Assert.DoesNotContain(bdic019.Samples, s => s.BusinessKey.Contains("regression", StringComparison.Ordinal));

        // The other 108 must not appear as a failure.
        foreach (var stillClean in E7KeysMirror.Where(k => k != "eba_qIO:qx2050"))
        {
            Assert.DoesNotContain(bdic019.Samples, s => s.BusinessKey.Contains(stillClean, StringComparison.Ordinal));
        }
    }

    // -----------------------------------------------------------------------------------------
    // I-XBR-01c / AO-1 (DPM 1.0 path): TwoSided - growth AND shrinkage count, and the outcomes
    // reach the sink so the validator can gate on a stale entry.
    // -----------------------------------------------------------------------------------------

    private const int Ao1Count = 50;

    /// <summary>
    /// One AO-1 key ("qIT:TI:x19": DomainCode "qIT", MemberCode "TI:x19", MemberXBRLCode whose local
    /// part "x19" differs from the MemberCode) still violates; the other 49 do not exist in the
    /// fixture. Outcome of the violating key: Matched 1, not stale. The 49 others: stale. The check
    /// fails with exactly those 49 (shrinks), and the matched one is not reported as a sample.
    /// </summary>
    [Fact]
    public void IXbr01c_Dpm1_AViolatingAo1Key_IsMatchedAndNotStale()
    {
        using var c = CreateAndOpen("ao1-one-violating.db");

        Exec(c, "INSERT INTO mDomain (DomainID, DomainCode) VALUES (1, 'qIT')");
        Exec(c, "INSERT INTO mMember (MemberID, DomainID, MemberCode, MemberXBRLCode) VALUES (1, 1, 'TI:x19', 'eba_TI:x19')");

        var sink = new List<KnownExceptions.Outcome>();
        var results = IntegrityChecks.Run(c, ValidationSourceModel.Dpm1, sink).ToList();
        var ixbr = Assert.Single(results, r => r.Id == "I-XBR-01c");

        var matched = Assert.Single(sink, o => o.Exception.Id == "AO-1" && o.Exception.BusinessKey == "qIT:TI:x19");
        Assert.True(matched.AppliesToThisReference);
        Assert.Equal(1, matched.Matched);
        Assert.False(matched.Stale);

        var ao1 = sink.Where(o => o.Exception.Id == "AO-1").ToList();
        Assert.Equal(Ao1Count, ao1.Count);
        Assert.Equal(Ao1Count - 1, ao1.Count(o => o.Stale));
        Assert.All(ao1.Where(o => o.Exception.BusinessKey != "qIT:TI:x19"), o =>
        {
            Assert.True(o.Stale);
            Assert.Equal(0, o.Matched);
        });

        // Exactly the 49 stale keys fail the check (TwoSided); the matched one does not.
        Assert.Equal(CheckStatus.Fail, ixbr.Status);
        Assert.Equal(Ao1Count - 1, ixbr.Failed);
        Assert.DoesNotContain(ixbr.Samples, s => s.BusinessKey.Contains("qIT:TI:x19", StringComparison.Ordinal));
        Assert.Contains(ixbr.Samples, s => s.BusinessKey.Contains("qIT:TI:x102", StringComparison.Ordinal) && s.BusinessKey.Contains("no longer violates", StringComparison.Ordinal));
    }

    /// <summary>
    /// No mMember violates at all: every one of the 50 AO-1 keys is stale (TwoSided) and the check
    /// fails with 50. Positive control for the previous test (same code path, opposite outcome).
    /// </summary>
    [Fact]
    public void IXbr01c_Dpm1_WhenNoAo1KeyViolates_AllFiftyAreStale()
    {
        using var c = CreateAndOpen("ao1-none-violating.db");

        Exec(c, "INSERT INTO mDomain (DomainID, DomainCode) VALUES (1, 'qIT')");
        Exec(c, "INSERT INTO mMember (MemberID, DomainID, MemberCode, MemberXBRLCode) VALUES (1, 1, 'x19', 'eba_TI:x19')");

        var sink = new List<KnownExceptions.Outcome>();
        var results = IntegrityChecks.Run(c, ValidationSourceModel.Dpm1, sink).ToList();
        var ixbr = Assert.Single(results, r => r.Id == "I-XBR-01c");

        var ao1 = sink.Where(o => o.Exception.Id == "AO-1").ToList();
        Assert.Equal(Ao1Count, ao1.Count);
        Assert.All(ao1, o =>
        {
            Assert.True(o.Stale);
            Assert.Equal(0, o.Matched);
        });
        Assert.Contains(ao1, o => o.Exception.BusinessKey == "qIT:TI:x19");

        Assert.Equal(CheckStatus.Fail, ixbr.Status);
        Assert.Equal(Ao1Count, ixbr.Failed);
    }
}
