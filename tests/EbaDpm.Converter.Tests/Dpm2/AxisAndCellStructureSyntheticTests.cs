using EbaDpm.Converter.Tests.Dictionary;

namespace EbaDpm.Converter.Tests.Dpm2;

/// <summary>
/// Synthetic coverage (issue #14) of the STRUCTURE the DPM 2.0 axis and cell loader emits:
/// <c>BusinessCode</c> composition, <c>mCellPosition</c>, trimmed codes, pruning of abstract
/// headers, the Cartesian product and <c>IsShaded</c>. Every expected value is written from
/// <c>docs/mapping-dpm2.md</c> ("Axes, ordinates and cells") and the loader's documented comments,
/// never copied from running the code. Runs <c>Dpm20AxisAndCellLoader.Load</c> over in-memory rows
/// (<see cref="AxisCellWorld"/>); no data files needed.
/// </summary>
public sealed class AxisAndCellStructureSyntheticTests
{
    private static int OpenKey(AxisCellWorld w, string direction, string code)
    {
        var dimension = w.AddDimension("dk" + code, []);
        return w.AddHeader(direction, code, isKey: true, propertyId: dimension.PropertyId);
    }

    private static List<string?> BusinessCodesOf(Action<AxisCellWorld> build)
    {
        var w = new AxisCellWorld();
        build(w);
        using var outcome = w.Run();
        Assert.Equal(0, outcome.Result.UnresolvedOpenAxisDimensions);
        return outcome.Cells().Select(c => c.BusinessCode).ToList();
    }

    // ---- GAP-1: BusinessCode tie-break between open axes of the same orientation ----

    [Theory]
    [InlineData("0030", "0020", "{T_01,0020,0030,0010}")]
    [InlineData("0020", "0030", "{T_01,0030,0020,0010}")]
    public void BusinessCode_SeveralOpenYAxes_GoByDescendingAxisId(string firstKeyCode, string secondKeyCode, string expected)
    {
        // Docs: "When several open Y axes coexist, their codes go by descending axis ID". The key
        // headers are declared (and so get their AxisID, ascending by HeaderID) in the order
        // first, second: the SECOND one therefore comes first. Both code permutations are
        // exercised so that neither an ascending/descending sort by code nor by AxisID passes both.
        var codes = BusinessCodesOf(w =>
        {
            var column = w.AddHeader("X", "0010");
            OpenKey(w, "X", firstKeyCode);
            OpenKey(w, "X", secondKeyCode);
            w.AddCell(column);
        });

        Assert.Equal([expected], codes);
    }

    // ---- GAP-2: orientation order Y, X, Z ----

    [Fact]
    public void BusinessCode_ClosedAxes_AreOrderedYThenXThenZ_NotByDeclarationNorAxisId()
    {
        var codes = BusinessCodesOf(w =>
        {
            w.AddHeader("Z", "Z1");
            w.AddHeader("X", "X1");
            w.AddHeader("Y", "Y1");
        });

        Assert.Equal(["{T_01,Y1,X1,Z1}"], codes);
    }

    [Fact]
    public void BusinessCode_OpenYAxisFromKeyX_GoesFirst_ThenClosedX_ThenClosedZ()
    {
        var codes = BusinessCodesOf(w =>
        {
            w.AddHeader("X", "X1");
            w.AddHeader("Z", "Z1");
            OpenKey(w, "X", "K1"); // key X -> open Y axis (the highest AxisID of the table)
        });

        Assert.Equal(["{T_01,K1,X1,Z1}"], codes);
    }

    [Fact]
    public void BusinessCode_OpenZAxisFromKeyZ_GoesLast_AfterClosedY_AndClosedX()
    {
        var codes = BusinessCodesOf(w =>
        {
            w.AddHeader("X", "X1");
            w.AddHeader("Y", "Y1");
            OpenKey(w, "Z", "KZ"); // key Z -> open Z axis
        });

        Assert.Equal(["{T_01,Y1,X1,KZ}"], codes);
    }

    [Fact]
    public void BusinessCode_OpenYAndOpenZ_AroundAClosedX()
    {
        var codes = BusinessCodesOf(w =>
        {
            OpenKey(w, "Z", "KZ");
            w.AddHeader("X", "X1");
            OpenKey(w, "X", "KY");
        });

        Assert.Equal(["{T_01,KY,X1,KZ}"], codes);
    }

    // ---- GAP-3: the open axis is a position of every cell ----

    [Fact]
    public void OpenAxis_IsAPositionAndPartOfTheBusinessCode_OfEveryCell_WithoutMultiplyingThem()
    {
        var w = new AxisCellWorld();
        w.AddHeader("X", "0010");
        w.AddHeader("X", "0011");
        OpenKey(w, "X", "0020");
        using var outcome = w.Run();

        // Docs: open axes do not multiply cells; their single ordinate is a position of every
        // cell; mCellPosition has one row per axis of the table (closed X + open Y = 2 per cell).
        var cells = outcome.Cells();
        Assert.Equal(["{T_01,0020,0010}", "{T_01,0020,0011}"], cells.Select(c => c.BusinessCode).Order(StringComparer.Ordinal));
        Assert.Equal(4, outcome.Count("SELECT COUNT(*) FROM \"mCellPosition\""));
        Assert.Equal(4, outcome.Result.CellPositionRows);
        Assert.Equal(["0010", "0020"], outcome.PositionCodes(outcome.Cell("{T_01,0020,0010}").CellId));
        Assert.Equal(["0011", "0020"], outcome.PositionCodes(outcome.Cell("{T_01,0020,0011}").CellId));
    }

    // ---- GAP-9: codes and labels are trimmed ----

    [Fact]
    public void ClosedAndKeyHeaderCodes_AreTrimmed_InOrdinateCode_AxisCode_AndBusinessCode()
    {
        var w = new AxisCellWorld();
        w.AddHeader("X", " 0010 ", label: "  Column label ");
        var dimension = w.AddDimension("dOpen", []);
        w.AddHeader("X", " 0020 ", label: "\tOpen key ", isKey: true, propertyId: dimension.PropertyId);
        using var outcome = w.Run();

        Assert.Equal(["{T_01,0020,0010}"], outcome.Cells().Select(c => c.BusinessCode));

        var ordinates = QueryHelpers.Rows(
            outcome.Connection,
            "SELECT \"OrdinateCode\", \"OrdinateLabel\" FROM \"mAxisOrdinate\" ORDER BY \"OrdinateCode\"", 2);
        Assert.Equal(["0010|Column label", "0020|Open key"], ordinates.Select(r => $"{r[0]}|{r[1]}"));

        var openAxis = QueryHelpers.Rows(
            outcome.Connection,
            "SELECT \"AxisCode\", \"AxisLabel\" FROM \"mAxis\" WHERE \"IsOpenAxis\" = 1", 2);
        Assert.Equal(["0020|Open key"], openAxis.Select(r => $"{r[0]}|{r[1]}"));
    }

    // ---- GAP-10: pruning of abstract headers after the transposition ----

    [Fact]
    public void AbstractHeader_WhoseOnlyChildIsAKeyHeader_IsNotEmitted_AndLevelAndOrderAreRecomputed()
    {
        var w = new AxisCellWorld();
        var abstractHeader = w.AddHeader("X", "A000", isAbstract: true);
        var dimension = w.AddDimension("dOpen", []);
        w.AddHeader("X", "K000", isKey: true, parentHeaderId: abstractHeader, propertyId: dimension.PropertyId);
        w.AddHeader("X", "0010");
        using var outcome = w.Run();

        // Docs: pruning happens AFTER the transposition, so the abstract header is left empty and
        // dropped; Level/Order are counted over the pruned tree (the leaf is 1,1 although its
        // source order is 3).
        Assert.Equal(["0010(1,1,-)"], outcome.ClosedOrdinates());
        Assert.Equal(["{T_01,K000,0010}"], outcome.Cells().Select(c => c.BusinessCode));
    }

    [Fact]
    public void AbstractHeader_WithASurvivingChild_IsEmitted_AndTheChildPointsAtIt()
    {
        var w = new AxisCellWorld();
        var abstractHeader = w.AddHeader("X", "A000", isAbstract: true);
        w.AddHeader("X", "0010", parentHeaderId: abstractHeader);
        var dimension = w.AddDimension("dOpen", []);
        w.AddHeader("X", "K000", isKey: true, parentHeaderId: abstractHeader, propertyId: dimension.PropertyId);
        using var outcome = w.Run();

        Assert.Equal(["A000(1,1,-)", "0010(2,2,A000)"], outcome.ClosedOrdinates());
        // The Cartesian product includes the abstract ordinate.
        Assert.Equal(
            ["{T_01,K000,0010}", "{T_01,K000,A000}"],
            outcome.Cells().Select(c => c.BusinessCode).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void TwoLevelAbstractChain_EndingInAKeyHeader_IsPrunedBottomUp()
    {
        var w = new AxisCellWorld();
        var outer = w.AddHeader("X", "A001", isAbstract: true);
        var inner = w.AddHeader("X", "A002", isAbstract: true, parentHeaderId: outer);
        var dimension = w.AddDimension("dOpen", []);
        w.AddHeader("X", "K000", isKey: true, parentHeaderId: inner, propertyId: dimension.PropertyId);
        w.AddHeader("X", "0010");
        using var outcome = w.Run();

        // Pruning the inner abstract header empties the outer one, which must go too.
        Assert.Equal(["0010(1,1,-)"], outcome.ClosedOrdinates());
    }

    [Fact]
    public void TwoLevelAbstractChain_EndingInALeaf_IsKeptWithTheParentChain()
    {
        var w = new AxisCellWorld();
        var outer = w.AddHeader("X", "A001", isAbstract: true);
        var inner = w.AddHeader("X", "A002", isAbstract: true, parentHeaderId: outer);
        w.AddHeader("X", "0010", parentHeaderId: inner);
        using var outcome = w.Run();

        Assert.Equal(["A001(1,1,-)", "A002(2,2,A001)", "0010(3,3,A002)"], outcome.ClosedOrdinates());
    }

    [Fact]
    public void PrunedAbstractSibling_DoesNotRemoveTheSurvivingSiblingsAbstractParent()
    {
        var w = new AxisCellWorld();
        var outer = w.AddHeader("X", "A001", isAbstract: true);
        var emptyInner = w.AddHeader("X", "A002", isAbstract: true, parentHeaderId: outer);
        var dimension = w.AddDimension("dOpen", []);
        w.AddHeader("X", "K000", isKey: true, parentHeaderId: emptyInner, propertyId: dimension.PropertyId);
        w.AddHeader("X", "0010", parentHeaderId: outer);
        using var outcome = w.Run();

        // A002 goes (only a key child); A001 stays because 0010 survives, and 0010 hangs from it.
        Assert.Equal(["A001(1,1,-)", "0010(2,2,A001)"], outcome.ClosedOrdinates());
    }

    // ---- GAP-11: Cartesian product axis by axis ----

    [Theory]
    [InlineData(2, 2, 2)]
    [InlineData(3, 2, 4)]
    public void ClosedAxes_AreMultiplied_AxisByAxis_OneCellAndOnePositionPerAxis(int columns, int rows, int sheets)
    {
        var w = new AxisCellWorld();
        // Header insertion order is preserved: all X, then all Y, then all Z, ascending.
        AddHeaders(w, "X", "x", columns);
        AddHeaders(w, "Y", "y", rows);
        AddHeaders(w, "Z", "z", sheets);
        using var outcome = w.Run();

        // Expected, from the docs: one cell per (column, row, sheet); BusinessCode Y,X,Z; one
        // position per axis.
        var expected = (from i in Enumerable.Range(1, columns)
                        from j in Enumerable.Range(1, rows)
                        from k in Enumerable.Range(1, sheets)
                        select (Code: $"{{T_01,y{j},x{i},z{k}}}", Positions: new List<string> { $"x{i}", $"y{j}", $"z{k}" }))
            .ToDictionary(e => e.Code, e => e.Positions);

        var cells = outcome.Cells();
        var total = columns * rows * sheets;
        Assert.Equal(total, cells.Count);
        Assert.Equal(total, cells.Select(c => c.BusinessCode).Distinct().Count());
        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), cells.Select(c => c.BusinessCode!).Order(StringComparer.Ordinal));
        Assert.Equal(3 * total, outcome.Count("SELECT COUNT(*) FROM \"mCellPosition\""));
        foreach (var cell in cells)
        {
            Assert.Equal(expected[cell.BusinessCode!], outcome.PositionCodes(cell.CellId));
        }
    }

    private static void AddHeaders(AxisCellWorld world, string axis, string prefix, int count)
    {
        foreach (var n in Enumerable.Range(1, count))
        {
            world.AddHeader(axis, $"{prefix}{n}");
        }
    }

    // ---- GAP-12: IsShaded and the nullability of the signatures ----

    [Fact]
    public void IsShaded_MissingCell_AndExcludedCell_AreShadedWithNullSignatures_LiveCellIsNot()
    {
        var w = new AxisCellWorld();
        var metric = w.AddMetric("met1");
        var live = w.AddHeader("X", "0010", propertyId: metric);
        var excluded = w.AddHeader("X", "0020", propertyId: metric);
        w.AddHeader("X", "0030", propertyId: metric); // no Cell row in the source at all
        w.AddCell(column: live);
        w.AddCell(column: excluded, isExcluded: true);
        using var outcome = w.Run();

        var cells = outcome.Cells();
        Assert.Equal(3, cells.Count);
        Assert.All(cells, c => Assert.False(string.IsNullOrEmpty(c.BusinessCode)));

        var liveCell = outcome.Cell("{T_01,0010}");
        Assert.Equal(0, liveCell.IsShaded);
        Assert.Equal("MET(eba_met1)", liveCell.Dps);
        Assert.Equal("MET(eba_met1)", liveCell.DatapointSignature);

        var excludedCell = outcome.Cell("{T_01,0020}");
        Assert.Equal(1, excludedCell.IsShaded);
        Assert.Null(excludedCell.Dps);
        Assert.Null(excludedCell.DatapointSignature);

        var missingCell = outcome.Cell("{T_01,0030}");
        Assert.Equal(1, missingCell.IsShaded);
        Assert.Null(missingCell.Dps);
        Assert.Null(missingCell.DatapointSignature);
    }
}
