using EbaDpm.Converter.Core.Mapping.Dpm20;
using EbaDpm.Converter.Tests.Dictionary;

namespace EbaDpm.Converter.Tests.Dpm2;

/// <summary>
/// The cross-axis dimension check (docs/mapping-dpm2.md, "Signatures", last paragraph): a cell has
/// one signature, so no dimension (the metric included) may be declared by ordinates of more than
/// one axis; "nearest wins" only arbitrates within ONE axis. Expected values are derived by hand
/// from that paragraph. Unit level: <see cref="Dpm20CellSignatureComposer.FindCrossAxisDimensions"/>;
/// loader level: the structural anomaly message and the unchanged cell rows.
/// </summary>
public sealed class CrossAxisDimensionTests
{
    private const int Met = Dpm20AxisAndCellLoader.MetDimensionId;
    private const int Open = Dpm20AxisAndCellLoader.OpenAxisMemberSentinel;

    private const int YAxis = 10;
    private const int XAxis = 20;
    private const int ZAxis = 30;

    private sealed class Model
    {
        public Dictionary<int, List<(int DimensionId, string DimensionXbrlCode, int MemberId, string? MemberXbrlCode, bool IsDefaultMember, (int HierarchyId, string HierarchyCode)? Restriction)>> Own { get; } = [];

        public Dictionary<int, int?> Parent { get; } = [];

        public Dictionary<int, (int Rank, int AxisId)> Axis { get; } = [];

        public Dictionary<int, string> Code { get; } = [];

        public Model Ordinate(int id, string code, string orientation, int axisId, int? parent = null)
        {
            Code[id] = code;
            Parent[id] = parent;
            Axis[id] = (Dpm20CellSignatureComposer.OrientationRank(orientation), axisId);
            return this;
        }

        public Model Pair(int ordinateId, int dimensionId, string dimensionXbrl, int memberId, string? memberXbrl, bool isDefault = false)
        {
            if (!Own.TryGetValue(ordinateId, out var list))
            {
                Own[ordinateId] = list = [];
            }

            list.Add((dimensionId, dimensionXbrl, memberId, memberXbrl, isDefault, null));
            return this;
        }

        public IReadOnlyList<Dpm20CellSignatureComposer.CrossAxisDimension> Find(params int[] cell)
            => Dpm20CellSignatureComposer.FindCrossAxisDimensions([.. cell], Own, Parent, Axis, Code);
    }

    private static Model YandX()
        => new Model().Ordinate(1, "0010", "Y", YAxis).Ordinate(2, "0030", "X", XAxis);

    private static void AssertContribution(
        Dpm20CellSignatureComposer.CrossAxisDimension found, int axisId, string[] ordinates, string[] members)
    {
        var contribution = found.ByAxisId[axisId];
        Assert.Equal(ordinates, contribution.OrdinateCodes.ToArray());
        Assert.Equal(members, contribution.Members.ToArray());
    }

    [Fact]
    public void SameDimensionOnYAndX_WithDifferentMembers_IsReported()
    {
        var m = YandX().Pair(1, 100, "eba_dA", 1, "eba_a1").Pair(2, 100, "eba_dA", 2, "eba_a2");

        var found = Assert.Single(m.Find(1, 2));

        Assert.Equal("eba_dA", found.DimensionXbrlCode);
        Assert.Equal([YAxis, XAxis], found.ByAxisId.Keys.ToArray());
        AssertContribution(found, YAxis, ["0010"], ["eba_a1"]);
        AssertContribution(found, XAxis, ["0030"], ["eba_a2"]);
    }

    [Fact]
    public void SameDimensionOnYAndX_WithTheSameMember_IsStillReported()
    {
        var m = YandX().Pair(1, 100, "eba_dA", 1, "eba_a1").Pair(2, 100, "eba_dA", 1, "eba_a1");

        var found = Assert.Single(m.Find(1, 2));

        AssertContribution(found, YAxis, ["0010"], ["eba_a1"]);
        AssertContribution(found, XAxis, ["0030"], ["eba_a1"]);
    }

    [Fact]
    public void MetricOnTwoAxes_IsReported()
    {
        var m = YandX().Pair(1, Met, "MET", 7, "eba_met1").Pair(2, Met, "MET", 8, "eba_met2");

        var found = Assert.Single(m.Find(1, 2));

        Assert.Equal("MET", found.DimensionXbrlCode);
        AssertContribution(found, YAxis, ["0010"], ["eba_met1"]);
        AssertContribution(found, XAxis, ["0030"], ["eba_met2"]);
    }

    [Fact]
    public void DefaultMemberOnOneAxis_IsStillReported()
    {
        // The signature would discard the default pair; the structural check runs before that.
        var m = YandX().Pair(1, 100, "eba_dA", 1, "eba_a0", isDefault: true).Pair(2, 100, "eba_dA", 2, "eba_a2");

        var found = Assert.Single(m.Find(1, 2));

        AssertContribution(found, YAxis, ["0010"], ["eba_a0"]);
        AssertContribution(found, XAxis, ["0030"], ["eba_a2"]);
    }

    [Fact]
    public void PairInheritedFromAnAncestorOnY_AgainstAnOwnPairOnX_IsReported_ListingTheAncestorOrdinate()
    {
        // Y leaf 0021 (id 3) inherits dA from its parent 0010 (id 1); X 0030 declares dA itself.
        var m = YandX().Ordinate(3, "0021", "Y", YAxis, parent: 1)
            .Pair(1, 100, "eba_dA", 1, "eba_a1")
            .Pair(2, 100, "eba_dA", 2, "eba_a2");

        var found = Assert.Single(m.Find(3, 2));

        AssertContribution(found, YAxis, ["0010"], ["eba_a1"]);
        AssertContribution(found, XAxis, ["0030"], ["eba_a2"]);
    }

    [Fact]
    public void OverrideWithinOneAxis_ParentAndChildOnY_IsNotReported()
    {
        var m = YandX().Ordinate(3, "0021", "Y", YAxis, parent: 1)
            .Pair(1, 100, "eba_dA", 1, "eba_a1")
            .Pair(3, 100, "eba_dA", 2, "eba_a2");

        Assert.Empty(m.Find(3, 2));
        Assert.Empty(m.Find(3));
    }

    [Fact]
    public void OverrideWithinOneAxis_IsNotReported_ButTheSameDimensionOnTheOtherAxisIs()
    {
        // Y: parent and child both declare dA (a legitimate override). X also declares dA: reported,
        // and the Y side lists BOTH declaring ordinates, the X side its own.
        var m = YandX().Ordinate(3, "0021", "Y", YAxis, parent: 1)
            .Pair(1, 100, "eba_dA", 1, "eba_a1")
            .Pair(3, 100, "eba_dA", 2, "eba_a2")
            .Pair(2, 100, "eba_dA", 3, "eba_a3");

        var found = Assert.Single(m.Find(3, 2));

        AssertContribution(found, YAxis, ["0010", "0021"], ["eba_a1", "eba_a2"]);
        AssertContribution(found, XAxis, ["0030"], ["eba_a3"]);
    }

    [Fact]
    public void NoSharedDimension_YieldsNothing()
    {
        var m = YandX().Pair(1, 100, "eba_dA", 1, "eba_a1").Pair(2, 101, "eba_dB", 2, "eba_b2");

        Assert.Empty(m.Find(1, 2));
    }

    [Fact]
    public void OpenAxisSentinel_IsShownAsAnAsterisk()
    {
        var m = YandX().Pair(1, 100, "eba_dA", Open, null).Pair(2, 100, "eba_dA", 2, "eba_a2");

        var found = Assert.Single(m.Find(1, 2));

        AssertContribution(found, YAxis, ["0010"], ["*"]);
        AssertContribution(found, XAxis, ["0030"], ["eba_a2"]);
    }

    [Fact]
    public void ThreeAxes_TwoDimensions_AreReportedOrderedByDimensionXbrlCode_WithOnlyTheAxesThatDeclareThem()
    {
        var m = YandX().Ordinate(4, "0040", "Z", ZAxis)
            .Pair(1, 101, "eba_dB", 1, "eba_b1")
            .Pair(2, 101, "eba_dB", 2, "eba_b2")
            .Pair(2, 100, "eba_dA", 3, "eba_a3")
            .Pair(4, 100, "eba_dA", 4, "eba_a4")
            .Pair(4, 102, "eba_dC", 5, "eba_c5"); // dC is on Z only: not reported

        var found = m.Find(1, 2, 4);

        Assert.Equal(["eba_dA", "eba_dB"], found.Select(f => f.DimensionXbrlCode).ToArray());
        Assert.Equal([XAxis, ZAxis], found[0].ByAxisId.Keys.ToArray());
        Assert.Equal([YAxis, XAxis], found[1].ByAxisId.Keys.ToArray());
    }

    // ---- loader level ----

    private const string Marker = "is declared by ordinates of more than one axis";

    private static List<string> CrossAxisMessages(AxisCellWorld.Outcome outcome)
        => outcome.Result.StructuralAnomalies.Where(a => a.Contains(Marker)).ToList();

    /// <summary>Metric column X1 declares dA=a2; rows Y1, Y2 declare dA=a1 (the conflict). Cells (Y1,X1), (Y2,X1).</summary>
    private static AxisCellWorld ConflictWorld(bool y1Excluded = false, bool y2Excluded = false)
    {
        var w = new AxisCellWorld();
        var dA = w.AddDimension("dA", ["a1", "a2"]);
        var metric = w.AddMetric("met1");
        var x1 = w.AddHeader("X", "X1", propertyId: metric, contextId: w.AddContext((dA, "a2")));
        var y1 = w.AddHeader("Y", "Y1", contextId: w.AddContext((dA, "a1")));
        var y2 = w.AddHeader("Y", "Y2", contextId: w.AddContext((dA, "a1")));
        w.AddCell(x1, y1, isExcluded: y1Excluded);
        w.AddCell(x1, y2, isExcluded: y2Excluded);
        return w;
    }

    [Fact]
    public void PlantedConflict_YieldsExactlyOneMessagePerTableAndDimension_WithCountExampleOrdinatesAndMembers()
    {
        using var outcome = ConflictWorld().Run();

        var message = Assert.Single(CrossAxisMessages(outcome));
        Assert.Equal(
            "Table 'T_01': dimension 'eba_dA' is declared by ordinates of more than one axis "
            + "(a cell signature must not repeat a dimension): 2 cell(s), e.g. BusinessCode '{T_01,Y1,X1}'; "
            + "Y axis ordinates [Y1, Y2] members [eba_a1]; X axis ordinates [X1] members [eba_a2].",
            message);
    }

    [Fact]
    public void PlantedConflict_DoesNotChangeTheCellRows_TieGoesToTheFirstOrdinateOfTheCell()
    {
        using var outcome = ConflictWorld().Run();

        // Both declarations are at depth 0 (a tie): docs say the first ordinate of the cell wins.
        // The cell lists its ordinates in axis order (mTableAxis."Order"), NOT in BusinessCode
        // order (Y,X,Z), so the winner is the declaration of the axis with the lowest Order.
        Assert.Equal("X", QueryHelpers.Rows(
            outcome.Connection,
            "SELECT a.\"AxisOrientation\" FROM \"mTableAxis\" t JOIN \"mAxis\" a ON a.\"AxisID\" = t.\"AxisID\" ORDER BY t.\"Order\" LIMIT 1",
            1).Single()[0]);
        const string expected = "MET(eba_met1)|eba_dA(eba_a2)";
        foreach (var cell in new[] { "{T_01,Y1,X1}", "{T_01,Y2,X1}" }.Select(businessCode => outcome.Cell(businessCode)))
        {
            Assert.Equal(0, cell.IsShaded);
            Assert.Equal(expected, cell.Dps);
            Assert.Equal(expected, cell.DatapointSignature);
        }

        Assert.Equal(2, outcome.Cells().Count);
    }

    [Fact]
    public void ShadedCellsAlone_DoNotTriggerTheCheck()
    {
        using var outcome = ConflictWorld(y1Excluded: true, y2Excluded: true).Run();

        Assert.All(outcome.Cells(), c => Assert.Equal(1, c.IsShaded));
        Assert.Empty(CrossAxisMessages(outcome));
    }

    [Fact]
    public void ShadedCellIsNotCounted_OnlyTheLiveCellIs()
    {
        using var outcome = ConflictWorld(y2Excluded: true).Run();

        var message = Assert.Single(CrossAxisMessages(outcome));
        Assert.Contains(": 1 cell(s), e.g. BusinessCode '{T_01,Y1,X1}';", message);
    }

    [Fact]
    public void CleanWorld_DimensionsDistributedAcrossTheAxes_YieldsNoMessage()
    {
        var w = new AxisCellWorld();
        var dA = w.AddDimension("dA", ["a1"]);
        var dB = w.AddDimension("dB", ["b1"]);
        var metric = w.AddMetric("met1");
        var x1 = w.AddHeader("X", "X1", propertyId: metric, contextId: w.AddContext((dB, "b1")));
        var y1 = w.AddHeader("Y", "Y1", contextId: w.AddContext((dA, "a1")));
        w.AddCell(x1, y1);
        using var outcome = w.Run();

        Assert.Equal("MET(eba_met1)|eba_dA(eba_a1)|eba_dB(eba_b1)", Assert.Single(outcome.Cells()).Dps);
        Assert.Empty(CrossAxisMessages(outcome));
    }

    [Fact]
    public void OverrideWithinOneAxis_AtLoaderLevel_YieldsNoMessage()
    {
        var w = new AxisCellWorld();
        var dA = w.AddDimension("dA", ["a1", "a2"]);
        var metric = w.AddMetric("met1");
        var x1 = w.AddHeader("X", "X1", propertyId: metric);
        var parent = w.AddHeader("Y", "Y1", isAbstract: true, contextId: w.AddContext((dA, "a1")));
        var child = w.AddHeader("Y", "Y2", parentHeaderId: parent, contextId: w.AddContext((dA, "a2")));
        w.AddCell(x1, child);
        using var outcome = w.Run();

        // The abstract parent Y1 gives a shaded cell; the live one is the child's.
        Assert.Equal("MET(eba_met1)|eba_dA(eba_a2)", outcome.Cell("{T_01,Y2,X1}").Dps);
        Assert.Equal(1, outcome.Cell("{T_01,Y1,X1}").IsShaded);
        Assert.Empty(CrossAxisMessages(outcome));
    }
}
