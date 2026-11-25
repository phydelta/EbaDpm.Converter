using EbaDpm.Converter.Core.Validation;
using EbaDpm.Converter.Native.PlaneC;

namespace EbaDpm.Converter.Native.Tests.PlaneC;

/// <summary>
/// The native plane C census of known divergences (docs/validation.md, "Known divergences"): the
/// file format, and what an entry does to a check. An entry names ONE object by business key; a stale
/// entry of an evaluated release fails; an entry of a release that was not evaluated is not applicable.
/// </summary>
public sealed class NativePlaneCCensusTests
{
    // ------------------------------------------------------------------ the format

    [Fact]
    public void Parse_ReadsTheReleaseTheCommentsAndBothKindsOfRow()
    {
        var census = NativePlaneCCensuses.Parse(
            "# CorpusRelease: 4.4 (measured 2026-11-25)\n" +
            "# a comment\n" +
            "\n" +
            "TableCode\tType\tLayoutSignature\n" +
            "T_01.00\tno-match\tMET(qAAA)|qDIM(qM1)\n" +
            "T_01.00\tcontradicts\tMET(qAAA)|qDIM(qM2)\tarbitrated: layout defect\n" +
            "T_02.00\thierarchy\t4.4|X|0010|qAAA\tlayout cites the wrong hierarchy\n",
            "census-test.tsv");

        Assert.Equal("4.4", census.CorpusRelease);
        Assert.Equal("census-test.tsv", census.SourceName);
        Assert.Equal(3, census.Entries.Count);

        var first = census.Entries[0];
        Assert.Equal("NDD-23", first.Id);
        Assert.Equal("C-DPS-01", first.Check);
        Assert.Equal("T_01.00\tMET(qAAA)|qDIM(qM1)", first.BusinessKey);
        Assert.Contains("census-test.tsv", first.Reason); // a row without a reason carries the reason of the file.

        var second = census.Entries[1];
        Assert.Equal("T_01.00\tMET(qAAA)|qDIM(qM2)", second.BusinessKey);
        Assert.Equal("arbitrated: layout defect", second.Reason);

        var hierarchy = census.Entries[2];
        Assert.Equal("NDD-27", hierarchy.Id);
        Assert.Equal("C-MHR-01", hierarchy.Check);
        Assert.Equal("T_02.00\t4.4|X|0010|qAAA", hierarchy.BusinessKey);
        Assert.Equal("layout cites the wrong hierarchy", hierarchy.Reason);
    }

    [Fact]
    public void Parse_TheTypeIsADiagnosis_NotPartOfTheKey()
    {
        var a = NativePlaneCCensuses.Parse("# CorpusRelease: 4.4\nT_01.00\tno-match\tMET(q1)");
        var b = NativePlaneCCensuses.Parse("# CorpusRelease: 4.4\nT_01.00\tcontradicts\tMET(q1)");

        Assert.Equal(a.Entries.Single().BusinessKey, b.Entries.Single().BusinessKey);
    }

    [Fact]
    public void Parse_WithoutTheReleaseHeader_IsAnError()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            NativePlaneCCensuses.Parse("TableCode\tType\tLayoutSignature\nT_01.00\tno-match\tMET(q1)\n", "no-header.tsv"));

        Assert.Contains("no-header.tsv", exception.Message);
        Assert.Contains("# CorpusRelease", exception.Message);
    }

    [Fact]
    public void Parse_WithAnEmptyReleaseHeader_IsAnError() =>
        Assert.Throws<InvalidOperationException>(() => NativePlaneCCensuses.Parse("# CorpusRelease:   \nT_01.00\tno-match\tMET(q1)"));

    [Theory]
    [InlineData("T_01.00\tunknown-type\tMET(q1)")]               // type not contradicts|no-match|hierarchy.
    [InlineData("T_01.00\tno-match")]                             // two columns.
    [InlineData("T_01.00\tno-match\tMET(q1)\treason\textra")]     // five columns.
    [InlineData("\tno-match\tMET(q1)")]                           // empty table code.
    [InlineData("T_01.00\tno-match\t")]                           // empty signature.
    [InlineData("T_01.00\tno-match\tMET(q1)\t ")]                 // a reason, when present, is not blank.
    [InlineData("T_01.00\thierarchy\t4.4|X|0010|qAAA")]           // a hierarchy row needs a reason.
    [InlineData("T_01.00\thierarchy\t4.4|X|0010|qAAA\t ")]        // and not a blank one.
    [InlineData("T_01.00\thierarchy\t\treason")]                  // and a key.
    public void Parse_MalformedRow_IsAnError_NeverSkipped(string row)
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            NativePlaneCCensuses.Parse("# CorpusRelease: 4.4\n" + row, "bad.tsv"));

        Assert.Contains("Malformed row in bad.tsv", exception.Message);
    }

    [Fact]
    public void LoadAll_TwoCensusesOfTheSameRelease_IsAnError_AndDifferentReleasesAreKept()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => NativePlaneCCensuses.LoadAll(
        [
            ("a.tsv", "# CorpusRelease: 4.4\nT_01.00\tno-match\tMET(q1)"),
            ("b.tsv", "# CorpusRelease: 4.4\nT_02.00\tno-match\tMET(q1)"),
        ]));
        Assert.Contains("a.tsv", exception.Message);
        Assert.Contains("b.tsv", exception.Message);

        var both = NativePlaneCCensuses.LoadAll(
        [
            ("a.tsv", "# CorpusRelease: 4.3\nT_01.00\tno-match\tMET(q1)"),
            ("b.tsv", "# CorpusRelease: 4.4\nT_02.00\tno-match\tMET(q1)"),
        ]);
        Assert.Equal(["4.3", "4.4"], both.Select(c => c.CorpusRelease).ToArray());
    }

    [Fact]
    public void EmbeddedCensuses_Load_AndEveryReleaseIsDeclaredOnce()
    {
        var censuses = NativePlaneCCensuses.EmbeddedCensuses; // throws on a malformed or duplicate file.

        Assert.Equal(censuses.Count, censuses.Select(c => c.CorpusRelease).Distinct(StringComparer.Ordinal).Count());
        foreach (var census in censuses)
        {
            Assert.All(census.Entries, e => Assert.False(string.IsNullOrWhiteSpace(e.Reason)));
        }
    }

    [Fact]
    public void Apply_WithoutACensus_ExemptsNothing_AndAStaleEntryIsReported()
    {
        var violations = new HashSet<string> { "T_01.00\tMET(q1)", "T_01.00\tMET(q2)" };

        var (none, matchedNone) = NativePlaneCCensuses.Apply("C-DPS-01", null, violations);
        Assert.Equal(["T_01.00\tMET(q1)", "T_01.00\tMET(q2)"], none);
        Assert.Empty(matchedNone);

        var census = NativePlaneCCensuses.Parse(
            "# CorpusRelease: 4.4\nT_01.00\tno-match\tMET(q1)\nT_01.00\tno-match\tMET(q3)\nT_01.00\thierarchy\t4.4|X|0010|qAAA\treason");
        var (unresolved, matched) = NativePlaneCCensuses.Apply("C-DPS-01", census, violations);

        // q1 is exempt; q2 still fails; q3 is stale; the hierarchy entry belongs to another check.
        Assert.Equal(["T_01.00\tMET(q2)", "(absent, NDD-23 no longer violates) T_01.00\tMET(q3)"], unresolved);
        Assert.Equal(["T_01.00\tMET(q1)"], matched.ToArray());
    }

    // ------------------------------------------------------------------ effect on C-DPS-01

    private static (SyntheticNativeFile Native, SyntheticLayoutRepository Layouts) ViolatingPair(string table = "T_01.00")
    {
        var native = new SyntheticNativeFile();
        PlaneCTestHelpers.BasicTable(native, "qM1", table);
        PlaneCTestHelpers.Existing(native, "MEM", "qM2");
        var layouts = new SyntheticLayoutRepository();
        layouts.SignatureCell(layouts.Sheet("4.4", table), "MET(qAAA)", "qDIM(qM2)"); // contradicts the native qM1.
        return (native, layouts);
    }

    private const string ViolationKey = "MET(qAAA)|qDIM(qM2)";

    [Fact]
    public void NoCensus_TheViolationFails_AndTheStatementSaysThereWasNoCensus()
    {
        var (native, layouts) = ViolatingPair();
        using (native)
        using (layouts)
        {
            var result = PlaneCTestHelpers.Run(native, layouts);

            var check = PlaneCTestHelpers.Check(result, "C-DPS-01");
            Assert.Equal(CheckStatus.Fail, check.Status);
            Assert.Contains("no native census declared for release 4.4", check.Statement);
            Assert.Empty(result.Divergences);
        }
    }

    [Fact]
    public void AppliedEntry_ExcludesItsViolation_AndIsReportedAsApplied()
    {
        var (native, layouts) = ViolatingPair();
        using (native)
        using (layouts)
        {
            var census = PlaneCTestHelpers.Census("4.4", $"T_01.00\tcontradicts\t{ViolationKey}\tarbitrated: layout defect");

            var result = PlaneCTestHelpers.Run(native, layouts, census);

            var check = PlaneCTestHelpers.Check(result, "C-DPS-01");
            Assert.Equal(CheckStatus.Pass, check.Status);
            Assert.Equal(1, check.Examined);
            var divergence = Assert.Single(result.Divergences);
            Assert.Equal("NDD-23", divergence.Id);
            Assert.Equal($"T_01.00\t{ViolationKey}", divergence.BusinessKey);
            Assert.Equal("C-DPS-01", divergence.Scope);
            Assert.True(divergence.AppliesToThisReference);
            Assert.True(divergence.Evaluated);
            Assert.Equal(1, divergence.Matched);
            Assert.False(divergence.Stale);
            Assert.Contains("arbitrated: layout defect", divergence.Reason);
        }
    }

    [Theory]
    [InlineData("T_02.00", "MET(qAAA)|qDIM(qM2)")]   // same signature, another table.
    [InlineData("T_01.00", "MET(qAAA)|qDIM(qM3)")]   // same table, another signature.
    public void AnEntryNamesOneObject_AnotherTableOrSignatureDoesNotExemptTheViolation(string table, string signature)
    {
        var (native, layouts) = ViolatingPair();
        using (native)
        using (layouts)
        {
            var census = PlaneCTestHelpers.Census("4.4", $"{table}\tcontradicts\t{signature}\tnot this object");

            var result = PlaneCTestHelpers.Run(native, layouts, census);

            var check = PlaneCTestHelpers.Check(result, "C-DPS-01");
            Assert.Equal(CheckStatus.Fail, check.Status);
            // The violation stays, and the entry that matched nothing is stale: two failures.
            Assert.Equal(2, check.Failed);
            Assert.Contains(check.Samples, s => s.BusinessKey == $"4.4|T_01.00\t{ViolationKey}");
            Assert.Contains(check.Samples, s => s.BusinessKey.StartsWith("4.4|(absent, NDD-23 no longer violates)", StringComparison.Ordinal));
            Assert.True(Assert.Single(result.Divergences).Stale);
        }
    }

    [Fact]
    public void StaleEntry_OfAnEvaluatedRelease_FailsTheCheckEvenWhenNothingElseViolates()
    {
        using var native = new SyntheticNativeFile();
        PlaneCTestHelpers.BasicTable(native); // qM1: the layout below is correct.
        using var layouts = new SyntheticLayoutRepository();
        layouts.SignatureCell(layouts.Sheet("4.4", "T_01.00"), "MET(qAAA)", "qDIM(qM1)");
        var census = PlaneCTestHelpers.Census("4.4", "T_01.00\tno-match\tMET(qAAA)|qDIM(qM2)\tused to violate");

        var result = PlaneCTestHelpers.Run(native, layouts, census);

        var check = PlaneCTestHelpers.Check(result, "C-DPS-01");
        Assert.Equal(CheckStatus.Fail, check.Status);
        Assert.Equal(ValidationLayer.Critical, check.Layer);
        Assert.Equal(1, check.Failed);
        Assert.Equal("4.4|(absent, NDD-23 no longer violates) T_01.00\tMET(qAAA)|qDIM(qM2)", Assert.Single(check.Samples).BusinessKey);
        var divergence = Assert.Single(result.Divergences);
        Assert.True(divergence.Stale);
        Assert.True(divergence.Evaluated);
        Assert.Equal(0, divergence.Matched);
    }

    [Fact]
    public void EntryOfAReleaseThatWasNotEvaluated_IsNotApplicable_NeverStale()
    {
        using var native = new SyntheticNativeFile();
        PlaneCTestHelpers.BasicTable(native);
        using var layouts = new SyntheticLayoutRepository();
        layouts.SignatureCell(layouts.Sheet("4.4", "T_01.00"), "MET(qAAA)", "qDIM(qM1)"); // only 4.4 is in the layouts.
        var census = PlaneCTestHelpers.Census("4.3", "T_01.00\tno-match\tMET(qAAA)|qDIM(qM2)\tbelongs to the 4.3 layouts");

        var result = PlaneCTestHelpers.Run(native, layouts, census);

        Assert.Equal(CheckStatus.Pass, PlaneCTestHelpers.Check(result, "C-DPS-01").Status);
        var divergence = Assert.Single(result.Divergences);
        Assert.False(divergence.AppliesToThisReference);
        Assert.False(divergence.Stale);
        Assert.True(divergence.Evaluated); // the check evaluated a release (4.4), just not the one of this entry.
    }

    [Fact]
    public void CensusOfAnotherRelease_DoesNotExemptAViolationOfTheReleaseEvaluated()
    {
        var (native, layouts) = ViolatingPair();
        using (native)
        using (layouts)
        {
            var census = PlaneCTestHelpers.Census("4.3", $"T_01.00\tcontradicts\t{ViolationKey}\tonly for 4.3");

            var check = PlaneCTestHelpers.Check(PlaneCTestHelpers.Run(native, layouts, census), "C-DPS-01");

            Assert.Equal(CheckStatus.Fail, check.Status);
            Assert.Equal(1, check.Failed);
            Assert.Contains("no native census declared for release 4.4", check.Statement);
        }
    }

    [Fact]
    public void WhenNoTableIsCompared_NoEntryIsEvaluated_AndNothingExpires()
    {
        using var native = new SyntheticNativeFile();
        PlaneCTestHelpers.BasicTable(native);
        using var layouts = new SyntheticLayoutRepository();
        layouts.SignatureCell(layouts.Sheet("9.9", "T_01.00"), "MET(qAAA)", "qDIM(qM1)"); // release not in the file.
        var census = PlaneCTestHelpers.Census("9.9", "T_01.00\tno-match\tMET(qAAA)|qDIM(qM2)\treason");

        var result = PlaneCTestHelpers.Run(native, layouts, census);

        Assert.Equal(CheckStatus.Skipped, PlaneCTestHelpers.Check(result, "C-DPS-01").Status);
        var divergence = Assert.Single(result.Divergences);
        Assert.False(divergence.Stale);
        Assert.False(divergence.Evaluated);
    }

    // ------------------------------------------------------------------ effect on C-MHR-01

    [Fact]
    public void HierarchyEntry_ExcludesItsViolation_AndAStaleOneFails()
    {
        using var native = new SyntheticNativeFile();
        var metric = native.Property("qAAA");
        var table = native.TableVersion("T_01.00");
        native.Header(table, "X", isKey: false, "0010", metric, native.SubCategory("qCO", "new_CO6"));
        using var layouts = new SyntheticLayoutRepository();
        layouts.MetricOrdinate(layouts.Sheet("4.4", "T_01.00"), "X", "0010", "qAAA", "qCO", "new_CO5");
        const string Row = "T_01.00\thierarchy\t4.4|X|0010|qAAA\tarbitrated: layout cites the wrong hierarchy";

        // Without the entry the unit fails.
        Assert.Equal(CheckStatus.Fail, PlaneCTestHelpers.Check(PlaneCTestHelpers.Run(native, layouts), "C-MHR-01").Status);

        // With it, it passes, and the entry is reported as applied (NDD-27).
        var applied = PlaneCTestHelpers.Run(native, layouts, PlaneCTestHelpers.Census("4.4", Row));
        Assert.Equal(CheckStatus.Pass, PlaneCTestHelpers.Check(applied, "C-MHR-01").Status);
        var divergence = Assert.Single(applied.Divergences);
        Assert.Equal("NDD-27", divergence.Id);
        Assert.Equal("C-MHR-01", divergence.Scope);
        Assert.Equal("T_01.00\t4.4|X|0010|qAAA", divergence.BusinessKey);
        Assert.Equal(1, divergence.Matched);
        Assert.False(divergence.Stale);

        // An entry for another ordinate exempts nothing, and it is stale.
        var other = PlaneCTestHelpers.Run(
            native, layouts, PlaneCTestHelpers.Census("4.4", "T_01.00\thierarchy\t4.4|X|0020|qAAA\tother ordinate"));
        var check = PlaneCTestHelpers.Check(other, "C-MHR-01");
        Assert.Equal(CheckStatus.Fail, check.Status);
        Assert.Equal(2, check.Failed);
        Assert.True(Assert.Single(other.Divergences).Stale);
    }

    [Fact]
    public void HierarchyStaleEntry_WhenTheNativeHierarchyIsRight_Fails()
    {
        using var native = new SyntheticNativeFile();
        var metric = native.Property("qAAA");
        var table = native.TableVersion("T_01.00");
        native.Header(table, "X", isKey: false, "0010", metric, native.SubCategory("qCO", "new_CO5"));
        using var layouts = new SyntheticLayoutRepository();
        layouts.MetricOrdinate(layouts.Sheet("4.4", "T_01.00"), "X", "0010", "qAAA", "qCO", "new_CO5");

        var result = PlaneCTestHelpers.Run(
            native, layouts, PlaneCTestHelpers.Census("4.4", "T_01.00\thierarchy\t4.4|X|0010|qAAA\tused to differ"));

        var check = PlaneCTestHelpers.Check(result, "C-MHR-01");
        Assert.Equal(CheckStatus.Fail, check.Status);
        Assert.Equal("(absent, NDD-27 no longer violates) T_01.00\t4.4|X|0010|qAAA", Assert.Single(check.Samples).BusinessKey);
        Assert.True(Assert.Single(result.Divergences).Stale);
    }
}
