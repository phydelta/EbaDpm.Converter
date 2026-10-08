using EbaDpm.Converter.Tests.Dictionary;

namespace EbaDpm.Converter.Tests.All;

/// <summary>
/// Cell invariants A-CEL-01/02/03/05, over <c>--all</c>.
///
/// <see cref="BusinessCode_ReconstructedFromCellPosition_MatchesTheEmittedOne_Exactly"/> is, together
/// with <c>mAxisOrdinate.OrdinateCode</c>, THE most sensitive invariant: an error in the cell
/// coordinates silently shifts ALL the data of a table. A one-off investigation measured it at
/// 100.000000 % over 904,204 cells of <c>--all</c>, but only once -- never as a test run on every
/// pass of the suite. This file closes that gap.
/// </summary>
[Collection("All")]
[Trait("Tier", "RealData")]
public sealed class CellInvariantTests
{
    private readonly AllFixture _fixture;

    public CellInvariantTests(AllFixture fixture)
    {
        _fixture = fixture;
    }

    private static readonly Dictionary<string, int> OrientationRank = new(StringComparer.Ordinal)
    {
        ["Y"] = 0,
        ["X"] = 1,
        ["Z"] = 2,
    };

    private sealed record ClosedPosition(string Orientation, int AxisOrder, string OrdinateCode);

    /// <summary>
    /// A-CEL-01: the reconstructed <c>BusinessCode</c> is <c>{</c> + <c>TableCode</c> + <c>,</c> + the
    /// <c>OrdinateCode</c> (TRIM) of each position of the cell on a CLOSED axis, in <c>Y, X, Z</c>
    /// order (the order already carried by <c>Access.TableCell.CellCode</c>), + <c>}</c>.
    /// Reimplemented here independently of <c>AxisAndCellLoader.TranslateBusinessCode</c> (which
    /// translates from the Access <c>CellCode</c>, not from <c>mCellPosition</c>): comparing the
    /// two paths is precisely what makes this a real cross-check.
    /// </summary>
    [DataFact]
    public void BusinessCode_ReconstructedFromCellPosition_MatchesTheEmittedOne_Exactly()
    {
        var tableCodeByTableId = QueryHelpers.Rows(_fixture.GeneratedConnection, "SELECT \"TableID\", \"TableCode\" FROM \"mTable\"", 2)
            .ToDictionary(r => int.Parse(r[0]!), r => r[1]!);

        var cellRows = QueryHelpers.Rows(_fixture.GeneratedConnection, "SELECT \"CellID\", \"TableID\", \"BusinessCode\" FROM \"mTableCell\"", 3);

        var positionRows = QueryHelpers.Rows(
            _fixture.GeneratedConnection,
            """
            SELECT cp."CellID", a."AxisOrientation", ta."Order", o."OrdinateCode"
            FROM "mCellPosition" cp
            JOIN "mAxisOrdinate" o ON o."OrdinateID" = cp."OrdinateID"
            JOIN "mAxis" a         ON a."AxisID" = o."AxisID"
            JOIN "mTableAxis" ta   ON ta."AxisID" = a."AxisID"
            WHERE a."IsOpenAxis" = 0
            """,
            4);

        var positionsByCell = new Dictionary<int, List<ClosedPosition>>();
        foreach (var row in positionRows)
        {
            var cellId = int.Parse(row[0]!);
            var position = new ClosedPosition(row[1]!, int.Parse(row[2]!), row[3]!.Trim());

            if (!positionsByCell.TryGetValue(cellId, out var list))
            {
                list = [];
                positionsByCell[cellId] = list;
            }

            list.Add(position);
        }

        var compared = 0;
        var mismatches = new List<string>();

        foreach (var row in cellRows)
        {
            var cellId = int.Parse(row[0]!);
            var tableId = int.Parse(row[1]!);
            var actualBusinessCode = row[2];

            if (!tableCodeByTableId.TryGetValue(tableId, out var tableCode))
            {
                mismatches.Add($"CellID={cellId}: TableID={tableId} has no row in mTable");
                continue;
            }

            var positions = positionsByCell.GetValueOrDefault(cellId, []);
            positions.Sort((a, b) =>
            {
                var rankCompare = OrientationRank[a.Orientation].CompareTo(OrientationRank[b.Orientation]);
                return rankCompare != 0 ? rankCompare : a.AxisOrder.CompareTo(b.AxisOrder);
            });

            var reconstructed = "{" + tableCode + "," + string.Join(",", positions.Select(p => p.OrdinateCode)) + "}";

            compared++;
            if (reconstructed != actualBusinessCode)
            {
                mismatches.Add($"CellID={cellId}: reconstructed='{reconstructed}' vs emitted='{actualBusinessCode}'");
            }
        }

        Assert.True(compared > 900_000, $"Only {compared} cells were compared (about 904,204 expected over --all): the comparison universe is suspiciously small.");
        Assert.True(
            mismatches.Count == 0,
            $"{mismatches.Count}/{compared} BusinessCode values reconstructed from mCellPosition do NOT match the " +
            $"emitted one (A-CEL-01, critical layer - the most sensitive check of the project). Examples:\n" +
            string.Join("\n", mismatches.Take(20)));
    }

    /// <summary>A-CEL-02: number of BusinessCode segments == 1 + number of positions on CLOSED axes.</summary>
    [DataFact]
    public void BusinessCode_SegmentCount_Equals_OneMoreThanClosedPositionCount()
    {
        var closedPositionCountByCell = QueryHelpers.Rows(
            _fixture.GeneratedConnection,
            """
            SELECT c."CellID", COUNT(*) FROM "mTableCell" c
            JOIN "mCellPosition" cp ON cp."CellID" = c."CellID"
            JOIN "mAxisOrdinate" o  ON o."OrdinateID" = cp."OrdinateID"
            JOIN "mAxis" a          ON a."AxisID" = o."AxisID"
            WHERE a."IsOpenAxis" = 0
            GROUP BY c."CellID"
            """,
            2).ToDictionary(r => int.Parse(r[0]!), r => int.Parse(r[1]!));

        var cellRows = QueryHelpers.Rows(_fixture.GeneratedConnection, "SELECT \"CellID\", \"BusinessCode\" FROM \"mTableCell\"", 2);

        var mismatches = new List<string>();
        foreach (var row in cellRows)
        {
            var cellId = int.Parse(row[0]!);
            var businessCode = row[1]!;
            var segmentCount = businessCode.Trim('{', '}').Split(',').Length;
            var expectedSegments = 1 + closedPositionCountByCell.GetValueOrDefault(cellId, 0);

            if (segmentCount != expectedSegments)
            {
                mismatches.Add($"CellID={cellId}: {segmentCount} segments in BusinessCode vs {expectedSegments} expected");
            }
        }

        Assert.True(
            mismatches.Count == 0,
            $"{mismatches.Count} cells whose number of BusinessCode segments differs from 1 + closed positions (A-CEL-02). Examples:\n" +
            string.Join("\n", mismatches.Take(20)));
    }

    /// <summary>A-CEL-03: BusinessCode is never NULL or empty; always shaped {...}; first segment == TableCode of its table.</summary>
    [DataFact]
    public void BusinessCode_IsNeverNullOrEmpty_AlwaysBraced_AndStartsWithItsOwnTableCode()
    {
        var nullOrEmpty = QueryHelpers.Scalar(
            _fixture.GeneratedConnection, "SELECT COUNT(*) FROM \"mTableCell\" WHERE \"BusinessCode\" IS NULL OR \"BusinessCode\" = ''");
        Assert.True(nullOrEmpty == 0, $"{nullOrEmpty} cells with a NULL or empty BusinessCode (A-CEL-03).");

        var notBraced = QueryHelpers.Scalar(
            _fixture.GeneratedConnection,
            "SELECT COUNT(*) FROM \"mTableCell\" WHERE substr(\"BusinessCode\",1,1) <> '{' OR substr(\"BusinessCode\", length(\"BusinessCode\"),1) <> '}'");
        Assert.True(notBraced == 0, $"{notBraced} cells with a BusinessCode not shaped {{...}} (A-CEL-03).");

        // Every cell always has at least one closed segment (A-AXE-04: the closed X axis is
        // MANDATORY on every table, never "at most"), so BusinessCode always contains at least
        // one comma and the first segment can be extracted without special cases.
        var wrongTableSegment = QueryHelpers.Scalar(
            _fixture.GeneratedConnection,
            """
            SELECT COUNT(*) FROM "mTableCell" c
            JOIN "mTable" t ON t."TableID" = c."TableID"
            WHERE instr(c."BusinessCode", ',') = 0
               OR substr(c."BusinessCode", 2, instr(c."BusinessCode", ',') - 2) <> t."TableCode"
            """);
        Assert.True(wrongTableSegment == 0, $"{wrongTableSegment} cells whose first BusinessCode segment does not match the TableCode of their table, or with no comma (A-CEL-03).");
    }

    /// <summary>A-CEL-05: no cell without positions; none with fewer than 2.</summary>
    [DataFact]
    public void NoCell_IsWithoutPositions_OrHasFewerThanTwo()
    {
        var withoutAnyPosition = QueryHelpers.Scalar(
            _fixture.GeneratedConnection,
            """
            SELECT COUNT(*) FROM "mTableCell" c
            WHERE NOT EXISTS (SELECT 1 FROM "mCellPosition" cp WHERE cp."CellID" = c."CellID")
            """);
        Assert.True(withoutAnyPosition == 0, $"{withoutAnyPosition} cells without any position (A-CEL-05).");

        var withFewerThanTwo = QueryHelpers.Scalar(
            _fixture.GeneratedConnection,
            """
            SELECT COUNT(*) FROM (
                SELECT "CellID", COUNT(*) AS Cnt FROM "mCellPosition"
                GROUP BY "CellID" HAVING COUNT(*) < 2
            )
            """);
        Assert.True(withFewerThanTwo == 0, $"{withFewerThanTwo} cells with fewer than 2 positions (A-CEL-05).");
    }
}
