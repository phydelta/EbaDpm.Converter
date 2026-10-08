using EbaDpm.Converter.Tests.Dictionary;

namespace EbaDpm.Converter.Tests.AxesAndCells;

/// <summary>
/// STRUCTURAL invariants of <c>mAxis</c>/<c>mTableAxis</c>/<c>mAxisOrdinate</c> checked WITHOUT
/// looking at any reference: they are the ones that protect the taxonomies that have no
/// counterpart in any reference database.
///
/// Critical acceptance layer.
/// </summary>
[Collection("AxesAndCells")]
[Trait("Tier", "RealData")]
public sealed class AxisStructuralIntegrityTests
{
    private readonly AxisAndCellFixture _fixture;

    public AxisStructuralIntegrityTests(AxisAndCellFixture fixture)
    {
        _fixture = fixture;
    }

    [DataFact]
    public void Sanity_AxesAndOrdinatesWereActuallyLoaded()
    {
        Assert.True(_fixture.AxisAndCellResult.AxisRows > 0, "AxisAndCellLoader loaded no mAxis rows.");
        Assert.True(_fixture.AxisAndCellResult.AxisOrdinateRows > 0, "AxisAndCellLoader loaded no mAxisOrdinate rows.");
        Assert.True(_fixture.AxisAndCellResult.TableCellRows > 0, "AxisAndCellLoader loaded no mTableCell rows.");
    }

    // ------------------------------------------------------------------
    // 1) mAxis is 1:1 with mTableAxis
    // ------------------------------------------------------------------

    [DataFact]
    public void Axis_Count_EqualsTableAxis_Count_AndNoAxisIdIsUsedTwice()
    {
        var axisCount = QueryHelpers.Scalar(_fixture.GeneratedConnection, "SELECT COUNT(*) FROM \"mAxis\"");
        var tableAxisCount = QueryHelpers.Scalar(_fixture.GeneratedConnection, "SELECT COUNT(*) FROM \"mTableAxis\"");
        Assert.True(axisCount > 0);
        Assert.Equal(axisCount, tableAxisCount);

        var duplicated = QueryHelpers.Scalar(
            _fixture.GeneratedConnection,
            "SELECT COUNT(*) FROM (SELECT \"AxisID\" FROM \"mTableAxis\" GROUP BY \"AxisID\" HAVING COUNT(*) > 1)");
        Assert.Equal(0, duplicated);
    }

    // ------------------------------------------------------------------
    // 2) Valid orientation; no axis without ordinates
    // ------------------------------------------------------------------

    [DataFact]
    public void AxisOrientation_IsAlwaysXYorZ()
    {
        var bad = QueryHelpers.Scalar(
            _fixture.GeneratedConnection,
            "SELECT COUNT(*) FROM \"mAxis\" WHERE \"AxisOrientation\" NOT IN ('X','Y','Z')");
        Assert.Equal(0, bad);
    }

    [DataFact]
    public void NoAxisIsEmptyOfOrdinates()
    {
        var empty = QueryHelpers.Scalar(
            _fixture.GeneratedConnection,
            """
            SELECT COUNT(*) FROM "mAxis" a
            WHERE NOT EXISTS (SELECT 1 FROM "mAxisOrdinate" o WHERE o."AxisID" = a."AxisID")
            """);
        Assert.Equal(0, empty);
    }

    // ------------------------------------------------------------------
    // 3) Every open axis has exactly 1 ordinate
    // ------------------------------------------------------------------

    [DataFact]
    public void EveryOpenAxis_HasExactlyOneOrdinate()
    {
        var bad = QueryHelpers.Scalar(
            _fixture.GeneratedConnection,
            """
            SELECT COUNT(*) FROM (
                SELECT a."AxisID", COUNT(o."OrdinateID") AS n
                FROM "mAxis" a JOIN "mAxisOrdinate" o ON o."AxisID" = a."AxisID"
                WHERE a."IsOpenAxis" = 1
                GROUP BY a."AxisID"
                HAVING n <> 1
            )
            """);
        Assert.Equal(0, bad);
    }

    // ------------------------------------------------------------------
    // 4)-6) OrdinateCode: unique per axis, unpadded, never the sentinel 999
    // ------------------------------------------------------------------

    [DataFact]
    public void OrdinateCode_IsUniquePerAxis()
    {
        var duplicated = QueryHelpers.Scalar(
            _fixture.GeneratedConnection,
            """
            SELECT COUNT(*) FROM (
                SELECT "AxisID", "OrdinateCode" FROM "mAxisOrdinate"
                GROUP BY "AxisID", "OrdinateCode" HAVING COUNT(*) > 1
            )
            """);
        Assert.Equal(0, duplicated);
    }

    [DataFact]
    public void OrdinateCode_NeverHasLeadingOrTrailingWhitespace()
    {
        var padded = QueryHelpers.Scalar(
            _fixture.GeneratedConnection,
            "SELECT COUNT(*) FROM \"mAxisOrdinate\" WHERE \"OrdinateCode\" <> TRIM(\"OrdinateCode\")");
        Assert.Equal(0, padded);
    }

    [DataFact]
    public void OrdinateCode_NeverIsTheOpenSentinel_999()
    {
        var count = QueryHelpers.Scalar(
            _fixture.GeneratedConnection,
            "SELECT COUNT(*) FROM \"mAxisOrdinate\" WHERE TRIM(\"OrdinateCode\") = '999'");
        Assert.Equal(0, count);
    }

    // ------------------------------------------------------------------
    // 7) mTableAxis.Order: permutation of 1..n PER TableID. Group by TableID, never by
    //    TableCode (several tables share a code across taxonomies).
    // ------------------------------------------------------------------

    [DataFact]
    public void TableAxis_Order_IsPermutationOfOneToN_PerTableId()
    {
        var rows = QueryHelpers.Rows(
            _fixture.GeneratedConnection,
            "SELECT \"TableID\", \"Order\" FROM \"mTableAxis\" ORDER BY \"TableID\", \"Order\"",
            2);
        Assert.True(rows.Count > 0);

        var failures = new List<string>();
        foreach (var group in rows.Select(r => (TableId: int.Parse(r[0]!), Order: int.Parse(r[1]!))).GroupBy(r => r.TableId))
        {
            var orders = group.Select(g => g.Order).OrderBy(o => o).ToList();
            var expected = Enumerable.Range(1, orders.Count).ToList();
            if (!orders.SequenceEqual(expected))
            {
                failures.Add($"TableID={group.Key}: observed [{string.Join(",", orders)}]");
            }
        }

        Assert.True(failures.Count == 0, $"{failures.Count} tables with mTableAxis.Order not a permutation of 1..n:\n" + string.Join("\n", failures.Take(20)));
    }

    // ------------------------------------------------------------------
    // 8) mAxisOrdinate.[Order]: permutation of 1..n PER AxisID, on CLOSED axes (the single
    //    ordinate of an open axis carries Order = 0 and is not part of the 1..n permutation).
    // ------------------------------------------------------------------

    [DataFact]
    public void AxisOrdinate_Order_IsPermutationOfOneToN_PerClosedAxis()
    {
        var rows = QueryHelpers.Rows(
            _fixture.GeneratedConnection,
            """
            SELECT o."AxisID", o."Order" FROM "mAxisOrdinate" o
            JOIN "mAxis" a ON a."AxisID" = o."AxisID"
            WHERE a."IsOpenAxis" = 0
            ORDER BY o."AxisID", o."Order"
            """,
            2);
        Assert.True(rows.Count > 0);

        var failures = new List<string>();
        foreach (var group in rows.Select(r => (AxisId: int.Parse(r[0]!), Order: int.Parse(r[1]!))).GroupBy(r => r.AxisId))
        {
            var orders = group.Select(g => g.Order).OrderBy(o => o).ToList();
            var expected = Enumerable.Range(1, orders.Count).ToList();
            if (!orders.SequenceEqual(expected))
            {
                failures.Add($"AxisID={group.Key}: observed [{string.Join(",", orders)}]");
            }
        }

        Assert.True(failures.Count == 0, $"{failures.Count} closed axes with Order not a permutation of 1..n:\n" + string.Join("\n", failures.Take(20)));
    }

    [DataFact]
    public void AxisOrdinate_Order_And_Level_AreZero_OnTheSingleOrdinateOfAnOpenAxis()
    {
        // Explicit exception to 1-based numbering. It only applies to the single ordinate of an open axis.
        var bad = QueryHelpers.Scalar(
            _fixture.GeneratedConnection,
            """
            SELECT COUNT(*) FROM "mAxisOrdinate" o
            JOIN "mAxis" a ON a."AxisID" = o."AxisID"
            WHERE a."IsOpenAxis" = 1 AND (o."Order" <> 0 OR o."Level" <> 0)
            """);
        Assert.Equal(0, bad);

        var openOrdinateCount = QueryHelpers.Scalar(
            _fixture.GeneratedConnection,
            "SELECT COUNT(*) FROM \"mAxisOrdinate\" o JOIN \"mAxis\" a ON a.\"AxisID\" = o.\"AxisID\" WHERE a.\"IsOpenAxis\" = 1");
        Assert.True(openOrdinateCount > 0, "There is no open axis in the converted universe: this assertion would be vacuous.");
    }

    // ------------------------------------------------------------------
    // 9) The tree: ParentOrdinateID is never dangling, never points to another axis, no cycles.
    // ------------------------------------------------------------------

    [DataFact]
    public void ParentOrdinateId_NeverDangling()
    {
        var dangling = QueryHelpers.Scalar(
            _fixture.GeneratedConnection,
            """
            SELECT COUNT(*) FROM "mAxisOrdinate" o
            WHERE o."ParentOrdinateID" IS NOT NULL
              AND NOT EXISTS (SELECT 1 FROM "mAxisOrdinate" p WHERE p."OrdinateID" = o."ParentOrdinateID")
            """);
        Assert.Equal(0, dangling);
    }

    [DataFact]
    public void ParentOrdinateId_NeverPointsToAnOrdinateOfAnotherAxis()
    {
        var crossAxis = QueryHelpers.Scalar(
            _fixture.GeneratedConnection,
            """
            SELECT COUNT(*) FROM "mAxisOrdinate" o
            JOIN "mAxisOrdinate" p ON p."OrdinateID" = o."ParentOrdinateID"
            WHERE p."AxisID" <> o."AxisID"
            """);
        Assert.Equal(0, crossAxis);
    }

    [DataFact]
    public void OrdinateTree_HasNoCycles()
    {
        var rows = QueryHelpers.Rows(
            _fixture.GeneratedConnection,
            "SELECT \"OrdinateID\", \"ParentOrdinateID\" FROM \"mAxisOrdinate\"",
            2);
        Assert.True(rows.Count > 0);

        var parentOf = rows.ToDictionary(
            r => int.Parse(r[0]!),
            r => r[1] is null ? (int?)null : int.Parse(r[1]!));

        var failures = new List<int>();
        foreach (var start in parentOf.Keys)
        {
            var visited = new HashSet<int>();
            var current = (int?)start;
            while (current is { } id)
            {
                if (!visited.Add(id))
                {
                    failures.Add(start);
                    break;
                }

                current = parentOf.TryGetValue(id, out var next) ? next : null;
            }
        }

        Assert.True(failures.Count == 0, $"{failures.Count} ordinates in a cycle. Examples: {string.Join(",", failures.Take(10))}");
    }

    // ------------------------------------------------------------------
    // 10)-11) Level == Level(parent)+1; roots have Level=1 ONLY on closed axes (the single
    //    ordinate of an open axis is excluded, covered above). Roots = ParentOrdinateID IS NULL,
    //    NEVER 0.
    // ------------------------------------------------------------------

    [DataFact]
    public void Level_OfChild_IsParentLevelPlusOne_OnClosedAxes()
    {
        var bad = QueryHelpers.Scalar(
            _fixture.GeneratedConnection,
            """
            SELECT COUNT(*) FROM "mAxisOrdinate" o
            JOIN "mAxisOrdinate" p ON p."OrdinateID" = o."ParentOrdinateID"
            JOIN "mAxis" a ON a."AxisID" = o."AxisID"
            WHERE a."IsOpenAxis" = 0 AND o."Level" <> p."Level" + 1
            """);
        Assert.Equal(0, bad);
    }

    [DataFact]
    public void Roots_OfClosedAxes_HaveLevelOne()
    {
        var bad = QueryHelpers.Scalar(
            _fixture.GeneratedConnection,
            """
            SELECT COUNT(*) FROM "mAxisOrdinate" o
            JOIN "mAxis" a ON a."AxisID" = o."AxisID"
            WHERE a."IsOpenAxis" = 0 AND o."ParentOrdinateID" IS NULL AND o."Level" <> 1
            """);
        Assert.Equal(0, bad);

        var rootCount = QueryHelpers.Scalar(
            _fixture.GeneratedConnection,
            """
            SELECT COUNT(*) FROM "mAxisOrdinate" o JOIN "mAxis" a ON a."AxisID" = o."AxisID"
            WHERE a."IsOpenAxis" = 0 AND o."ParentOrdinateID" IS NULL
            """);
        Assert.True(rootCount > 0);
    }

    [DataFact]
    public void Roots_AreMarkedWithNull_NeverWithZero()
    {
        var zeroParents = QueryHelpers.Scalar(
            _fixture.GeneratedConnection,
            "SELECT COUNT(*) FROM \"mAxisOrdinate\" WHERE \"ParentOrdinateID\" = 0");
        Assert.Equal(0, zeroParents);
    }

    // ------------------------------------------------------------------
    // 12) The single ordinate of an open axis is a root.
    // ------------------------------------------------------------------

    [DataFact]
    public void TheSingleOrdinateOfAnOpenAxis_IsAlwaysARoot()
    {
        var bad = QueryHelpers.Scalar(
            _fixture.GeneratedConnection,
            """
            SELECT COUNT(*) FROM "mAxisOrdinate" o
            JOIN "mAxis" a ON a."AxisID" = o."AxisID"
            WHERE a."IsOpenAxis" = 1 AND o."ParentOrdinateID" IS NOT NULL
            """);
        Assert.Equal(0, bad);
    }

    // ------------------------------------------------------------------
    // 13) [Order] rebuilt in pre-order from (ParentOrdinateID, Order) reproduces the stored
    //     Order, for every closed axis.
    // ------------------------------------------------------------------

    [DataFact]
    public void Order_OfClosedAxes_IsExactlyThePreorderTraversal()
    {
        var rows = QueryHelpers.Rows(
            _fixture.GeneratedConnection,
            """
            SELECT o."AxisID", o."OrdinateID", o."ParentOrdinateID", o."Order" FROM "mAxisOrdinate" o
            JOIN "mAxis" a ON a."AxisID" = o."AxisID"
            WHERE a."IsOpenAxis" = 0
            """,
            4);
        Assert.True(rows.Count > 0);

        var byAxis = rows
            .Select(r => (AxisId: int.Parse(r[0]!), OrdinateId: int.Parse(r[1]!), ParentId: r[2] is null ? (int?)null : int.Parse(r[2]!), Order: int.Parse(r[3]!)))
            .GroupBy(r => r.AxisId);

        var failures = new List<string>();
        foreach (var axisGroup in byAxis)
        {
            var nodes = axisGroup.ToList();
            var childrenByParent = nodes
                .Where(n => n.ParentId.HasValue)
                .GroupBy(n => n.ParentId!.Value)
                .ToDictionary(g => g.Key, g => g.OrderBy(n => n.Order).Select(n => n.OrdinateId).ToList());
            var byOrdinateId = nodes.ToDictionary(n => n.OrdinateId);

            var expectedOrder = new List<int>();
            void Visit(int ordinateId)
            {
                expectedOrder.Add(ordinateId);
                if (childrenByParent.TryGetValue(ordinateId, out var kids))
                {
                    foreach (var kid in kids)
                    {
                        Visit(kid);
                    }
                }
            }

            foreach (var root in nodes.Where(n => n.ParentId is null).OrderBy(n => n.Order))
            {
                Visit(root.OrdinateId);
            }

            var actualOrder = nodes.OrderBy(n => n.Order).Select(n => n.OrdinateId).ToList();
            if (!expectedOrder.SequenceEqual(actualOrder))
            {
                failures.Add($"AxisID={axisGroup.Key}: rebuilt pre-order does not match the stored Order");
            }
        }

        Assert.True(failures.Count == 0, $"{failures.Count} axes whose Order does not reproduce the pre-order:\n" + string.Join("\n", failures.Take(20)));
    }

    // ------------------------------------------------------------------
    // 16)-17) mAxis.AxisCode / OptionalKey: internal consistency with IsOpenAxis, without a
    //    reference; this is the formula the converter itself promises to always apply.
    // ------------------------------------------------------------------

    [DataFact]
    public void AxisCode_IsNullOnClosedAxes_AndEqualsTheOrdinateCode_OnOpenAxes()
    {
        var closedWithCode = QueryHelpers.Scalar(
            _fixture.GeneratedConnection,
            "SELECT COUNT(*) FROM \"mAxis\" WHERE \"IsOpenAxis\" = 0 AND \"AxisCode\" IS NOT NULL");
        Assert.Equal(0, closedWithCode);

        var mismatched = QueryHelpers.Scalar(
            _fixture.GeneratedConnection,
            """
            SELECT COUNT(*) FROM "mAxis" a
            JOIN "mAxisOrdinate" o ON o."AxisID" = a."AxisID"
            WHERE a."IsOpenAxis" = 1 AND (a."AxisCode" IS NULL OR a."AxisCode" <> o."OrdinateCode")
            """);
        Assert.Equal(0, mismatched);
    }

    [DataFact]
    public void OptionalKey_AlwaysEqualsIsOpenAxis()
    {
        var mismatched = QueryHelpers.Scalar(
            _fixture.GeneratedConnection,
            "SELECT COUNT(*) FROM \"mAxis\" WHERE \"OptionalKey\" IS NOT \"IsOpenAxis\"");
        Assert.Equal(0, mismatched);
    }

    // ------------------------------------------------------------------
    // 18) TypeOfKey and RelatedDimensionTableId: constant NULL.
    // ------------------------------------------------------------------

    [DataFact]
    public void TypeOfKey_IsAlwaysNull()
    {
        var populated = QueryHelpers.Scalar(_fixture.GeneratedConnection, "SELECT COUNT(*) FROM \"mAxisOrdinate\" WHERE \"TypeOfKey\" IS NOT NULL");
        Assert.Equal(0, populated);
    }

    [DataFact]
    public void RelatedDimensionTableId_IsAlwaysNull()
    {
        var populated = QueryHelpers.Scalar(_fixture.GeneratedConnection, "SELECT COUNT(*) FROM \"mAxisOrdinate\" WHERE \"RelatedDimensionTableId\" IS NOT NULL");
        Assert.Equal(0, populated);
    }

    [DataFact]
    public void IsDisplayBeforeChildren_IsAlwaysZero_NeverNull()
    {
        var total = QueryHelpers.Scalar(_fixture.GeneratedConnection, "SELECT COUNT(*) FROM \"mAxisOrdinate\"");
        var notZero = QueryHelpers.Scalar(_fixture.GeneratedConnection, "SELECT COUNT(*) FROM \"mAxisOrdinate\" WHERE \"IsDisplayBeforeChildren\" IS NOT 0");
        Assert.True(total > 0);
        Assert.Equal(0, notZero);
    }

    // ------------------------------------------------------------------
    // 19)-20) IsRowKey = 1 if and only if the axis is open (internal consistency); no ordinate
    //    of an X axis carries IsRowKey = 1 (the key-column transposition has already been done).
    // ------------------------------------------------------------------

    [DataFact]
    public void IsRowKey_AlwaysEqualsAxisIsOpen()
    {
        var mismatched = QueryHelpers.Scalar(
            _fixture.GeneratedConnection,
            """
            SELECT COUNT(*) FROM "mAxisOrdinate" o
            JOIN "mAxis" a ON a."AxisID" = o."AxisID"
            WHERE o."IsRowKey" IS NOT a."IsOpenAxis"
            """);
        Assert.Equal(0, mismatched);
    }

    [DataFact]
    public void NoOrdinateOfAnXAxis_HasIsRowKeySet()
    {
        var bad = QueryHelpers.Scalar(
            _fixture.GeneratedConnection,
            """
            SELECT COUNT(*) FROM "mAxisOrdinate" o
            JOIN "mAxis" a ON a."AxisID" = o."AxisID"
            WHERE a."AxisOrientation" = 'X' AND o."IsRowKey" = 1
            """);
        Assert.Equal(0, bad);
    }

    // ------------------------------------------------------------------
    // 24) No axis with orientation 'O' reaches the target.
    // ------------------------------------------------------------------

    [DataFact]
    public void NoAxis_HasOrientation_O()
    {
        var count = QueryHelpers.Scalar(_fixture.GeneratedConnection, "SELECT COUNT(*) FROM \"mAxis\" WHERE \"AxisOrientation\" = 'O'");
        Assert.Equal(0, count);
    }
}
