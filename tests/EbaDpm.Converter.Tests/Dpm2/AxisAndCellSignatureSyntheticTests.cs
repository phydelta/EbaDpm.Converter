using EbaDpm.Converter.Tests.Dictionary;

namespace EbaDpm.Converter.Tests.Dpm2;

/// <summary>
/// Synthetic coverage (issue #14) of the categorisation and the signatures the DPM 2.0 axis and
/// cell loader emits: key-header fixed pairs in the cell signature, default members, the open-axis
/// hierarchy bracket (<c>DPS</c> with the hierarchy code, <c>DatapointSignature</c> with its ID),
/// the sub-category fallback, ordinal ordering, the projection of the variable context and the
/// one-dimension-one-axis arbitration. Expected values come from <c>docs/mapping-dpm2.md</c>
/// ("mOrdinateCategorisation", "mOpenAxisValueRestriction", "Signatures") and the loader's
/// documented comments, never from running the code. Same fixture as
/// <see cref="KeyHeaderFixedPairsSyntheticTests"/> (in-memory rows, real-schema destination).
/// </summary>
public sealed class AxisAndCellSignatureSyntheticTests
{
    private static List<string> Pairs(string pairs)
        => pairs.Split(';', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal).ToList();

    /// <summary>Closed X column <c>0010</c> with the metric <c>met1</c> plus a live cell on it.</summary>
    private static (int Column, int Metric) AddMetricColumn(AxisCellWorld w, bool metricIsDefault = false, int? contextId = null)
    {
        var metric = w.AddMetric("met1", metricIsDefault);
        var column = w.AddHeader("X", "0010", propertyId: metric);
        w.AddCell(column, contextId: contextId);
        return (column, metric);
    }

    // ---- GAP-4: key-header fixed pairs in the cell signature ----

    [Fact]
    public void KeyHeaderFixedPairs_AreInTheCellDpsAndDatapointSignature_MetricFirst_ThenOrdinalDimensionOrder()
    {
        var w = new AxisCellWorld();
        // dA's XBRL code starts with an upper-case 'A', which sorts BEFORE "MET" in an ordinal
        // ordering: only "metric first" puts MET at the head.
        var dA = w.AddDimension("dA", ["a1"], xbrlCode: "A_dim");
        var dB = w.AddDimension("dB", ["b1"]);
        var dOpen = w.AddDimension("dOpen", []);
        AddMetricColumn(w);
        w.AddHeader("X", "0020", isKey: true, propertyId: dOpen.PropertyId, contextId: w.AddContext((dB, "b1"), (dA, "a1")));
        using var outcome = w.Run();

        var cell = Assert.Single(outcome.Cells());
        Assert.Equal(0, cell.IsShaded);
        const string expected = "MET(eba_met1)|A_dim(eba_a1)|eba_dB(eba_b1)|eba_dOpen(*)";
        Assert.Equal(expected, cell.Dps);
        Assert.Equal(expected, cell.DatapointSignature);
        Assert.Equal(["dA=a1", "dB=b1", "dOpen=<open>"], outcome.Categorisation("0020"));
    }

    // ---- GAP-5: default members ----

    [Fact]
    public void DefaultMemberFixedPair_StaysInTheOrdinateCategorisation_ButNotInTheCellSignature_AndADefaultMetricStays()
    {
        var w = new AxisCellWorld();
        var dA = w.AddDimension("dA", ["a0", "a1"], defaultMember: "a0");
        var dB = w.AddDimension("dB", ["b1"]);
        var dOpen = w.AddDimension("dOpen", []);
        AddMetricColumn(w, metricIsDefault: true);
        w.AddHeader("X", "0020", isKey: true, propertyId: dOpen.PropertyId, contextId: w.AddContext((dA, "a0"), (dB, "b1")));
        using var outcome = w.Run();

        // The categorisation keeps the default pair (the effective context of the ordinate)...
        Assert.Equal(["dA=a0", "dB=b1", "dOpen=<open>"], outcome.Categorisation("0020"));
        // ...the metric row too, although its member is flagged default...
        Assert.Contains("MET=met1", outcome.Categorisation("0010"));
        // ...but the cell signature drops the default pair and keeps the default metric.
        var cell = Assert.Single(outcome.Cells());
        Assert.Equal("MET(eba_met1)|eba_dB(eba_b1)|eba_dOpen(*)", cell.Dps);
        Assert.Equal("MET(eba_met1)|eba_dB(eba_b1)|eba_dOpen(*)", cell.DatapointSignature);
    }

    // ---- GAP-6: a key-header context declaring the open dimension itself ----

    [Fact]
    public void KeyHeaderContext_DeclaringTheOpenDimensionItself_KeepsOneOpenRow_AndReportsTheAnomaly()
    {
        var w = new AxisCellWorld();
        var dA = w.AddDimension("dA", ["a1"]);
        var dOpen = w.AddDimension("dOpen", ["o1"]);
        AddMetricColumn(w);
        var context = w.AddContext((dOpen, "o1"), (dA, "a1"));
        w.AddHeader("X", "0020", isKey: true, propertyId: dOpen.PropertyId, contextId: context);
        using var outcome = w.Run();

        // One row for the open dimension, with the open member; the other fixed pair stays.
        Assert.Equal(["dA=a1", "dOpen=<open>"], outcome.Categorisation("0020"));
        Assert.Equal(1, outcome.Count("SELECT COUNT(*) FROM \"mOrdinateCategorisation\" c JOIN \"mDimension\" d ON d.\"DimensionID\" = c.\"DimensionID\" WHERE d.\"DimensionCode\" = 'dOpen'"));
        Assert.Equal("MET(eba_met1)|eba_dA(eba_a1)|eba_dOpen(*)", Assert.Single(outcome.Cells()).Dps);
        Assert.Contains(outcome.Result.StructuralAnomalies, a => a.Contains("declares a FIXED pair for its SAME") && a.Contains("the open one wins"));
        Assert.Contains(outcome.Result.StructuralAnomalies, a => a.Contains("contributes 1 FIXED pair(s)"));
    }

    [Fact]
    public void KeyHeaderContext_DeclaringOnlyTheOpenDimension_ContributesNoFixedPair()
    {
        var w = new AxisCellWorld();
        var dOpen = w.AddDimension("dOpen", ["o1"]);
        AddMetricColumn(w);
        w.AddHeader("X", "0020", isKey: true, propertyId: dOpen.PropertyId, contextId: w.AddContext((dOpen, "o1")));
        using var outcome = w.Run();

        Assert.Equal(["dOpen=<open>"], outcome.Categorisation("0020"));
        Assert.Equal("MET(eba_met1)|eba_dOpen(*)", Assert.Single(outcome.Cells()).Dps);
        Assert.Contains(outcome.Result.StructuralAnomalies, a => a.Contains("declares a FIXED pair for its SAME"));
        Assert.DoesNotContain(outcome.Result.StructuralAnomalies, a => a.Contains("contributes"));
    }

    // ---- GAP-7 / GAP-8: the open-axis hierarchy restriction and its bracket ----

    private static AxisCellWorld RestrictedOpenAxisWorld(int subCategoryVId)
    {
        var w = new AxisCellWorld();
        var dOpen = w.AddDimension("dOpen", []);
        AddMetricColumn(w);
        w.AddHeader("X", "0020", isKey: true, propertyId: dOpen.PropertyId, subCategoryVId: subCategoryVId);
        return w;
    }

    private static readonly Dictionary<int, (int HierarchyId, string HierarchyCode, int DomainId)> HierarchyOfSubCategory7 =
        new() { [7] = (42, "H_CODE", 1) };

    private static void AssertRestrictedBracket(AxisCellWorld.Outcome outcome)
    {
        // DPS carries the hierarchy CODE, DatapointSignature the hierarchy ID.
        var cell = Assert.Single(outcome.Cells());
        Assert.Equal("MET(eba_met1)|eba_dOpen(*[H_CODE])", cell.Dps);
        Assert.Equal("MET(eba_met1)|eba_dOpen(*[42])", cell.DatapointSignature);

        // The open pair of the ordinate: same split between DPS and DimensionMemberSignature.
        var open = Assert.Single(outcome.CategorisationRows("0020"));
        Assert.Equal("dOpen=<open>", open.Pair);
        Assert.Equal("eba_dOpen(*[H_CODE])", open.Dps);
        Assert.Equal("eba_dOpen(*[42])", open.Dms);

        // One mOpenAxisValueRestriction row for the open axis: no starting member, not included.
        var restriction = Assert.Single(QueryHelpers.Rows(
            outcome.Connection,
            """
            SELECT r."HierarchyID", r."HierarchyStartingMemberID", r."IsStartingMemberIncluded", a."AxisCode", a."IsOpenAxis"
            FROM "mOpenAxisValueRestriction" r JOIN "mAxis" a ON a."AxisID" = r."AxisID"
            """,
            5));
        Assert.Equal("42|-|0|0020|1", string.Join("|", restriction.Select(v => v ?? "-")));
        Assert.Equal(1, outcome.Result.OpenAxisValueRestrictionRows);
        Assert.Equal(0, outcome.Result.UnresolvedOpenAxisRestrictions);
    }

    [Fact]
    public void SubCategoryOfTheKeyHeader_WritesTheRestrictionRow_AndTheBracketWithCodeInDps_IdInDatapointSignature()
    {
        var w = RestrictedOpenAxisWorld(subCategoryVId: 77);
        w.Source.SubCategoryVersions.Add(new(SubCategoryVId: 77, SubCategoryId: 7, StartReleaseId: 1, EndReleaseId: null));
        using var outcome = w.Run(HierarchyOfSubCategory7);

        AssertRestrictedBracket(outcome);
        Assert.DoesNotContain(outcome.Result.StructuralAnomalies, a => a.Contains("FALLBACK"));
    }

    [Fact]
    public void KeyHeaderWithoutSubCategory_HasNoRestrictionRow_AndAnUnbracketedOpenPair()
    {
        var w = new AxisCellWorld();
        var dOpen = w.AddDimension("dOpen", []);
        AddMetricColumn(w);
        w.AddHeader("X", "0020", isKey: true, propertyId: dOpen.PropertyId);
        using var outcome = w.Run(HierarchyOfSubCategory7);

        Assert.Equal(0, outcome.Count("SELECT COUNT(*) FROM \"mOpenAxisValueRestriction\""));
        Assert.Equal("MET(eba_met1)|eba_dOpen(*)", Assert.Single(outcome.Cells()).Dps);
        Assert.Equal("MET(eba_met1)|eba_dOpen(*)", Assert.Single(outcome.Cells()).DatapointSignature);
    }

    [Fact]
    public void SubCategoryVersion_NoLongerCurrent_ResolvesByFallback_AndLogsIt()
    {
        var w = RestrictedOpenAxisWorld(subCategoryVId: 77);
        // Not current at the cutoff: only the unfiltered read returns it.
        w.Source.NonCurrentSubCategoryVersions.Add(new(SubCategoryVId: 77, SubCategoryId: 7, StartReleaseId: 1, EndReleaseId: 3));
        using var outcome = w.Run(HierarchyOfSubCategory7);

        AssertRestrictedBracket(outcome);
        Assert.Contains(
            outcome.Result.StructuralAnomalies,
            a => a.Contains("FALLBACK") && a.Contains("SubCategoryVID=77") && a.Contains("SubCategoryID=7"));
    }

    [Fact]
    public void SubCategoryVersion_UnknownEvenWithoutTheCutoff_IsCountedAndWritesNoRestriction()
    {
        var w = RestrictedOpenAxisWorld(subCategoryVId: 88);
        w.Source.SubCategoryVersions.Add(new(SubCategoryVId: 77, SubCategoryId: 7, StartReleaseId: 1, EndReleaseId: null));
        using var outcome = w.Run(HierarchyOfSubCategory7);

        Assert.Equal(1, outcome.Result.UnresolvedOpenAxisRestrictions);
        Assert.Equal(0, outcome.Result.OpenAxisValueRestrictionRows);
        Assert.Equal(0, outcome.Count("SELECT COUNT(*) FROM \"mOpenAxisValueRestriction\""));
        Assert.Equal("MET(eba_met1)|eba_dOpen(*)", Assert.Single(outcome.Cells()).Dps);
        Assert.DoesNotContain(outcome.Result.StructuralAnomalies, a => a.Contains("FALLBACK"));
    }

    [Fact]
    public void SubCategoryVersion_WithoutAHierarchy_IsCountedAndWritesNoRestriction()
    {
        var w = RestrictedOpenAxisWorld(subCategoryVId: 77);
        w.Source.SubCategoryVersions.Add(new(SubCategoryVId: 77, SubCategoryId: 7, StartReleaseId: 1, EndReleaseId: null));
        using var outcome = w.Run(); // no hierarchy for SubCategoryID 7

        Assert.Equal(1, outcome.Result.UnresolvedOpenAxisRestrictions);
        Assert.Equal(0, outcome.Count("SELECT COUNT(*) FROM \"mOpenAxisValueRestriction\""));
        Assert.Equal("MET(eba_met1)|eba_dOpen(*)", Assert.Single(outcome.Cells()).Dps);
    }

    // ---- GAP-14: ordinal ordering of the dimension codes ----

    [Fact]
    public void CellSignature_OrdersDimensionsByOrdinalXbrlCode_UpperCaseBeforeLowerCase()
    {
        var w = new AxisCellWorld();
        // Declared in a non-sorted order, with codes whose case-insensitive order (a1, B1, Z1)
        // differs from the ordinal one (B1, Z1, a1).
        var z1 = w.AddDimension("Z1", ["m_Z"]);
        var a1 = w.AddDimension("a1", ["m_a"]);
        var b1 = w.AddDimension("B1", ["m_B"]);
        AddMetricColumn(w, contextId: w.AddContext((z1, "m_Z"), (a1, "m_a"), (b1, "m_B")));
        using var outcome = w.Run();

        var cell = Assert.Single(outcome.Cells());
        const string expected = "MET(eba_met1)|eba_B1(eba_m_B)|eba_Z1(eba_m_Z)|eba_a1(eba_m_a)";
        Assert.Equal(expected, cell.Dps);
        Assert.Equal(expected, cell.DatapointSignature);
    }

    // ---- GAP-16: projection of the variable context onto the ordinates ----

    private static (AxisCellWorld World, int X1, int Y1, int Y2) ProjectionWorld(
        Func<AxisCellWorld, AxisCellWorld.Dimension, AxisCellWorld.Dimension, (int Cell11Context, int Cell12Context)> contexts)
    {
        var w = new AxisCellWorld();
        var dA = w.AddDimension("dA", ["a0", "a1", "a2"], defaultMember: "a0");
        var dB = w.AddDimension("dB", ["b0", "b1"], defaultMember: "b0");
        var metric = w.AddMetric("met1");
        var x1 = w.AddHeader("X", "X1", propertyId: metric);
        var y1 = w.AddHeader("Y", "Y1");
        var y2 = w.AddHeader("Y", "Y2");
        var (c11, c12) = contexts(w, dA, dB);
        w.AddCell(x1, y1, contextId: c11);
        w.AddCell(x1, y2, contextId: c12);
        return (w, x1, y1, y2);
    }

    [Fact]
    public void Projection_AllCellsAgree_GivesThePair_NoneCarriesIt_ResetsToTheDefaultMember_MixedGivesNothing()
    {
        // Cell (X1,Y1) carries dA=a1; cell (X1,Y2) carries dB=b1. The universe is {dA, dB}.
        var (w, _, _, _) = ProjectionWorld((world, dA, dB) => (world.AddContext((dA, "a1")), world.AddContext((dB, "b1"))));
        using var outcome = w.Run();

        // X1: its two cells disagree on presence of each dimension (mixed) -> nothing but the metric.
        Assert.Equal(["MET=met1"], outcome.Categorisation("X1"));
        // Y1: its only cell carries dA=a1 (agree) and no dB (reset to dB's default member b0).
        Assert.Equal(["dA=a1", "dB=b0"], outcome.Categorisation("Y1"));
        // Y2: the mirror image.
        Assert.Equal(["dA=a0", "dB=b1"], outcome.Categorisation("Y2"));

        // The cell signatures drop the reset (default) pair and keep the carried one.
        Assert.Equal("MET(eba_met1)|eba_dA(eba_a1)", outcome.Cell("{T_01,Y1,X1}").Dps);
        Assert.Equal("MET(eba_met1)|eba_dB(eba_b1)", outcome.Cell("{T_01,Y2,X1}").Dps);
    }

    [Fact]
    public void Projection_CellsCarryingTheDimensionWithDifferentMembers_GiveNoPairOnTheSharedOrdinate()
    {
        var (w, _, _, _) = ProjectionWorld((world, dA, _) => (world.AddContext((dA, "a1")), world.AddContext((dA, "a2"))));
        using var outcome = w.Run();

        // X1 sees dA=a1 in one cell and dA=a2 in the other: they disagree, so nothing.
        Assert.Equal(["MET=met1"], outcome.Categorisation("X1"));
        Assert.Equal(["dA=a1"], outcome.Categorisation("Y1"));
        Assert.Equal(["dA=a2"], outcome.Categorisation("Y2"));
    }

    // ---- GAP-17: one dimension, one axis ----

    [Theory]
    // yDeclares, xDeclares, tableDeclares -> pairs of X1 ; pairs of Y1
    [InlineData(true, false, false, "MET=met1", "dA=a1")]                 // level 1: the Y header declares it
    [InlineData(false, true, false, "MET=met1;dA=a1", "")]                // level 1: the X header declares it
    [InlineData(false, false, true, "MET=met1;dA=a1", "")]                // level 2: the table context goes to X
    [InlineData(true, false, true, "MET=met1", "dA=a1")]                  // level 1 beats level 2
    [InlineData(false, false, false, "MET=met1;dA=a1", "dA=a1")]          // level 3: unarbitrated, left as is
    public void DimensionProjectedOnTwoAxes_IsKeptOnlyOnTheOwnerAxis(
        bool yDeclares, bool xDeclares, bool tableDeclares, string expectedX, string expectedY)
    {
        var w = new AxisCellWorld();
        var dA = w.AddDimension("dA", ["a1"]);
        var metric = w.AddMetric("met1");
        var x1 = w.AddHeader("X", "X1", propertyId: metric, contextId: xDeclares ? w.AddContext((dA, "a1")) : null);
        var y1 = w.AddHeader("Y", "Y1", contextId: yDeclares ? w.AddContext((dA, "a1")) : null);
        if (tableDeclares)
        {
            w.TableContextId = w.AddContext((dA, "a1"));
        }

        // The only cell carries dA=a1, so the projection puts the pair on BOTH closed axes.
        w.AddCell(x1, y1, contextId: w.AddContext((dA, "a1")));
        using var outcome = w.Run();

        Assert.Equal(Pairs(expectedX), outcome.Categorisation("X1"));
        Assert.Equal(Pairs(expectedY), outcome.Categorisation("Y1"));

        var reported = outcome.Result.StructuralAnomalies.Any(a => a.Contains("on more than one axis"));
        Assert.Equal(!yDeclares && !xDeclares && !tableDeclares, reported);
    }
}
