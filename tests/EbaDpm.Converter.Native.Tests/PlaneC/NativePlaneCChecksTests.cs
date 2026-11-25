using EbaDpm.Converter.Core.Validation;
using EbaDpm.Converter.Native.PlaneC;

namespace EbaDpm.Converter.Native.Tests.PlaneC;

/// <summary>Shared by the plane C test classes.</summary>
internal static class PlaneCTestHelpers
{
    public static readonly IReadOnlyList<NativePlaneCCensus> NoCensus = [];

    public static NativePlaneCResult Run(
        SyntheticNativeFile native, SyntheticLayoutRepository layouts, params NativePlaneCCensus[] censuses) =>
        NativePlaneC.Run(native.Connection, layouts.Connection, censuses);

    public static CheckResult Check(NativePlaneCResult result, string id) => result.Checks.Single(c => c.Id == id);

    public static NativePlaneCCensus Census(string release, params string[] rows) =>
        NativePlaneCCensuses.Parse($"# CorpusRelease: {release}\nTableCode\tType\tLayoutSignature\n" + string.Join("\n", rows));

    /// <summary>A table T_01.00 valid from 4.2 with one cell: the metric qAAA with the context pair
    /// qDIM(<paramref name="member"/>), released at 4.4 (id 3).</summary>
    public static long BasicTable(SyntheticNativeFile native, string member = "qM1", string tableCode = "T_01.00")
    {
        var table = native.TableVersion(tableCode);
        AddCell(native, table, member);
        return table;
    }

    public static long AddCell(SyntheticNativeFile native, long table, string member, string metricCode = "qAAA", string dimensionCode = "qDIM")
    {
        var metric = Existing(native, "_PR", metricCode);
        var dimension = Existing(native, "_PR", dimensionCode);
        var item = Existing(native, "MEM", member);
        return native.Cell(table, native.Variable("fact", metric, (dimension, item)));
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SyntheticNativeFile, Dictionary<(string, string), long>> Items = new();

    /// <summary>The item of that code in that category, created on first use.</summary>
    public static long Existing(SyntheticNativeFile native, string category, string code)
    {
        var items = Items.GetOrCreateValue(native);
        if (!items.TryGetValue((category, code), out var id))
        {
            id = native.Item(category, code);
            items[(category, code)] = id;
        }

        return id;
    }
}

/// <summary>
/// C-DPS-01 / C-DPS-02 / C-KEY-01 / C-COB-01 of the native plane C over a small native file and a
/// small layout repository (docs/validation.md, "Native output", "Plane C"). Every verdict has the
/// opposite case next to it.
/// </summary>
public sealed class NativePlaneCChecksTests
{
    private const string Release44 = "4.4";

    // ------------------------------------------------------------------ C-DPS-01

    [Fact]
    public void Dps01_LayoutSignatureContainedInANativeSignature_Passes_AndASupersetIsNotRequiredToBeEqual()
    {
        using var native = new SyntheticNativeFile();
        var metric = PlaneCTestHelpers.Existing(native, "_PR", "qAAA");
        var dimension = PlaneCTestHelpers.Existing(native, "_PR", "qDIM");
        var extra = PlaneCTestHelpers.Existing(native, "_PR", "qEXT");
        var m1 = PlaneCTestHelpers.Existing(native, "MEM", "qM1");
        var table = native.TableVersion("T_01.00");
        native.Cell(table, native.Variable("fact", metric, (dimension, m1), (extra, m1)));
        using var layouts = new SyntheticLayoutRepository();
        var sheet = layouts.Sheet(Release44, "T_01.00");
        layouts.SignatureCell(sheet, "MET(qAAA)", "qDIM(qM1)"); // a subset of the native signature.

        var check = PlaneCTestHelpers.Check(PlaneCTestHelpers.Run(native, layouts), "C-DPS-01");

        Assert.Equal(CheckStatus.Pass, check.Status);
        Assert.Equal(1, check.Examined);
        Assert.Equal(0, check.Failed);
    }

    [Fact]
    public void Dps01_MemberContradiction_Fails_AsContradicts_WithTheBusinessKeyOfTheLayoutSignature()
    {
        using var native = new SyntheticNativeFile();
        PlaneCTestHelpers.BasicTable(native); // qDIM(qM1)
        PlaneCTestHelpers.Existing(native, "MEM", "qM2");
        using var layouts = new SyntheticLayoutRepository();
        var sheet = layouts.Sheet(Release44, "T_01.00");
        layouts.SignatureCell(sheet, "MET(qAAA)", "qDIM(qM2)"); // the layout says qM2, the native cell has qM1.

        var check = PlaneCTestHelpers.Check(PlaneCTestHelpers.Run(native, layouts), "C-DPS-01");

        Assert.Equal(CheckStatus.Fail, check.Status);
        Assert.Equal(ValidationLayer.Critical, check.Layer);
        Assert.Equal(1, check.Failed);
        var sample = Assert.Single(check.Samples);
        Assert.Equal("4.4|T_01.00\tMET(qAAA)|qDIM(qM2)", sample.BusinessKey);
        Assert.Equal("contradicts", sample.Reference);
    }

    [Fact]
    public void Dps01_NoSignatureWithTheDimensionsOfTheLayout_Fails_AsNoMatch()
    {
        using var native = new SyntheticNativeFile();
        PlaneCTestHelpers.BasicTable(native); // MET(qAAA)|qDIM(qM1): no qOTHER anywhere.
        PlaneCTestHelpers.Existing(native, "_PR", "qOTHER");
        using var layouts = new SyntheticLayoutRepository();
        var sheet = layouts.Sheet(Release44, "T_01.00");
        layouts.SignatureCell(sheet, "MET(qAAA)", "qOTHER(qM1)");

        var check = PlaneCTestHelpers.Check(PlaneCTestHelpers.Run(native, layouts), "C-DPS-01");

        Assert.Equal(CheckStatus.Fail, check.Status);
        var sample = Assert.Single(check.Samples);
        Assert.Equal("4.4|T_01.00\tMET(qAAA)|qOTHER(qM1)", sample.BusinessKey);
        Assert.Equal("no-match", sample.Reference);
    }

    [Fact]
    public void Dps01_OnlyTheViolatingSignatureFails_WhenTheTableHasAContainedOneToo()
    {
        using var native = new SyntheticNativeFile();
        var table = PlaneCTestHelpers.BasicTable(native);
        PlaneCTestHelpers.Existing(native, "MEM", "qM2");
        PlaneCTestHelpers.AddCell(native, table, "qM2");
        using var layouts = new SyntheticLayoutRepository();
        var sheet = layouts.Sheet(Release44, "T_01.00");
        layouts.SignatureCell(sheet, "MET(qAAA)", "qDIM(qM1)");
        layouts.SignatureCell(sheet, "MET(qAAA)", "qDIM(qM2)");
        layouts.SignatureCell(sheet, "MET(qAAA)", "qDIM(qM3)");

        var check = PlaneCTestHelpers.Check(PlaneCTestHelpers.Run(native, layouts), "C-DPS-01");

        Assert.Equal(CheckStatus.Fail, check.Status);
        Assert.Equal(3, check.Examined);
        Assert.Equal(1, check.Failed);
        Assert.Equal("4.4|T_01.00\tMET(qAAA)|qDIM(qM3)", Assert.Single(check.Samples).BusinessKey);
    }

    [Fact]
    public void Dps01_ExcludedNativeCell_HasNoSignature_SoTheLayoutSignatureOfItIsNotContained()
    {
        using var native = new SyntheticNativeFile();
        var metric = PlaneCTestHelpers.Existing(native, "_PR", "qAAA");
        var dimension = PlaneCTestHelpers.Existing(native, "_PR", "qDIM");
        var m1 = PlaneCTestHelpers.Existing(native, "MEM", "qM1");
        var table = native.TableVersion("T_01.00");
        var variable = native.Variable("fact", metric, (dimension, m1));
        native.Cell(table, variable, excluded: true);
        using var layouts = new SyntheticLayoutRepository();
        var sheet = layouts.Sheet(Release44, "T_01.00");
        layouts.SignatureCell(sheet, "MET(qAAA)", "qDIM(qM1)");

        var result = PlaneCTestHelpers.Run(native, layouts);

        // The only native cell is excluded: the table has no native signature, nothing is compared.
        Assert.Equal(CheckStatus.Skipped, PlaneCTestHelpers.Check(result, "C-DPS-01").Status);

        // The same fixture with the cell not excluded passes: the exclusion is what changed the verdict.
        using var native2 = new SyntheticNativeFile();
        PlaneCTestHelpers.BasicTable(native2);
        Assert.Equal(CheckStatus.Pass, PlaneCTestHelpers.Check(PlaneCTestHelpers.Run(native2, layouts), "C-DPS-01").Status);
    }

    [Fact]
    public void Dps01_ShadedLayoutCell_IsNotCompared()
    {
        using var native = new SyntheticNativeFile();
        PlaneCTestHelpers.BasicTable(native);
        using var layouts = new SyntheticLayoutRepository();
        var sheet = layouts.Sheet(Release44, "T_01.00");
        layouts.SignatureCell(sheet, "MET(qAAA)", "qDIM(qM1)");
        layouts.SignatureCell(sheet, shaded: true, "MET(qAAA)", "qDIM(qZZZ)"); // would be a violation if compared.

        var check = PlaneCTestHelpers.Check(PlaneCTestHelpers.Run(native, layouts), "C-DPS-01");

        Assert.Equal(CheckStatus.Pass, check.Status);
        Assert.Equal(1, check.Examined);
    }

    [Fact]
    public void Dps01_ADuplicateSignatureInTheLayoutIsComparedOnce()
    {
        using var native = new SyntheticNativeFile();
        PlaneCTestHelpers.BasicTable(native);
        PlaneCTestHelpers.Existing(native, "MEM", "qM2");
        using var layouts = new SyntheticLayoutRepository();
        var sheet = layouts.Sheet(Release44, "T_01.00");
        layouts.SignatureCell(sheet, "MET(qAAA)", "qDIM(qM2)");
        layouts.SignatureCell(sheet, "MET(qAAA)", "qDIM(qM2)");

        var check = PlaneCTestHelpers.Check(PlaneCTestHelpers.Run(native, layouts), "C-DPS-01");

        Assert.Equal(1, check.Examined);
        Assert.Equal(1, check.Failed);
    }

    // ------------------------------------------------------------------ C-DPS-02

    [Fact]
    public void Dps02_NativeTermsTheLayoutDoesNotMention_AreInformative_AndNamed()
    {
        using var native = new SyntheticNativeFile();
        var metric = PlaneCTestHelpers.Existing(native, "_PR", "qAAA");
        var dimension = PlaneCTestHelpers.Existing(native, "_PR", "qDIM");
        var extra = PlaneCTestHelpers.Existing(native, "_PR", "qEXT");
        var m1 = PlaneCTestHelpers.Existing(native, "MEM", "qM1");
        var table = native.TableVersion("T_01.00");
        native.Cell(table, native.Variable("fact", metric, (dimension, m1), (extra, m1)));
        using var layouts = new SyntheticLayoutRepository();
        var sheet = layouts.Sheet(Release44, "T_01.00");
        layouts.SignatureCell(sheet, "MET(qAAA)", "qDIM(qM1)");

        var result = PlaneCTestHelpers.Run(native, layouts);
        var check = PlaneCTestHelpers.Check(result, "C-DPS-02");

        Assert.Equal(ValidationLayer.Informative, check.Layer);
        Assert.Equal(CheckStatus.Info, check.Status);
        Assert.Equal(1, check.Failed);
        Assert.Equal("4.4|T_01.00|qEXT(qM1)", Assert.Single(check.Samples).BusinessKey);
        // Informative: it never fails the critical check.
        Assert.Equal(CheckStatus.Pass, PlaneCTestHelpers.Check(result, "C-DPS-01").Status);
    }

    [Fact]
    public void Dps02_WhenTheLayoutMentionsEveryNativeTerm_ThereIsNothingToReport()
    {
        using var native = new SyntheticNativeFile();
        PlaneCTestHelpers.BasicTable(native);
        using var layouts = new SyntheticLayoutRepository();
        var sheet = layouts.Sheet(Release44, "T_01.00");
        layouts.SignatureCell(sheet, "MET(qAAA)", "qDIM(qM1)");

        var check = PlaneCTestHelpers.Check(PlaneCTestHelpers.Run(native, layouts), "C-DPS-02");

        Assert.Equal(CheckStatus.Pass, check.Status);
        Assert.Equal(0, check.Failed);
        Assert.Empty(check.Samples);
    }

    // ------------------------------------------------------------------ C-KEY-01

    [Fact]
    public void Key01_KeyColumnOfAnOpenTable_IsExcludedFromDps01_CountedAndNamed()
    {
        using var native = new SyntheticNativeFile();
        PlaneCTestHelpers.BasicTable(native);
        using var layouts = new SyntheticLayoutRepository();
        var sheet = layouts.Sheet(Release44, "T_01.00");
        layouts.KeyDeclaration(sheet, "qKEY");
        layouts.SignatureCell(sheet, "MET(qKEY)"); // the key column drawn as a grid column: MET(qKEY) + qKEY(*)
        layouts.SignatureCell(sheet, "MET(qAAA)", "qDIM(qM1)"); // plus the key term qKEY(*) of the sheet.

        var result = PlaneCTestHelpers.Run(native, layouts);
        var dps = PlaneCTestHelpers.Check(result, "C-DPS-01");
        var key = PlaneCTestHelpers.Check(result, "C-KEY-01");

        // The ordinary signature has qKEY(*) too, which the native cell lacks (no key header was
        // declared in the native table): it is not contained. The key-column signature is excluded.
        Assert.Equal(1, dps.Examined);
        Assert.Equal(1, dps.Failed);
        Assert.Equal(CheckStatus.Info, key.Status);
        Assert.Equal(1, key.Examined);
        Assert.Equal("4.4|T_01.00 (1)", Assert.Single(key.Samples).BusinessKey);
    }

    [Fact]
    public void Key01_NativeSideKeyHeader_MakesTheOrdinarySignatureOfTheLayoutContained()
    {
        // Control of the previous test: when the native table declares the key header, the ordinary
        // signature (with qKEY(*)) is contained and C-DPS-01 passes, with the key column still excluded.
        using var native = new SyntheticNativeFile();
        var table = PlaneCTestHelpers.BasicTable(native);
        native.Header(table, "Y", isKey: true, "0010", PlaneCTestHelpers.Existing(native, "_PR", "qKEY"));
        using var layouts = new SyntheticLayoutRepository();
        var sheet = layouts.Sheet(Release44, "T_01.00");
        layouts.KeyDeclaration(sheet, "qKEY");
        layouts.SignatureCell(sheet, "MET(qKEY)");
        layouts.SignatureCell(sheet, "MET(qAAA)", "qDIM(qM1)");

        var result = PlaneCTestHelpers.Run(native, layouts);

        var dps = PlaneCTestHelpers.Check(result, "C-DPS-01");
        Assert.Equal(CheckStatus.Pass, dps.Status);
        Assert.Equal(1, dps.Examined);
        Assert.Equal(1, PlaneCTestHelpers.Check(result, "C-KEY-01").Examined);
    }

    [Fact]
    public void Key01_MetricWithoutItsOwnOpenAxisTerm_IsNotAKeyColumn_AndIsCompared()
    {
        using var native = new SyntheticNativeFile();
        PlaneCTestHelpers.BasicTable(native);
        using var layouts = new SyntheticLayoutRepository();
        var sheet = layouts.Sheet(Release44, "T_01.00");
        layouts.SignatureCell(sheet, "MET(qKEY)"); // no qKEY(*) in the signature: not a key column.

        var result = PlaneCTestHelpers.Run(native, layouts);

        Assert.Equal(0, PlaneCTestHelpers.Check(result, "C-KEY-01").Examined);
        var dps = PlaneCTestHelpers.Check(result, "C-DPS-01");
        Assert.Equal(CheckStatus.Fail, dps.Status);
        Assert.Equal(1, dps.Examined);
    }

    // ------------------------------------------------------------------ release pairing and C-COB-01

    private static string Cob(NativePlaneCResult result, string release) =>
        PlaneCTestHelpers.Check(result, "C-COB-01").Samples.Single(s => s.BusinessKey == $"release {release}" && s.Generated!.Contains(" sheet(s): ")).Generated!;

    [Fact]
    public void Pairing_SheetReleaseNotInTheFile_IsNotCompared_AndCounted()
    {
        using var native = new SyntheticNativeFile();
        PlaneCTestHelpers.BasicTable(native);
        using var layouts = new SyntheticLayoutRepository();
        var sheet = layouts.Sheet("9.9", "T_01.00");
        layouts.SignatureCell(sheet, "MET(qAAA)", "qDIM(qZZZ)"); // would fail if compared.

        var result = PlaneCTestHelpers.Run(native, layouts);

        Assert.StartsWith(
            "1 sheet(s): 0 with a unique table version at the release, 0 without a table version valid at the release, " +
            "0 with several table versions of the same code (not compared), 1 of a release not in the file (not compared); " +
            "0 table(s) compared",
            Cob(result, "9.9"));
        Assert.Equal(CheckStatus.Skipped, PlaneCTestHelpers.Check(result, "C-DPS-01").Status);
    }

    [Fact]
    public void Pairing_ReleaseMatchIsExact_APointReleaseDoesNotMatchItsBaseRelease()
    {
        using var native = new SyntheticNativeFile(); // releases "4.2", "4.3", "4.4".
        PlaneCTestHelpers.BasicTable(native);
        using var layouts = new SyntheticLayoutRepository();
        var sheet = layouts.Sheet("4.4.1", "T_01.00");
        layouts.SignatureCell(sheet, "MET(qAAA)", "qDIM(qM1)");

        var result = PlaneCTestHelpers.Run(native, layouts);

        Assert.Contains("1 of a release not in the file (not compared)", Cob(result, "4.4.1"));
        Assert.Equal(CheckStatus.Skipped, PlaneCTestHelpers.Check(result, "C-DPS-01").Status);
    }

    [Fact]
    public void Pairing_NoTableVersionValidAtTheRelease_IsNotCompared_AndCounted()
    {
        using var native = new SyntheticNativeFile();
        var table = native.TableVersion("T_01.00", start: 1, end: 3); // ends at 4.4: not valid at it.
        PlaneCTestHelpers.AddCell(native, table, "qM1");
        using var layouts = new SyntheticLayoutRepository();
        var sheet = layouts.Sheet(Release44, "T_01.00");
        layouts.SignatureCell(sheet, "MET(qAAA)", "qDIM(qZZZ)");

        var result = PlaneCTestHelpers.Run(native, layouts);

        Assert.StartsWith(
            "1 sheet(s): 0 with a unique table version at the release, 1 without a table version valid at the release, " +
            "0 with several table versions of the same code (not compared), 0 of a release not in the file (not compared); " +
            "0 table(s) compared",
            Cob(result, "4.4"));
        Assert.Equal(CheckStatus.Skipped, PlaneCTestHelpers.Check(result, "C-DPS-01").Status);
    }

    [Fact]
    public void Pairing_TwoTableVersionsOfOneCodeAtTheRelease_AreNotCompared_AndCounted()
    {
        using var native = new SyntheticNativeFile();
        PlaneCTestHelpers.BasicTable(native);
        PlaneCTestHelpers.BasicTable(native); // a second table version of the same code, also valid at 4.4.
        using var layouts = new SyntheticLayoutRepository();
        var sheet = layouts.Sheet(Release44, "T_01.00");
        layouts.SignatureCell(sheet, "MET(qAAA)", "qDIM(qZZZ)");

        var result = PlaneCTestHelpers.Run(native, layouts);

        Assert.StartsWith(
            "1 sheet(s): 0 with a unique table version at the release, 0 without a table version valid at the release, " +
            "1 with several table versions of the same code (not compared), 0 of a release not in the file (not compared); " +
            "0 table(s) compared",
            Cob(result, "4.4"));
        Assert.Equal(CheckStatus.Skipped, PlaneCTestHelpers.Check(result, "C-DPS-01").Status);
    }

    [Theory]
    [InlineData(3L, null, true)]   // starts at R: valid.
    [InlineData(4L, null, false)]  // starts after R.
    [InlineData(1L, 3L, false)]    // ends at R (EndReleaseID is exclusive).
    [InlineData(1L, 4L, true)]     // ends after R.
    public void Pairing_ValidityPredicate_IsStartLessOrEqualAndEndGreater(long start, long? end, bool paired)
    {
        using var native = new SyntheticNativeFile();
        var table = native.TableVersion("T_01.00", start, end);
        PlaneCTestHelpers.AddCell(native, table, "qM1");
        using var layouts = new SyntheticLayoutRepository();
        var sheet = layouts.Sheet(Release44, "T_01.00");
        layouts.SignatureCell(sheet, "MET(qAAA)", "qDIM(qM1)");

        var result = PlaneCTestHelpers.Run(native, layouts);

        Assert.Equal(paired ? CheckStatus.Pass : CheckStatus.Skipped, PlaneCTestHelpers.Check(result, "C-DPS-01").Status);
        Assert.Contains(paired ? "1 with a unique table version at the release" : "1 without a table version valid at the release", Cob(result, "4.4"));
    }

    [Fact]
    public void NothingExamined_IsSkipped_NeverPass_ForEveryCriticalCheck()
    {
        using var native = new SyntheticNativeFile();
        using var layouts = new SyntheticLayoutRepository(); // no sheet at all.

        var result = PlaneCTestHelpers.Run(native, layouts);

        foreach (var id in new[] { "C-DPS-01", "C-EQU-01", "C-MHR-01" })
        {
            var check = PlaneCTestHelpers.Check(result, id);
            Assert.Equal(CheckStatus.Skipped, check.Status);
            Assert.NotNull(check.SkipReason);
            Assert.Equal(ValidationLayer.Critical, check.Layer);
        }

        Assert.Equal(
            ["C-COB-01", "C-DPS-01", "C-DPS-02", "C-EQU-01", "C-EQU-02", "C-KEY-01", "C-MHR-01", "C-MHR-02"],
            result.Checks.Select(c => c.Id).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void EverySheet_IsComparedAtItsOwnRelease_AndAnotherReleaseGivesAnotherVerdict()
    {
        // T_01.00 has two table versions: v1 valid at 4.2 only (qDIM(qM1)), v2 from 4.3 on (qDIM(qM2)).
        using var native = new SyntheticNativeFile();
        var v1 = native.TableVersion("T_01.00", start: 1, end: 2);
        PlaneCTestHelpers.AddCell(native, v1, "qM1");
        var v2 = native.TableVersion("T_01.00", start: 2);
        PlaneCTestHelpers.AddCell(native, v2, "qM2");

        using var right = new SyntheticLayoutRepository();
        right.SignatureCell(right.Sheet("4.2", "T_01.00"), "MET(qAAA)", "qDIM(qM1)");
        right.SignatureCell(right.Sheet("4.3", "T_01.00"), "MET(qAAA)", "qDIM(qM2)");
        var ok = PlaneCTestHelpers.Check(PlaneCTestHelpers.Run(native, right), "C-DPS-01");
        Assert.Equal(CheckStatus.Pass, ok.Status);
        Assert.Equal(2, ok.Examined);

        // Control: the same layouts with the releases exchanged: each is compared against the version
        // of the other release and contradicts it.
        using var swapped = new SyntheticLayoutRepository();
        swapped.SignatureCell(swapped.Sheet("4.3", "T_01.00"), "MET(qAAA)", "qDIM(qM1)");
        swapped.SignatureCell(swapped.Sheet("4.2", "T_01.00"), "MET(qAAA)", "qDIM(qM2)");
        var bad = PlaneCTestHelpers.Check(PlaneCTestHelpers.Run(native, swapped), "C-DPS-01");
        Assert.Equal(CheckStatus.Fail, bad.Status);
        Assert.Equal(2, bad.Failed);
        Assert.Equal(
            ["4.2|T_01.00\tMET(qAAA)|qDIM(qM2)", "4.3|T_01.00\tMET(qAAA)|qDIM(qM1)"],
            bad.Samples.Select(s => s.BusinessKey).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void ACodeThatChangedBetweenReleases_IsComparedWithTheCodeOfTheSheetRelease()
    {
        // One item, one table version valid at 4.2 and 4.3; the member is "qOLD" until 4.3 and "qNEW" from it.
        using var native = new SyntheticNativeFile();
        var metric = native.Property("qAAA");
        var dimension = native.Property("qDIM");
        var member = native.Member("MEM", "qOLD", start: 1, end: 2);
        native.Item("MEM", "qNEW", start: 2, itemId: member);
        var table = native.TableVersion("T_01.00", start: 1);
        native.Cell(table, native.Variable("fact", metric, (dimension, member)));

        using var right = new SyntheticLayoutRepository();
        right.SignatureCell(right.Sheet("4.2", "T_01.00"), "MET(qAAA)", "qDIM(qOLD)");
        right.SignatureCell(right.Sheet("4.3", "T_01.00"), "MET(qAAA)", "qDIM(qNEW)");
        var ok = PlaneCTestHelpers.Check(PlaneCTestHelpers.Run(native, right), "C-DPS-01");
        Assert.Equal(CheckStatus.Pass, ok.Status);
        Assert.Equal(2, ok.Examined);

        // Control: the code of 4.3 in the sheet of 4.2 is not the code of the file at 4.2.
        using var wrong = new SyntheticLayoutRepository();
        wrong.SignatureCell(wrong.Sheet("4.2", "T_01.00"), "MET(qAAA)", "qDIM(qNEW)");
        var bad = PlaneCTestHelpers.Check(PlaneCTestHelpers.Run(native, wrong), "C-DPS-01");
        Assert.Equal(CheckStatus.Fail, bad.Status);
        Assert.Equal("contradicts", Assert.Single(bad.Samples).Reference);
    }

    [Fact]
    public void CodeMissingAtTheRelease_IsReportedInCob01_AndTheTableHasNoNativeSignature()
    {
        using var native = new SyntheticNativeFile();
        var metric = native.Property("qAAA");
        var dimension = native.Property("qDIM");
        var member = native.Member("MEM", "qM1", start: 3); // exists from 4.4 only.
        var table = native.TableVersion("T_01.00");
        native.Cell(table, native.Variable("fact", metric, (dimension, member)));
        using var layouts = new SyntheticLayoutRepository();
        layouts.SignatureCell(layouts.Sheet("4.3", "T_01.00"), "MET(qAAA)", "qDIM(qM1)");

        var result = PlaneCTestHelpers.Run(native, layouts);

        var cob = PlaneCTestHelpers.Check(result, "C-COB-01");
        Assert.Contains("1 table(s) paired without any native signature", Cob(result, "4.3"));
        Assert.Contains("1 native cell(s) without signature for a code missing at the release", Cob(result, "4.3"));
        Assert.Contains(cob.Samples, s => s.Generated == "1 code(s) missing at the release: context item");
        Assert.Equal(CheckStatus.Skipped, PlaneCTestHelpers.Check(result, "C-DPS-01").Status);
    }

    // ------------------------------------------------------------------ paired table, 0 signatures compared

    private static (SyntheticNativeFile Native, SyntheticLayoutRepository Layouts) OnlyKeyColumnSignatures()
    {
        var native = new SyntheticNativeFile();
        PlaneCTestHelpers.BasicTable(native);
        var layouts = new SyntheticLayoutRepository();
        var sheet = layouts.Sheet(Release44, "T_01.00");
        layouts.KeyDeclaration(sheet, "qKEY");
        layouts.SignatureCell(sheet, "MET(qKEY)"); // MET(qKEY) + qKEY(*): a key column, the only layout signature.
        return (native, layouts);
    }

    [Fact]
    public void Dps01_PairedTableWhoseOnlySignaturesAreKeyColumns_IsSkipped_NeverPass_AndSaysWhy()
    {
        var (native, layouts) = OnlyKeyColumnSignatures();
        using (native)
        using (layouts)
        {
            var result = PlaneCTestHelpers.Run(native, layouts);

            var check = PlaneCTestHelpers.Check(result, "C-DPS-01");
            Assert.Equal(CheckStatus.Skipped, check.Status);
            Assert.NotEqual(CheckStatus.Pass, check.Status);
            Assert.Equal(0, check.Examined);
            Assert.Contains("C-KEY-01", check.SkipReason);
            Assert.Equal(1, PlaneCTestHelpers.Check(result, "C-KEY-01").Examined); // the exclusion is still reported.
        }
    }

    [Fact]
    public void Dps01_PairedTableWithOnlyKeyColumns_AndAStaleCensusEntry_Fails()
    {
        var (native, layouts) = OnlyKeyColumnSignatures();
        using (native)
        using (layouts)
        {
            var census = PlaneCTestHelpers.Census("4.4", "T_01.00	no-match	MET(qAAA)|qDIM(qM2)	used to violate");

            var result = PlaneCTestHelpers.Run(native, layouts, census);

            var check = PlaneCTestHelpers.Check(result, "C-DPS-01");
            Assert.Equal(CheckStatus.Fail, check.Status);
            Assert.Equal(1, check.Failed);
            Assert.StartsWith("4.4|(absent, NDD-23 no longer violates)", Assert.Single(check.Samples).BusinessKey, StringComparison.Ordinal);
            Assert.True(Assert.Single(result.Divergences).Stale);
        }
    }

    [Fact]
    public void Dps01_PairedTableWithNoLayoutSignatureAtAll_IsSkipped()
    {
        using var native = new SyntheticNativeFile();
        PlaneCTestHelpers.BasicTable(native);
        using var layouts = new SyntheticLayoutRepository();
        layouts.Sheet(Release44, "T_01.00"); // paired with a unique table version, but the sheet has no cell.

        var result = PlaneCTestHelpers.Run(native, layouts);

        var check = PlaneCTestHelpers.Check(result, "C-DPS-01");
        Assert.Equal(CheckStatus.Skipped, check.Status);
        Assert.Equal(0, check.Examined);
        Assert.Contains("1 with a unique table version at the release", Cob(result, "4.4"));
    }
}
