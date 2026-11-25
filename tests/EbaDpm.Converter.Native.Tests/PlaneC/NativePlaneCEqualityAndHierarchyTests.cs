using EbaDpm.Converter.Core.Validation;
using EbaDpm.Converter.Native.PlaneC;

namespace EbaDpm.Converter.Native.Tests.PlaneC;

/// <summary>
/// C-EQU-01 / C-EQU-02 (the cells that an <c>==</c> rule names carry the same native signature) and
/// C-MHR-01 / C-MHR-02 (the hierarchy of a metric on its header) of the native plane C over small
/// synthetic files (docs/validation.md, "Native output", "Plane C").
/// </summary>
public sealed class NativePlaneCEqualityAndHierarchyTests
{
    private static SyntheticLayoutRepository.RuleTerm Term(string row, string column, string? z = null, string table = "T_01.00") =>
        new(table, row, column, z);

    /// <summary>T_01.00 with the row header 0010 and the column headers 0020 and 0030, and the two
    /// cells (0010,0020) and (0010,0030); returns the table and the metric/dimension/member ids.</summary>
    private sealed record Grid(long Table, long Metric, long Dimension, long M1, long M2, long Row, long ColumnA, long ColumnB);

    private static Grid NewGrid(SyntheticNativeFile native, string tableCode = "T_01.00")
    {
        var metric = PlaneCTestHelpers.Existing(native, "_PR", "qAAA");
        var dimension = PlaneCTestHelpers.Existing(native, "_PR", "qDIM");
        var m1 = PlaneCTestHelpers.Existing(native, "MEM", "qM1");
        var m2 = PlaneCTestHelpers.Existing(native, "MEM", "qM2");
        var table = native.TableVersion(tableCode);
        var row = native.Header(table, "Y", isKey: false, "0010");
        var columnA = native.Header(table, "X", isKey: false, "0020");
        var columnB = native.Header(table, "X", isKey: false, "0030");
        return new Grid(table, metric, dimension, m1, m2, row, columnA, columnB);
    }

    private static void TwoCells(SyntheticNativeFile native, Grid g, long memberA, long memberB)
    {
        native.Cell(g.Table, native.Variable("fact", g.Metric, (g.Dimension, memberA)), rowHeader: g.Row, columnHeader: g.ColumnA);
        native.Cell(g.Table, native.Variable("fact", g.Metric, (g.Dimension, memberB)), rowHeader: g.Row, columnHeader: g.ColumnB);
    }

    private static long RuleSheet(SyntheticLayoutRepository layouts, SyntheticNativeFile native, string release = "4.4") =>
        layouts.Sheet(release, "T_01.00");

    // ------------------------------------------------------------------ C-EQU-01

    [Fact]
    public void Equ01_CellsOfARuleWithTheSameSignature_Pass_AndTheCoverageCountsTheRule()
    {
        using var native = new SyntheticNativeFile();
        var g = NewGrid(native);
        TwoCells(native, g, g.M1, g.M1); // two variables, the same signature.
        using var layouts = new SyntheticLayoutRepository();
        layouts.EqualityRule(1, RuleSheet(layouts, native), Term("0010", "0020"), Term("0010", "0030"));

        var result = PlaneCTestHelpers.Run(native, layouts);

        var check = PlaneCTestHelpers.Check(result, "C-EQU-01");
        Assert.Equal(CheckStatus.Pass, check.Status);
        Assert.Equal(ValidationLayer.Critical, check.Layer);
        Assert.Equal(1, check.Examined);
        Assert.Contains(
            "1 total rules, 1 checkable (1 with identical signature, 0 with different signature), 0 not checkable",
            PlaneCTestHelpers.Check(result, "C-EQU-02").Statement);
    }

    [Fact]
    public void Equ01_CellsOfARuleWithDifferentSignatures_Fail_WithTheTermsOfTheRuleAsKey()
    {
        using var native = new SyntheticNativeFile();
        var g = NewGrid(native);
        TwoCells(native, g, g.M1, g.M2);
        using var layouts = new SyntheticLayoutRepository();
        var a = Term("0010", "0020");
        var b = Term("0010", "0030");
        layouts.EqualityRule(1, RuleSheet(layouts, native), a, b);

        var result = PlaneCTestHelpers.Run(native, layouts);

        var check = PlaneCTestHelpers.Check(result, "C-EQU-01");
        Assert.Equal(CheckStatus.Fail, check.Status);
        Assert.Equal(1, check.Failed);
        var sample = Assert.Single(check.Samples);
        Assert.Equal($"{{{a.Text}}}=={{{b.Text}}}", sample.BusinessKey);
        Assert.Equal("MET(qAAA)|qDIM(qM1) | MET(qAAA)|qDIM(qM2)", sample.Generated);
        Assert.Contains(
            "1 checkable (0 with identical signature, 1 with different signature)",
            PlaneCTestHelpers.Check(result, "C-EQU-02").Statement);
    }

    [Fact]
    public void Equ01_ARuleOfThreeCells_FailsWhenAnyOneDiffers()
    {
        using var native = new SyntheticNativeFile();
        var g = NewGrid(native);
        var columnC = native.Header(g.Table, "X", isKey: false, "0040");
        TwoCells(native, g, g.M1, g.M1);
        native.Cell(g.Table, native.Variable("fact", g.Metric, (g.Dimension, g.M2)), rowHeader: g.Row, columnHeader: columnC);
        using var layouts = new SyntheticLayoutRepository();
        layouts.EqualityRule(1, RuleSheet(layouts, native), Term("0010", "0020"), Term("0010", "0030"), Term("0010", "0040"));

        var check = PlaneCTestHelpers.Check(PlaneCTestHelpers.Run(native, layouts), "C-EQU-01");

        Assert.Equal(CheckStatus.Fail, check.Status);
        Assert.Equal(3, Assert.Single(check.Samples).Generated!.Split(" | ").Length);
    }

    private static (SyntheticNativeFile Native, SyntheticLayoutRepository Layouts) Unresolvable(string reason)
    {
        var native = new SyntheticNativeFile();
        var layouts = new SyntheticLayoutRepository();
        switch (reason)
        {
            case "cell unresolved":
            {
                var g = NewGrid(native);
                TwoCells(native, g, g.M1, g.M1);
                layouts.EqualityRule(1, RuleSheet(layouts, native), Term("0010", "0020"), Term("0010", "9990")); // no such column.
                break;
            }

            case "release not in the file":
            {
                var g = NewGrid(native);
                TwoCells(native, g, g.M1, g.M1);
                layouts.EqualityRule(1, RuleSheet(layouts, native, "9.9"), Term("0010", "0020"), Term("0010", "0030"));
                break;
            }

            case "no table version at the release":
            {
                var table = native.TableVersion("T_01.00", start: 1, end: 3); // not valid at 4.4.
                native.Header(table, "Y", isKey: false, "0010");
                layouts.EqualityRule(1, RuleSheet(layouts, native), Term("0010", "0020"), Term("0010", "0030"));
                break;
            }

            case "several table versions at the release":
            {
                NewGrid(native);
                NewGrid(native); // a second table version of T_01.00, also valid at 4.4.
                layouts.EqualityRule(1, RuleSheet(layouts, native), Term("0010", "0020"), Term("0010", "0030"));
                break;
            }

            case "ambiguous cell":
            {
                var g = NewGrid(native);
                TwoCells(native, g, g.M1, g.M1);
                // A second cell at (0010, 0020), on another sheet header: without a sheet code the rule is ambiguous.
                var sheetHeader = native.Header(g.Table, "Z", isKey: false, "0100");
                native.Cell(g.Table, native.Variable("fact", g.Metric, (g.Dimension, g.M1)), rowHeader: g.Row, columnHeader: g.ColumnA, sheetHeader: sheetHeader);
                layouts.EqualityRule(1, RuleSheet(layouts, native), Term("0010", "0020"), Term("0010", "0030"));
                break;
            }

            case "shaded or without signature":
            {
                var g = NewGrid(native);
                native.Cell(g.Table, native.Variable("fact", g.Metric, (g.Dimension, g.M1)), excluded: true, rowHeader: g.Row, columnHeader: g.ColumnA);
                native.Cell(g.Table, native.Variable("fact", g.Metric, (g.Dimension, g.M1)), rowHeader: g.Row, columnHeader: g.ColumnB);
                layouts.EqualityRule(1, RuleSheet(layouts, native), Term("0010", "0020"), Term("0010", "0030"));
                break;
            }

            case "signature with a missing code":
            {
                var g = NewGrid(native);
                var late = native.Member("MEM", "qLATE", start: 4); // after 4.4: no code at 4.4.
                TwoCells(native, g, g.M1, late);
                layouts.EqualityRule(1, RuleSheet(layouts, native), Term("0010", "0020"), Term("0010", "0030"));
                break;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(reason));
        }

        return (native, layouts);
    }

    [Theory]
    [InlineData("cell unresolved")]
    [InlineData("release not in the file")]
    [InlineData("no table version at the release")]
    [InlineData("several table versions at the release")]
    [InlineData("ambiguous cell")]
    [InlineData("shaded or without signature")]
    [InlineData("signature with a missing code")]
    public void Equ01_ARuleThatCannotBeResolved_IsNotCheckable_NotAFailure_AndItsReasonIsReported(string reason)
    {
        var (native, layouts) = Unresolvable(reason);
        using (native)
        using (layouts)
        {
            var result = PlaneCTestHelpers.Run(native, layouts);

            var equ01 = PlaneCTestHelpers.Check(result, "C-EQU-01");
            Assert.Equal(CheckStatus.Skipped, equ01.Status);
            Assert.Equal(0, equ01.Failed);
            var equ02 = PlaneCTestHelpers.Check(result, "C-EQU-02");
            Assert.Equal(CheckStatus.Info, equ02.Status);
            Assert.Contains("1 total rules, 0 checkable", equ02.Statement);
            var sample = Assert.Single(equ02.Samples);
            Assert.Equal(reason, sample.BusinessKey);
            Assert.Equal("1", sample.Generated);
        }
    }

    [Fact]
    public void Equ01_AnUnresolvableRuleNextToAResolvableOne_DoesNotHideTheFailureOfTheOther()
    {
        using var native = new SyntheticNativeFile();
        var g = NewGrid(native);
        TwoCells(native, g, g.M1, g.M2);
        using var layouts = new SyntheticLayoutRepository();
        var sheet = RuleSheet(layouts, native);
        layouts.EqualityRule(1, sheet, Term("0010", "0020"), Term("0010", "9990")); // not checkable.
        layouts.EqualityRule(2, sheet, Term("0010", "0020"), Term("0010", "0030")); // checkable, divergent.

        var result = PlaneCTestHelpers.Run(native, layouts);

        var equ01 = PlaneCTestHelpers.Check(result, "C-EQU-01");
        Assert.Equal(CheckStatus.Fail, equ01.Status);
        Assert.Equal(1, equ01.Examined);
        Assert.Equal(1, equ01.Failed);
        Assert.Contains("2 total rules, 1 checkable (0 with identical signature, 1 with different signature), 1 not checkable", PlaneCTestHelpers.Check(result, "C-EQU-02").Statement);
    }

    [Fact]
    public void Equ01_OpenAxisSentinel9999_ResolvesToTheCellWithoutAHeaderOnThatAxis()
    {
        using var native = new SyntheticNativeFile();
        var g = NewGrid(native);
        // Open rows: the cells have no row header.
        native.Cell(g.Table, native.Variable("fact", g.Metric, (g.Dimension, g.M1)), columnHeader: g.ColumnA);
        native.Cell(g.Table, native.Variable("fact", g.Metric, (g.Dimension, g.M2)), columnHeader: g.ColumnB);
        using var layouts = new SyntheticLayoutRepository();
        layouts.EqualityRule(1, RuleSheet(layouts, native), Term("9999", "0020"), Term("9999", "0030"));

        var check = PlaneCTestHelpers.Check(PlaneCTestHelpers.Run(native, layouts), "C-EQU-01");

        Assert.Equal(CheckStatus.Fail, check.Status); // resolved (so checkable) and divergent: M1 against M2.
        Assert.Equal(1, check.Examined);
    }

    [Fact]
    public void Equ01_SheetCode_SelectsTheCellOfThatSheet_AndNoSheetCodeIsAmbiguous()
    {
        using var native = new SyntheticNativeFile();
        var g = NewGrid(native);
        var sheetA = native.Header(g.Table, "Z", isKey: false, "0100");
        var sheetB = native.Header(g.Table, "Z", isKey: false, "0200");
        native.Cell(g.Table, native.Variable("fact", g.Metric, (g.Dimension, g.M1)), rowHeader: g.Row, columnHeader: g.ColumnA, sheetHeader: sheetA);
        native.Cell(g.Table, native.Variable("fact", g.Metric, (g.Dimension, g.M2)), rowHeader: g.Row, columnHeader: g.ColumnA, sheetHeader: sheetB);
        using var layouts = new SyntheticLayoutRepository();
        var sheet = RuleSheet(layouts, native);
        layouts.EqualityRule(1, sheet, Term("0010", "0020", "0100"), Term("0010", "0020", "0200")); // M1 vs M2: divergent.
        layouts.EqualityRule(2, sheet, Term("0010", "0020", "0100"), Term("0010", "0020", "0100")); // the same cell: identical.
        layouts.EqualityRule(3, sheet, Term("0010", "0020"), Term("0010", "0020", "0000")); // no sheet filter: ambiguous.

        var result = PlaneCTestHelpers.Run(native, layouts);

        var equ01 = PlaneCTestHelpers.Check(result, "C-EQU-01");
        Assert.Equal(2, equ01.Examined);
        Assert.Equal(1, equ01.Failed);
        var equ02 = PlaneCTestHelpers.Check(result, "C-EQU-02");
        Assert.Contains("3 total rules, 2 checkable (1 with identical signature, 1 with different signature), 1 not checkable", equ02.Statement);
        Assert.Equal("ambiguous cell", Assert.Single(equ02.Samples).BusinessKey);
    }

    [Fact]
    public void Equ01_RuleIsResolvedAtTheReleaseOfItsSheet()
    {
        // T_01.00 v1 (4.2 only): the two cells have different signatures; v2 (from 4.3): the same one.
        using var native = new SyntheticNativeFile();
        var g1 = NewGrid(native);
        native.Exec($"UPDATE \"TableVersion\" SET \"EndReleaseID\" = 2 WHERE \"TableVID\" = {g1.Table}");
        TwoCells(native, g1, g1.M1, g1.M2);
        var table2 = native.TableVersion("T_01.00", start: 2);
        var row = native.Header(table2, "Y", isKey: false, "0010");
        var columnA = native.Header(table2, "X", isKey: false, "0020");
        var columnB = native.Header(table2, "X", isKey: false, "0030");
        native.Cell(table2, native.Variable("fact", g1.Metric, (g1.Dimension, g1.M1)), rowHeader: row, columnHeader: columnA);
        native.Cell(table2, native.Variable("fact", g1.Metric, (g1.Dimension, g1.M1)), rowHeader: row, columnHeader: columnB);

        using var layouts = new SyntheticLayoutRepository();
        layouts.EqualityRule(1, layouts.Sheet("4.2", "T_01.00"), Term("0010", "0020"), Term("0010", "0030"));
        layouts.EqualityRule(2, layouts.Sheet("4.3", "T_01.00"), Term("0010", "0020"), Term("0010", "0030"));

        var check = PlaneCTestHelpers.Check(PlaneCTestHelpers.Run(native, layouts), "C-EQU-01");

        Assert.Equal(2, check.Examined);
        Assert.Equal(1, check.Failed); // only the rule of the 4.2 sheet diverges.
    }

    [Fact]
    public void Equ01_ALayoutRepositoryWithoutEqualityRules_IsNotAFailure()
    {
        using var native = new SyntheticNativeFile();
        PlaneCTestHelpers.BasicTable(native);
        using var layouts = new SyntheticLayoutRepository(withEqualityRules: false);
        layouts.SignatureCell(layouts.Sheet("4.4", "T_01.00"), "MET(qAAA)", "qDIM(qM1)");

        var result = PlaneCTestHelpers.Run(native, layouts);

        Assert.Equal(CheckStatus.Skipped, PlaneCTestHelpers.Check(result, "C-EQU-01").Status);
        Assert.Contains("0 total rules", PlaneCTestHelpers.Check(result, "C-EQU-02").Statement);
    }

    // ------------------------------------------------------------------ C-MHR-01 / C-MHR-02

    private static (SyntheticNativeFile Native, long Table, long Metric) HierarchyNative(string domain = "qCO", string hierarchy = "new_CO5")
    {
        var native = new SyntheticNativeFile();
        var metric = native.Property("qAAA");
        var table = native.TableVersion("T_01.00");
        native.Header(table, "X", isKey: false, "0010", metric, native.SubCategory(domain, hierarchy));
        return (native, table, metric);
    }

    private static SyntheticLayoutRepository HierarchyLayouts(
        string axis = "X", string ordinate = "0010", string domain = "qCO", string hierarchy = "new_CO5", string metric = "qAAA")
    {
        var layouts = new SyntheticLayoutRepository();
        layouts.MetricOrdinate(layouts.Sheet("4.4", "T_01.00"), axis, ordinate, metric, domain, hierarchy);
        return layouts;
    }

    [Fact]
    public void Mhr01_MatchingHierarchy_Passes()
    {
        var (native, _, _) = HierarchyNative();
        using (native)
        using (var layouts = HierarchyLayouts())
        {
            var check = PlaneCTestHelpers.Check(PlaneCTestHelpers.Run(native, layouts), "C-MHR-01");

            Assert.Equal(CheckStatus.Pass, check.Status);
            Assert.Equal(ValidationLayer.Critical, check.Layer);
            Assert.Equal(1, check.Examined);
        }
    }

    [Fact]
    public void Mhr01_DifferentSubCategoryCode_Fails_WithBothHierarchiesInTheSample()
    {
        var (native, _, _) = HierarchyNative(hierarchy: "new_CO6");
        using (native)
        using (var layouts = HierarchyLayouts())
        {
            var check = PlaneCTestHelpers.Check(PlaneCTestHelpers.Run(native, layouts), "C-MHR-01");

            Assert.Equal(CheckStatus.Fail, check.Status);
            var sample = Assert.Single(check.Samples);
            Assert.Equal("T_01.00\t4.4|X|0010|qAAA", sample.BusinessKey);
            Assert.Equal("qCO:new_CO6", sample.Generated);
            Assert.Equal("qCO:new_CO5", sample.Reference);
        }
    }

    [Fact]
    public void Mhr01_DifferentCategoryCode_Fails()
    {
        var (native, _, _) = HierarchyNative(domain: "qXX");
        using (native)
        using (var layouts = HierarchyLayouts())
        {
            var check = PlaneCTestHelpers.Check(PlaneCTestHelpers.Run(native, layouts), "C-MHR-01");

            Assert.Equal(CheckStatus.Fail, check.Status);
            var sample = Assert.Single(check.Samples);
            Assert.Equal("qXX:new_CO5", sample.Generated);
            Assert.Equal("qCO:new_CO5", sample.Reference);
        }
    }

    [Fact]
    public void Mhr01_EbaPrefixOfTheLayoutDomain_IsNormalised_OnBothSides()
    {
        var (native, _, _) = HierarchyNative(domain: "eba_dom:qCO");
        using (native)
        using (var layouts = HierarchyLayouts(domain: "qCO"))
        {
            Assert.Equal(CheckStatus.Pass, PlaneCTestHelpers.Check(PlaneCTestHelpers.Run(native, layouts), "C-MHR-01").Status);
        }
    }

    [Fact]
    public void Mhr02_HeaderWithoutAHierarchy_IsNotAViolation_AndIsCounted_ButNothingWasCompared()
    {
        using var native = new SyntheticNativeFile();
        var metric = native.Property("qAAA");
        var table = native.TableVersion("T_01.00");
        native.Header(table, "X", isKey: false, "0010", metric, subCategoryVid: null);
        using var layouts = HierarchyLayouts();

        var result = PlaneCTestHelpers.Run(native, layouts);

        var mhr01 = PlaneCTestHelpers.Check(result, "C-MHR-01");
        Assert.Equal(CheckStatus.Skipped, mhr01.Status); // zero compared: skipped, never pass.
        Assert.Contains(
            "1 total units, 0 matched and compared, 1 matched with no hierarchy on the header (not violations)",
            PlaneCTestHelpers.Check(result, "C-MHR-02").Statement);
    }

    [Theory]
    [InlineData("key header", "ordinate not found among the non-key headers of the axis")]
    [InlineData("other axis", "ordinate not found among the non-key headers of the axis")]
    [InlineData("other ordinate", "ordinate not found among the non-key headers of the axis")]
    [InlineData("other metric", "header without that metric")]
    public void Mhr02_AUnitThatMatchesNoHeader_IsNotMatched_WithItsReason_AndIsNotAViolation(string kind, string reason)
    {
        using var native = new SyntheticNativeFile();
        var metric = native.Property("qAAA");
        var otherMetric = native.Property("qBBB");
        var table = native.TableVersion("T_01.00");
        var subCategory = native.SubCategory("qCO", "new_CO5");
        switch (kind)
        {
            case "key header":
                native.Header(table, "X", isKey: true, "0010", metric, subCategory);
                break;
            case "other axis":
                native.Header(table, "Y", isKey: false, "0010", metric, subCategory);
                break;
            case "other ordinate":
                native.Header(table, "X", isKey: false, "0020", metric, subCategory);
                break;
            default:
                native.Header(table, "X", isKey: false, "0010", otherMetric, subCategory);
                break;
        }

        using var layouts = HierarchyLayouts();
        var result = PlaneCTestHelpers.Run(native, layouts);

        Assert.Equal(CheckStatus.Skipped, PlaneCTestHelpers.Check(result, "C-MHR-01").Status);
        var mhr02 = PlaneCTestHelpers.Check(result, "C-MHR-02");
        Assert.Contains($"1 not matched ({reason}: 1)", mhr02.Statement);
        Assert.Contains(mhr02.Samples, s => s.BusinessKey == $"T_01.00\t4.4|X|0010|qAAA [{reason}]");
    }

    [Fact]
    public void Mhr02_SheetLevelMetricValue_IsNotExamined_AndIsCounted()
    {
        var (native, _, _) = HierarchyNative();
        using (native)
        using (var layouts = HierarchyLayouts())
        {
            layouts.MetricOrdinate(layouts.Sheet("4.4", "T_02.00"), "X", "0010", "qAAA", "qCO", "new_CO5", sheetLevel: true);

            var result = PlaneCTestHelpers.Run(native, layouts);

            Assert.Contains("1 sheet-level value(s) (no ordinate) not examined", PlaneCTestHelpers.Check(result, "C-MHR-02").Statement);
            Assert.Equal(CheckStatus.Pass, PlaneCTestHelpers.Check(result, "C-MHR-01").Status);
        }
    }

    [Fact]
    public void Mhr01_MetricWithoutABracket_IsNotAUnit()
    {
        var (native, _, _) = HierarchyNative(hierarchy: "new_CO6");
        using (native)
        using (var layouts = new SyntheticLayoutRepository())
        {
            layouts.MetricOrdinate(layouts.Sheet("4.4", "T_01.00"), "X", "0010", "qAAA", hierarchyDomain: null, hierarchy: null);

            var result = PlaneCTestHelpers.Run(native, layouts);

            Assert.Equal(CheckStatus.Skipped, PlaneCTestHelpers.Check(result, "C-MHR-01").Status);
            Assert.Contains("0 total units", PlaneCTestHelpers.Check(result, "C-MHR-02").Statement);
        }
    }

    [Fact]
    public void Mhr01_LayoutRepositoryBuiltBeforeTheBracketWasStored_IsSkippedWithTheReason_NotPassed()
    {
        var (native, _, _) = HierarchyNative(hierarchy: "new_CO6");
        using (native)
        using (var layouts = new SyntheticLayoutRepository(withHierarchyColumns: false))
        {
            layouts.Sheet("4.4", "T_01.00");

            var result = PlaneCTestHelpers.Run(native, layouts);

            var mhr01 = PlaneCTestHelpers.Check(result, "C-MHR-01");
            Assert.Equal(CheckStatus.Skipped, mhr01.Status);
            Assert.Contains("re-extract the layouts", mhr01.SkipReason);
            Assert.Equal(CheckStatus.Skipped, PlaneCTestHelpers.Check(result, "C-MHR-02").Status);
        }
    }
}
