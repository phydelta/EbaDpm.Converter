using EbaDpm.Converter.Core.Mapping.Dpm20;

namespace EbaDpm.Converter.Tests.Dpm2;

/// <summary>
/// Direct synthetic coverage (issue #14, GAP-13) of the pure signature functions of the DPM 2.0
/// cell loader: <c>OrientationRank</c>, <c>ComposeBusinessCode</c>, <c>ComposeDps</c>,
/// <c>ComposeDms</c> and <c>ComposeCellSignature</c>. Every expected value is written by hand from
/// <c>docs/mapping-dpm2.md</c> ("Cells", "Signatures"), <c>docs/mapping-dpm1.md</c> ("Data point
/// signatures") and the composer's doc comments, never copied from running the code. No data files.
/// </summary>
public sealed class Dpm20CellSignatureComposerTests
{
    // The metric dimension (and the open-axis member sentinel) is 9999 in the composer.
    private const int Met = 9999;
    private const int Sentinel = 9999;

    private sealed record Pair(
        int DimensionId, string DimensionXbrlCode, int MemberId, string? MemberXbrlCode,
        bool IsDefault = false, (int HierarchyId, string HierarchyCode)? Restriction = null);

    private static Pair Closed(int dimId, string dim, int memberId, string member, bool isDefault = false)
        => new(dimId, dim, memberId, member, isDefault);

    private static Pair Metric(string member, bool isDefault = false, (int, string)? restriction = null)
        => new(Met, "MET", 1, member, isDefault, restriction);

    private static Pair Open(int dimId, string dim, (int, string)? restriction = null)
        => new(dimId, dim, Sentinel, null, false, restriction);

    /// <summary>Runs ComposeCellSignature: own pairs and parents are given per ordinate ID.</summary>
    private static (string? Dps, string? Dms) Compose(
        int[] cellOrdinates,
        Dictionary<int, Pair[]> own,
        Dictionary<int, int?>? parents = null)
    {
        var categorisations = own.ToDictionary(
            kv => kv.Key,
            kv => kv.Value
                .Select(p => (p.DimensionId, p.DimensionXbrlCode, p.MemberId, p.MemberXbrlCode, p.IsDefault, p.Restriction))
                .ToList());

        return Dpm20CellSignatureComposer.ComposeCellSignature(
            [.. cellOrdinates], categorisations, parents ?? new Dictionary<int, int?>());
    }

    // ---- OrientationRank ----

    [Theory]
    [InlineData("Y", 0)]
    [InlineData("X", 1)]
    [InlineData("Z", 2)]
    [InlineData("Q", 3)]
    [InlineData("", 3)]
    public void OrientationRank_IsYThenXThenZ_AnythingElseLast(string orientation, int expected)
        => Assert.Equal(expected, Dpm20CellSignatureComposer.OrientationRank(orientation));

    // ---- ComposeBusinessCode ----

    [Fact]
    public void ComposeBusinessCode_OrdersByOrientationRankNotByListOrderOrCode()
    {
        // Docs: ordinate codes ordered by orientation Y, X, Z.
        var rank = new Dictionary<int, (int Rank, int AxisId)> { [1] = (2, 10), [2] = (0, 11), [3] = (1, 12) };
        var code = new Dictionary<int, string> { [1] = "Z1", [2] = "Y1", [3] = "X1" };

        Assert.Equal("{T_01,Y1,X1,Z1}", Dpm20CellSignatureComposer.ComposeBusinessCode("T_01", [1, 2, 3], rank, code));
    }

    [Fact]
    public void ComposeBusinessCode_SameOrientation_GoesByDescendingAxisId()
    {
        // Docs: "When several open Y axes coexist, their codes go by descending axis ID".
        var rank = new Dictionary<int, (int Rank, int AxisId)> { [1] = (0, 5), [2] = (0, 9), [3] = (0, 7), [4] = (1, 1) };
        var code = new Dictionary<int, string> { [1] = "a", [2] = "b", [3] = "c", [4] = "x" };

        Assert.Equal("{T_01,b,c,a,x}", Dpm20CellSignatureComposer.ComposeBusinessCode("T_01", [1, 2, 3, 4], rank, code));
    }

    [Fact]
    public void ComposeBusinessCode_UnknownOrientationRank_GoesAfterZ()
    {
        var rank = new Dictionary<int, (int Rank, int AxisId)>
        {
            [1] = (Dpm20CellSignatureComposer.OrientationRank("Q"), 99),
            [2] = (Dpm20CellSignatureComposer.OrientationRank("Z"), 1),
        };
        var code = new Dictionary<int, string> { [1] = "Q1", [2] = "Z1" };

        Assert.Equal("{T_01,Z1,Q1}", Dpm20CellSignatureComposer.ComposeBusinessCode("T_01", [1, 2], rank, code));
    }

    // ---- ComposeDps / ComposeDms (one pair) ----

    [Fact]
    public void ComposeDpsAndDms_Metric_IsMetOfMemberCode_AndNeverCarriesABracket()
    {
        // Docs: a restriction attached to a metric categorisation never produces brackets.
        Assert.Equal("MET(ei1)", Dpm20CellSignatureComposer.ComposeDps(Met, "MET", 1, "ei1", (3, "HC")));
        Assert.Equal("MET(ei1)", Dpm20CellSignatureComposer.ComposeDms(Met, "MET", 1, "ei1", (3, "HC")));
        // Even when the member ID collides with the open-axis sentinel (9999), the metric branch wins.
        Assert.Equal("MET(ei1)", Dpm20CellSignatureComposer.ComposeDps(Met, "MET", Sentinel, "ei1", (3, "HC")));
        Assert.Equal("MET(ei1)", Dpm20CellSignatureComposer.ComposeDms(Met, "MET", Sentinel, "ei1", (3, "HC")));
    }

    [Fact]
    public void ComposeDpsAndDms_ClosedPair_IsDimensionOfMemberCode_RestrictionIgnored()
    {
        Assert.Equal("BAS(x1)", Dpm20CellSignatureComposer.ComposeDps(5, "BAS", 77, "x1", (3, "HC")));
        Assert.Equal("BAS(x1)", Dpm20CellSignatureComposer.ComposeDms(5, "BAS", 77, "x1", (3, "HC")));
    }

    [Fact]
    public void ComposeDpsAndDms_OpenAxis_WithoutRestriction_IsAsterisk()
    {
        Assert.Equal("qCOE(*)", Dpm20CellSignatureComposer.ComposeDps(5, "qCOE", Sentinel, null));
        Assert.Equal("qCOE(*)", Dpm20CellSignatureComposer.ComposeDms(5, "qCOE", Sentinel, null));
    }

    [Fact]
    public void ComposeDpsAndDms_OpenAxis_WithRestriction_DpsHasHierarchyCode_DmsHasHierarchyId()
    {
        // Docs: dim(*[HierarchyCode]) in DPS, dim(*[HierarchyID]) in the ID variant.
        Assert.Equal("qCOE(*[BT3])", Dpm20CellSignatureComposer.ComposeDps(5, "qCOE", Sentinel, null, (31, "BT3")));
        Assert.Equal("qCOE(*[31])", Dpm20CellSignatureComposer.ComposeDms(5, "qCOE", Sentinel, null, (31, "BT3")));
    }

    // ---- ComposeCellSignature: nearest ancestor wins ----

    [Fact]
    public void CellSignature_OwnPairBeatsParentPair_ForTheSameDimension()
    {
        // Ordinate 1 (child of 2) carries d1=a; its parent carries d1=b. Nearest (depth 0) wins.
        var (dps, dms) = Compose([1],
            new() { [1] = [Closed(10, "d1", 100, "x:a")], [2] = [Closed(10, "d1", 101, "x:b")] },
            new() { [1] = 2, [2] = null });

        Assert.Equal("d1(x:a)", dps);
        Assert.Equal("d1(x:a)", dms);
    }

    [Fact]
    public void CellSignature_PairInheritedFromTheNearestAncestor_BeatsTheFartherOne()
    {
        // 1 -> 2 -> 3. Ordinate 1 has no pair for d1; 2 (depth 1) says b; 3 (depth 2) says c.
        var (dps, _) = Compose([1],
            new() { [2] = [Closed(10, "d1", 101, "x:b")], [3] = [Closed(10, "d1", 102, "x:c")] },
            new() { [1] = 2, [2] = 3, [3] = null });

        Assert.Equal("d1(x:b)", dps);
    }

    [Fact]
    public void CellSignature_DimensionsOfDifferentAncestors_AreAllCollected()
    {
        // Each dimension is arbitrated on its own: d1 comes from the parent, d2 from the child.
        var (dps, _) = Compose([1],
            new() { [1] = [Closed(11, "d2", 200, "x:q")], [2] = [Closed(10, "d1", 100, "x:a")] },
            new() { [1] = 2, [2] = null });

        Assert.Equal("d1(x:a)|d2(x:q)", dps);
    }

    [Fact]
    public void CellSignature_NearestIsComparedAcrossAllOrdinatesOfTheCell_NotAxisByAxis()
    {
        // Docs: pairs collected from every ordinate of the cell and its ancestors, nearest wins.
        // Ordinate 1 is a root that states d1=near (depth 0); ordinate 2 inherits d1=far at depth 1.
        // The order the ordinates are listed in must not matter.
        var own = new Dictionary<int, Pair[]>
        {
            [1] = [Closed(10, "d1", 100, "x:near")],
            [3] = [Closed(10, "d1", 101, "x:far")],
        };
        var parents = new Dictionary<int, int?> { [1] = null, [2] = 3, [3] = null };

        Assert.Equal("d1(x:near)", Compose([1, 2], own, parents).Dps);
        Assert.Equal("d1(x:near)", Compose([2, 1], own, parents).Dps);
    }

    [Fact]
    public void CellSignature_EachWinningPairKeepsTheRestrictionOfTheOrdinateThatContributedIt()
    {
        // The open pair of d1 comes from the parent (hierarchy 31/BT3); the child states d2 closed.
        var (dps, dms) = Compose([1],
            new() { [1] = [Closed(11, "d2", 200, "x:q")], [2] = [Open(10, "d1", (31, "BT3"))] },
            new() { [1] = 2, [2] = null });

        Assert.Equal("d1(*[BT3])|d2(x:q)", dps);
        Assert.Equal("d1(*[31])|d2(x:q)", dms);
    }

    [Fact]
    public void CellSignature_MissingParentEntry_IsTreatedAsARoot()
    {
        var (dps, _) = Compose([1], new() { [1] = [Closed(10, "d1", 100, "x:a")] }, new Dictionary<int, int?>());

        Assert.Equal("d1(x:a)", dps);
    }

    // ---- ComposeCellSignature: MaxOrdinateTreeDepth (64) ----

    private static (Dictionary<int, Pair[]> Own, Dictionary<int, int?> Parents) Chain(int length)
    {
        // Ordinates 1..length, each the child of the next; the root (length) carries d1.
        var parents = new Dictionary<int, int?>();
        for (var i = 1; i <= length; i++)
        {
            parents[i] = i == length ? null : i + 1;
        }

        return (new Dictionary<int, Pair[]> { [length] = [Closed(10, "d1", 100, "x:a")] }, parents);
    }

    [Fact]
    public void CellSignature_ChainOf64Ordinates_IsWithinTheDepthBound()
    {
        var (own, parents) = Chain(64);

        Assert.Equal("d1(x:a)", Compose([1], own, parents).Dps);
    }

    [Fact]
    public void CellSignature_ChainOf65Ordinates_ExceedsTheDepthBound_AndThrowsNamingTheOrdinate()
    {
        var (own, parents) = Chain(65);

        var ex = Assert.Throws<InvalidOperationException>(() => Compose([1], own, parents));
        Assert.Contains("OrdinateID=1", ex.Message, StringComparison.Ordinal);
        Assert.Contains("64", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CellSignature_ParentCycle_ThrowsInsteadOfLooping()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Compose(
            [1],
            new() { [1] = [Closed(10, "d1", 100, "x:a")] },
            new() { [1] = 2, [2] = 1 }));
        Assert.Contains("OrdinateID=1", ex.Message, StringComparison.Ordinal);
    }

    // ---- ComposeCellSignature: default members ----

    [Fact]
    public void CellSignature_DefaultMemberOfANonMetricDimension_IsDiscarded()
    {
        var (dps, dms) = Compose([1],
            new() { [1] = [Closed(10, "d1", 100, "x:dflt", isDefault: true), Closed(11, "d2", 200, "x:q")] });

        Assert.Equal("d2(x:q)", dps);
        Assert.Equal("d2(x:q)", dms);
    }

    [Fact]
    public void CellSignature_NearestPairIsADefault_ItIsDroppedAndTheFartherPairDoesNotReappear()
    {
        // Docs, in order: the nearest pair wins, THEN default members are dropped. A child that
        // resets d1 to its default therefore removes d1 from the signature entirely.
        var (dps, _) = Compose([1],
            new() { [1] = [Closed(10, "d1", 100, "x:dflt", isDefault: true)], [2] = [Closed(10, "d1", 101, "x:b")] },
            new() { [1] = 2, [2] = null });

        Assert.Null(dps);
    }

    [Fact]
    public void CellSignature_NearestPairIsNotDefault_FartherDefaultIsIrrelevant()
    {
        var (dps, _) = Compose([1],
            new() { [1] = [Closed(10, "d1", 100, "x:a")], [2] = [Closed(10, "d1", 101, "x:dflt", isDefault: true)] },
            new() { [1] = 2, [2] = null });

        Assert.Equal("d1(x:a)", dps);
    }

    [Fact]
    public void CellSignature_DefaultMetric_IsKept()
    {
        // Docs: the metric pair is always kept (metric members are often flagged as defaults).
        var (dps, dms) = Compose([1],
            new() { [1] = [Metric("ei1", isDefault: true), Closed(10, "d1", 100, "x:dflt", isDefault: true)] });

        Assert.Equal("MET(ei1)", dps);
        Assert.Equal("MET(ei1)", dms);
    }

    // ---- ComposeCellSignature: ordering ----

    [Fact]
    public void CellSignature_MetricComesFirst_EvenWhenAnotherDimensionCodeSortsBefore()
    {
        // "AAA" and "BAS" sort before "MET" under any ordinal comparison; the metric is still first.
        var (dps, dms) = Compose([1],
            new() { [1] = [Closed(10, "BAS", 100, "x:a"), Closed(11, "AAA", 101, "x:b"), Metric("ei1")] });

        Assert.Equal("MET(ei1)|AAA(x:b)|BAS(x:a)", dps);
        Assert.Equal("MET(ei1)|AAA(x:b)|BAS(x:a)", dms);
    }

    [Fact]
    public void CellSignature_NonMetricPairs_AreSortedByDimensionCodeOrdinally_NotByCulture()
    {
        // Ordinal: 'Z'(90) < '_'(95) < 'a'(97). A culture-aware comparison puts "aa" before "Zd"
        // (and ignores or reorders the underscore), so this input separates the two.
        var (dps, _) = Compose([1],
            new() { [1] = [Closed(10, "aa", 100, "x:1"), Closed(11, "Zd", 101, "x:2"), Closed(12, "_u", 102, "x:3")] });

        Assert.Equal("Zd(x:2)|_u(x:3)|aa(x:1)", dps);
    }

    [Fact]
    public void CellSignature_OrderIsIndependentOfTheOrderOfTheOrdinatesOfTheCell()
    {
        var own = new Dictionary<int, Pair[]>
        {
            [1] = [Closed(11, "d2", 200, "x:q")],
            [2] = [Closed(10, "d1", 100, "x:a")],
            [3] = [Metric("ei1")],
        };

        Assert.Equal("MET(ei1)|d1(x:a)|d2(x:q)", Compose([1, 2, 3], own).Dps);
        Assert.Equal("MET(ei1)|d1(x:a)|d2(x:q)", Compose([3, 2, 1], own).Dps);
    }

    [Fact]
    public void CellSignature_PairsAreJoinedWithAPipe_AndNothingElse()
    {
        var (dps, _) = Compose([1],
            new() { [1] = [Closed(10, "d1", 100, "x:a"), Closed(11, "d2", 101, "x:b")] });

        Assert.Equal("d1(x:a)|d2(x:b)", dps);
    }

    // ---- ComposeCellSignature: DPS (codes) versus DMS (IDs) and NULL results ----

    [Fact]
    public void CellSignature_OpenRestricted_DpsUsesHierarchyCode_DmsUsesHierarchyId()
    {
        var (dps, dms) = Compose([1],
            new() { [1] = [Metric("ei1"), Open(10, "d1", (31, "BT3"))] });

        Assert.Equal("MET(ei1)|d1(*[BT3])", dps);
        Assert.Equal("MET(ei1)|d1(*[31])", dms);
    }

    [Fact]
    public void CellSignature_OpenUnrestricted_IsAsteriskInBothForms()
    {
        var (dps, dms) = Compose([1], new() { [1] = [Open(10, "d1")] });

        Assert.Equal("d1(*)", dps);
        Assert.Equal("d1(*)", dms);
    }

    [Fact]
    public void CellSignature_MetricRestriction_NeverProducesABracket()
    {
        var (dps, dms) = Compose([1], new() { [1] = [Metric("ei1", restriction: (3, "HC"))] });

        Assert.Equal("MET(ei1)", dps);
        Assert.Equal("MET(ei1)", dms);
    }

    [Fact]
    public void CellSignature_NoOrdinates_BothFormsAreNull_NotEmptyStrings()
    {
        var (dps, dms) = Compose([], new Dictionary<int, Pair[]>());

        Assert.Null(dps);
        Assert.Null(dms);
    }

    [Fact]
    public void CellSignature_OrdinatesWithoutCategorisations_BothFormsAreNull()
    {
        var (dps, dms) = Compose([1, 2], new Dictionary<int, Pair[]>(), new() { [1] = 2, [2] = null });

        Assert.Null(dps);
        Assert.Null(dms);
    }

    [Fact]
    public void CellSignature_OnlyDefaultNonMetricPairs_BothFormsAreNull()
    {
        var (dps, dms) = Compose([1], new() { [1] = [Closed(10, "d1", 100, "x:dflt", isDefault: true)] });

        Assert.Null(dps);
        Assert.Null(dms);
    }

    [Fact]
    public void CellSignature_WithoutMetric_HasNoMetPart()
    {
        var (dps, _) = Compose([1], new() { [1] = [Closed(10, "d1", 100, "x:a")] });

        Assert.Equal("d1(x:a)", dps);
    }
}
