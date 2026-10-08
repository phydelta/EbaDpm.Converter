using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Core.Validation.Checks;

/// <summary>
/// Axes and ordinates (<c>mAxis</c>, <c>mTableAxis</c>, <c>mAxisOrdinate</c>).
/// Plane A, without looking at any reference.
/// </summary>
public static class AxisChecks
{
    public static IEnumerable<CheckResult> Run(SqliteConnection c)
    {
        yield return SqlHelpers.CountsEqual(
            c, "A-AXE-01.1", ValidationLayer.Critical, "mAxis", "|mAxis| == |mTableAxis|",
            "SELECT COUNT(*) FROM \"mAxis\"", "SELECT COUNT(*) FROM \"mTableAxis\"");
        yield return SqlHelpers.CountCheck(
            c, "A-AXE-01.2", ValidationLayer.Critical, "mTableAxis", "No AxisID used twice in mTableAxis",
            "SELECT COUNT(*) FROM \"mTableAxis\"",
            "SELECT COUNT(*) FROM (SELECT \"AxisID\" FROM \"mTableAxis\" GROUP BY \"AxisID\" HAVING COUNT(*) > 1)");

        yield return SqlHelpers.CountCheck(
            c, "A-AXE-02.1", ValidationLayer.Critical, "mAxis", "AxisOrientation ∈ {X,Y,Z}",
            "SELECT COUNT(*) FROM \"mAxis\"", "SELECT COUNT(*) FROM \"mAxis\" WHERE \"AxisOrientation\" NOT IN ('X','Y','Z')");
        yield return SqlHelpers.CountCheck(
            c, "A-AXE-02.2", ValidationLayer.Critical, "mAxis", "No axis with orientation 'O'",
            "SELECT COUNT(*) FROM \"mAxis\"", "SELECT COUNT(*) FROM \"mAxis\" WHERE \"AxisOrientation\" = 'O'");

        yield return SqlHelpers.CountCheck(
            c, "A-AXE-03.1", ValidationLayer.Critical, "mAxis", "No axis without ordinates",
            "SELECT COUNT(*) FROM \"mAxis\"",
            "SELECT COUNT(*) FROM \"mAxis\" a WHERE NOT EXISTS (SELECT 1 FROM \"mAxisOrdinate\" o WHERE o.\"AxisID\" = a.\"AxisID\")");
        yield return SqlHelpers.CountCheck(
            c, "A-AXE-03.2", ValidationLayer.Critical, "mAxis", "Every open axis has exactly 1 ordinate",
            "SELECT COUNT(*) FROM \"mAxis\" WHERE \"IsOpenAxis\" = 1",
            """
            SELECT COUNT(*) FROM (
                SELECT a."AxisID", COUNT(o."OrdinateID") AS n FROM "mAxis" a
                JOIN "mAxisOrdinate" o ON o."AxisID" = a."AxisID"
                WHERE a."IsOpenAxis" = 1 GROUP BY a."AxisID" HAVING n <> 1)
            """);

        yield return TableHasExactlyOneClosedXAxis_AtMostOneClosedYAxis(c);

        yield return OrderIsPermutation(c, "A-AXE-05", "mTableAxis", "TableID", null);
        yield return OrderIsPermutation(c, "A-AXE-06", "mAxisOrdinate", "AxisID", "JOIN \"mAxis\" a ON a.\"AxisID\" = o.\"AxisID\" WHERE a.\"IsOpenAxis\" = 0");

        yield return PreorderReproducesStoredOrder(c);

        // "ParentOrdinateID not dangling and on the same axis" is already covered by A-AXE-08.1/.2.
        yield return SqlHelpers.CountCheck(
            c, "A-AXE-08.1", ValidationLayer.Critical, "mAxisOrdinate", "ParentOrdinateID never dangling",
            "SELECT COUNT(*) FROM \"mAxisOrdinate\"",
            "SELECT COUNT(*) FROM \"mAxisOrdinate\" o WHERE o.\"ParentOrdinateID\" IS NOT NULL AND NOT EXISTS (SELECT 1 FROM \"mAxisOrdinate\" p WHERE p.\"OrdinateID\" = o.\"ParentOrdinateID\")");
        yield return SqlHelpers.CountCheck(
            c, "A-AXE-08.2", ValidationLayer.Critical, "mAxisOrdinate", "ParentOrdinateID never points to an ordinate of another axis",
            "SELECT COUNT(*) FROM \"mAxisOrdinate\" WHERE \"ParentOrdinateID\" IS NOT NULL",
            "SELECT COUNT(*) FROM \"mAxisOrdinate\" o JOIN \"mAxisOrdinate\" p ON p.\"OrdinateID\" = o.\"ParentOrdinateID\" WHERE p.\"AxisID\" <> o.\"AxisID\"");
        yield return SqlHelpers.CountCheck(
            c, "A-AXE-08.3", ValidationLayer.Critical, "mAxisOrdinate", "Roots have ParentOrdinateID NULL, never 0",
            "SELECT COUNT(*) FROM \"mAxisOrdinate\"", "SELECT COUNT(*) FROM \"mAxisOrdinate\" WHERE \"ParentOrdinateID\" = 0");
        yield return OrdinateTreeHasNoCycles(c);

        yield return SqlHelpers.CountCheck(
            c, "A-AXE-09.1", ValidationLayer.Critical, "mAxisOrdinate", "Level == Level(parent)+1 on closed axes",
            "SELECT COUNT(*) FROM \"mAxisOrdinate\" o JOIN \"mAxis\" a ON a.\"AxisID\"=o.\"AxisID\" WHERE a.\"IsOpenAxis\"=0 AND o.\"ParentOrdinateID\" IS NOT NULL",
            """
            SELECT COUNT(*) FROM "mAxisOrdinate" o
            JOIN "mAxisOrdinate" p ON p."OrdinateID" = o."ParentOrdinateID"
            JOIN "mAxis" a ON a."AxisID" = o."AxisID"
            WHERE a."IsOpenAxis" = 0 AND o."Level" <> p."Level" + 1
            """);
        yield return SqlHelpers.CountCheck(
            c, "A-AXE-09.2", ValidationLayer.Critical, "mAxisOrdinate", "Closed-axis roots have Level=1",
            "SELECT COUNT(*) FROM \"mAxisOrdinate\" o JOIN \"mAxis\" a ON a.\"AxisID\"=o.\"AxisID\" WHERE a.\"IsOpenAxis\"=0 AND o.\"ParentOrdinateID\" IS NULL",
            "SELECT COUNT(*) FROM \"mAxisOrdinate\" o JOIN \"mAxis\" a ON a.\"AxisID\"=o.\"AxisID\" WHERE a.\"IsOpenAxis\"=0 AND o.\"ParentOrdinateID\" IS NULL AND o.\"Level\" <> 1");

        yield return SqlHelpers.CountCheck(
            c, "A-AXE-10", ValidationLayer.Critical, "mAxisOrdinate", "The single ordinate of an open axis is a root, Level=0, Order=0",
            "SELECT COUNT(*) FROM \"mAxisOrdinate\" o JOIN \"mAxis\" a ON a.\"AxisID\"=o.\"AxisID\" WHERE a.\"IsOpenAxis\"=1",
            "SELECT COUNT(*) FROM \"mAxisOrdinate\" o JOIN \"mAxis\" a ON a.\"AxisID\"=o.\"AxisID\" WHERE a.\"IsOpenAxis\"=1 AND (o.\"ParentOrdinateID\" IS NOT NULL OR o.\"Level\" <> 0 OR o.\"Order\" <> 0)");

        yield return SqlHelpers.CountCheck(
            c, "A-AXE-11.1", ValidationLayer.Critical, "mAxis", "AxisCode NULL on every closed axis",
            "SELECT COUNT(*) FROM \"mAxis\" WHERE \"IsOpenAxis\"=0", "SELECT COUNT(*) FROM \"mAxis\" WHERE \"IsOpenAxis\"=0 AND \"AxisCode\" IS NOT NULL");
        yield return SqlHelpers.CountCheck(
            c, "A-AXE-11.2", ValidationLayer.Critical, "mAxis", "AxisCode == OrdinateCode of the single ordinate, on every open axis",
            "SELECT COUNT(*) FROM \"mAxis\" WHERE \"IsOpenAxis\"=1",
            """
            SELECT COUNT(*) FROM "mAxis" a JOIN "mAxisOrdinate" o ON o."AxisID" = a."AxisID"
            WHERE a."IsOpenAxis" = 1 AND (a."AxisCode" IS NULL OR a."AxisCode" <> o."OrdinateCode")
            """);

        yield return SqlHelpers.CountCheck(
            c, "A-AXE-12", ValidationLayer.Critical, "mAxis", "OptionalKey == IsOpenAxis",
            "SELECT COUNT(*) FROM \"mAxis\"", "SELECT COUNT(*) FROM \"mAxis\" WHERE \"OptionalKey\" IS NOT \"IsOpenAxis\"");

        yield return SqlHelpers.CountCheck(
            c, "A-AXE-13", ValidationLayer.Critical, "mAxisOrdinate", "TypeOfKey NULL in 100% of rows",
            "SELECT COUNT(*) FROM \"mAxisOrdinate\"", "SELECT COUNT(*) FROM \"mAxisOrdinate\" WHERE \"TypeOfKey\" IS NOT NULL");

        yield return SqlHelpers.CountCheck(
            c, "A-AXE-14", ValidationLayer.Critical, "mAxisOrdinate", "IsDisplayBeforeChildren constant 0, never NULL",
            "SELECT COUNT(*) FROM \"mAxisOrdinate\"", "SELECT COUNT(*) FROM \"mAxisOrdinate\" WHERE \"IsDisplayBeforeChildren\" IS NOT 0");

        yield return SqlHelpers.CountCheck(
            c, "A-AXE-15.1", ValidationLayer.Critical, "mAxisOrdinate", "IsRowKey == IsOpenAxis of its axis",
            "SELECT COUNT(*) FROM \"mAxisOrdinate\"",
            "SELECT COUNT(*) FROM \"mAxisOrdinate\" o JOIN \"mAxis\" a ON a.\"AxisID\"=o.\"AxisID\" WHERE o.\"IsRowKey\" IS NOT a.\"IsOpenAxis\"");
        yield return SqlHelpers.CountCheck(
            c, "A-AXE-15.2", ValidationLayer.Critical, "mAxisOrdinate", "No ordinate of an X axis with IsRowKey=1",
            "SELECT COUNT(*) FROM \"mAxisOrdinate\" o JOIN \"mAxis\" a ON a.\"AxisID\"=o.\"AxisID\" WHERE a.\"AxisOrientation\"='X'",
            "SELECT COUNT(*) FROM \"mAxisOrdinate\" o JOIN \"mAxis\" a ON a.\"AxisID\"=o.\"AxisID\" WHERE a.\"AxisOrientation\"='X' AND o.\"IsRowKey\"=1");

        yield return SqlHelpers.CountCheck(
            c, "A-AXE-16.1", ValidationLayer.Critical, "mAxisOrdinate", "OrdinateCode without leading or trailing spaces",
            "SELECT COUNT(*) FROM \"mAxisOrdinate\"", "SELECT COUNT(*) FROM \"mAxisOrdinate\" WHERE \"OrdinateCode\" <> TRIM(\"OrdinateCode\")");
        yield return SqlHelpers.CountCheck(
            c, "A-AXE-16.2", ValidationLayer.Critical, "mAxisOrdinate", "No non-abstract ordinate without a code",
            "SELECT COUNT(*) FROM \"mAxisOrdinate\" WHERE \"IsAbstractHeader\" = 0",
            "SELECT COUNT(*) FROM \"mAxisOrdinate\" WHERE \"IsAbstractHeader\" = 0 AND (\"OrdinateCode\" IS NULL OR TRIM(\"OrdinateCode\") = '')");

        yield return AbstractHeadersRetainPositions(c);
    }

    private static CheckResult TableHasExactlyOneClosedXAxis_AtMostOneClosedYAxis(SqliteConnection c)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var rows = SqlHelpers.Rows(
            c,
            """
            SELECT ta."TableID",
                   SUM(CASE WHEN a."AxisOrientation"='X' AND a."IsOpenAxis"=0 THEN 1 ELSE 0 END) AS x_closed,
                   SUM(CASE WHEN a."AxisOrientation"='Y' AND a."IsOpenAxis"=0 THEN 1 ELSE 0 END) AS y_closed
            FROM "mTableAxis" ta JOIN "mAxis" a ON a."AxisID" = ta."AxisID"
            GROUP BY ta."TableID"
            """,
            3);
        sw.Stop();

        var failures = rows
            .Where(r => int.Parse(r[1]!) != 1 || int.Parse(r[2]!) > 1)
            .Select(r => new CheckSample($"TableID={r[0]}: closed-X={r[1]} closed-Y={r[2]}"))
            .ToList();

        return CheckResult.FromViolationCount(
            "A-AXE-04", ValidationPlane.A, ValidationLayer.Critical, "mTableAxis",
            "Every table has exactly 1 closed X axis and at most 1 closed Y axis",
            rows.Count, failures.Count, sw.ElapsedMilliseconds, failures);
    }

    private static CheckResult OrderIsPermutation(SqliteConnection c, string id, string table, string groupColumn, string? extraJoinWhere)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var from = table == "mTableAxis"
            ? $"SELECT \"{groupColumn}\" AS g, \"Order\" AS o FROM \"mTableAxis\""
            : $"SELECT o.\"{groupColumn}\" AS g, o.\"Order\" AS o FROM \"mAxisOrdinate\" o {extraJoinWhere}";
        var rows = SqlHelpers.Rows(c, from, 2);
        sw.Stop();

        var failures = new List<CheckSample>();
        foreach (var group in rows.Select(r => (Group: r[0]!, Order: int.Parse(r[1]!))).GroupBy(r => r.Group))
        {
            var orders = group.Select(g => g.Order).OrderBy(o => o).ToList();
            var expected = Enumerable.Range(1, orders.Count).ToList();
            if (!orders.SequenceEqual(expected))
            {
                failures.Add(new CheckSample($"{groupColumn}={group.Key}: [{string.Join(",", orders)}]"));
            }
        }

        var statement = table == "mTableAxis"
            ? "mTableAxis.Order is a dense permutation of 1..n per TableID"
            : "mAxisOrdinate.Order is a dense permutation of 1..n per CLOSED axis";
        return CheckResult.FromViolationCount(id, ValidationPlane.A, ValidationLayer.Critical, table, statement, rows.Count, failures.Count, sw.ElapsedMilliseconds, failures);
    }

    private static CheckResult PreorderReproducesStoredOrder(SqliteConnection c)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var rows = SqlHelpers.Rows(
            c,
            """
            SELECT o."AxisID", o."OrdinateID", o."ParentOrdinateID", o."Order" FROM "mAxisOrdinate" o
            JOIN "mAxis" a ON a."AxisID" = o."AxisID" WHERE a."IsOpenAxis" = 0
            """,
            4);

        var byAxis = rows
            .Select(r => (AxisId: r[0]!, OrdinateId: int.Parse(r[1]!), ParentId: r[2] is null ? (int?)null : int.Parse(r[2]!), Order: int.Parse(r[3]!)))
            .GroupBy(r => r.AxisId);

        var failures = new List<CheckSample>();
        foreach (var axisGroup in byAxis)
        {
            var nodes = axisGroup.ToList();
            var childrenByParent = nodes.Where(n => n.ParentId.HasValue).GroupBy(n => n.ParentId!.Value)
                .ToDictionary(g => g.Key, g => g.OrderBy(n => n.Order).Select(n => n.OrdinateId).ToList());

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
                failures.Add(new CheckSample($"AxisID={axisGroup.Key}"));
            }
        }
        sw.Stop();

        return CheckResult.FromViolationCount(
            "A-AXE-07", ValidationPlane.A, ValidationLayer.Critical, "mAxisOrdinate",
            "Order rebuilt in PREORDER from (ParentOrdinateID, Order) reproduces the stored Order",
            byAxis.Count(), failures.Count, sw.ElapsedMilliseconds, failures);
    }

    private static CheckResult OrdinateTreeHasNoCycles(SqliteConnection c)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var rows = SqlHelpers.Rows(c, "SELECT \"OrdinateID\", \"ParentOrdinateID\" FROM \"mAxisOrdinate\"", 2);
        var parentOf = rows.ToDictionary(r => int.Parse(r[0]!), r => r[1] is null ? (int?)null : int.Parse(r[1]!));

        var failures = new List<CheckSample>();
        foreach (var start in parentOf.Keys)
        {
            var visited = new HashSet<int>();
            var current = (int?)start;
            while (current is { } id)
            {
                if (!visited.Add(id))
                {
                    failures.Add(new CheckSample($"OrdinateID={start}"));
                    break;
                }

                current = parentOf.TryGetValue(id, out var next) ? next : null;
            }
        }
        sw.Stop();

        return CheckResult.FromViolationCount("A-AXE-08.4", ValidationPlane.A, ValidationLayer.Critical, "mAxisOrdinate", "Ordinate tree without cycles", rows.Count, failures.Count, sw.ElapsedMilliseconds, failures);
    }

    private static CheckResult AbstractHeadersRetainPositions(SqliteConnection c)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var count = SqlHelpers.Scalar(
            c, "SELECT COUNT(*) FROM \"mCellPosition\" cp JOIN \"mAxisOrdinate\" o ON o.\"OrdinateID\"=cp.\"OrdinateID\" WHERE o.\"IsAbstractHeader\"=1");
        sw.Stop();
        var failed = count > 0 ? 0 : 1;
        var samples = failed == 0 ? [] : new List<CheckSample> { new("0 positions on abstract headers: was a 'leaves only' filter applied by mistake?") };
        return CheckResult.FromViolationCount(
            "A-AXE-17", ValidationPlane.A, ValidationLayer.Critical, "mCellPosition",
            "Abstract headers DO have cell positions (no 'leaves only' filtering)",
            1, failed, sw.ElapsedMilliseconds, samples);
    }
}
