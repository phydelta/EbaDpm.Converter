using EbaDpm.Converter.Tests.Dictionary;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Tests.Dpm2;

/// <summary>
/// The invariant that motivated the choice of how DPM 2.0 signatures are built: the variable
/// context is projected onto the ordinates (instead of taking <c>DPS</c> directly from
/// <c>VariableVersion</c>) BECAUSE, with equal <c>DPS</c> (100.00% normalized), that variant keeps an
/// internal invariant that the alternative broke:
/// <b><c>mTableCell.DPS</c> IS the closure of our own <c>mOrdinateCategorisation</c></b>.
///
/// This test recomposes that closure FROM SCRATCH, reading ONLY what was written to the generated
/// SQLite (it reuses no in-memory state of the loader): for each cell, it climbs through
/// <c>ParentOrdinateID</c> from each of its <c>mCellPosition</c> rows (CLOSED axes only -- an
/// open-axis ordinate has no <c>ParentOrdinateID</c>), applying the signature rule literally -- the
/// nearest one wins per dimension, the default member is discarded except in <c>MET</c>, <c>MET</c>
/// goes first and the rest are ordered ordinally by dimension XBRL code -- and compares the result,
/// string by string, against <c>mTableCell.DPS</c>.
///
/// It is LITERALLY the same check as
/// <see cref="AxesAndCells.SignatureStructuralTests.CellDps_IsExactlyTheInheritedCompositionOfOrdinateCategorisationDps"/>
/// does for DPM 1.0, applied here to the DPM 2.0 source: both sources end in the SAME signature
/// algorithm, so the check has to be the same, only over the complete <c>--all</c> conversion of
/// <see cref="Dpm20SkeletonFixture"/> instead of the eight-taxonomy subset of
/// <see cref="AxesAndCells.AxisAndCellFixture"/>.
///
/// This test does NOT chase the COUNT of <c>mOrdinateCategorisation</c> rows (215,808 versus the
/// 71,148 of the reference, and that is correct) -- it compares the CLOSURE those rows produce, not
/// how many there are.
/// </summary>
[Collection("Dpm2Skeleton")]
[Trait("Tier", "RealData")]
public sealed class Dpm20SignatureClosureInvariantTests(Dpm20SkeletonFixture fixture)
{
    private const int MetDimensionId = 9999;

    private sealed record CategorisationEntry(int DimensionId, string Dps, string DimensionXbrlCode, bool IsDefaultMember, bool IsMetric);

    private static Dictionary<int, List<CategorisationEntry>> BuildOwnCategorisationsByOrdinateId(SqliteConnection connection)
    {
        var rows = QueryHelpers.Rows(
            connection,
            """
            SELECT oc."OrdinateID", oc."DimensionID", oc."DPS", dim."DimensionXBRLCode", mem."IsDefaultMember"
            FROM "mOrdinateCategorisation" oc
            JOIN "mDimension" dim ON dim."DimensionID" = oc."DimensionID"
            JOIN "mMember" mem    ON mem."MemberID"    = oc."MemberID"
            """,
            5);

        var result = new Dictionary<int, List<CategorisationEntry>>();
        foreach (var row in rows)
        {
            var ordinateId = int.Parse(row[0]!);
            var dimensionId = int.Parse(row[1]!);
            var dps = row[2]!;
            var dimensionXbrlCode = row[3] ?? "?";
            var isDefaultMember = row[4] == "1";
            var isMetric = dimensionId == MetDimensionId;

            if (!result.TryGetValue(ordinateId, out var list))
            {
                list = [];
                result[ordinateId] = list;
            }

            list.Add(new CategorisationEntry(dimensionId, dps, dimensionXbrlCode, isDefaultMember, isMetric));
        }

        return result;
    }

    /// <summary>
    /// <c>OrdinateID -&gt; ParentOrdinateID</c> ONLY for CLOSED-axis ordinates -- as in production
    /// (<c>Dpm20AxisAndCellLoader</c>), an OPEN-axis ordinate has no entry, so the walk treats it as
    /// a root with no ancestry.
    /// </summary>
    private static Dictionary<int, int?> BuildClosedParentByOrdinateId(SqliteConnection connection)
    {
        var rows = QueryHelpers.Rows(
            connection,
            """
            SELECT o."OrdinateID", o."ParentOrdinateID"
            FROM "mAxisOrdinate" o
            JOIN "mAxis" a ON a."AxisID" = o."AxisID"
            WHERE a."IsOpenAxis" = 0
            """,
            2);

        return rows.ToDictionary(r => int.Parse(r[0]!), r => r[1] is null ? (int?)null : int.Parse(r[1]!));
    }

    /// <summary>Literal reimplementation of the signature composition over what is ALREADY WRITTEN to the generated SQLite.</summary>
    private static string ComposeExpectedDps(
        List<int> ordinateIdsOfCell,
        IReadOnlyDictionary<int, List<CategorisationEntry>> ownCategorisationsByOrdinateId,
        IReadOnlyDictionary<int, int?> parentByOrdinateId)
    {
        var chosen = new Dictionary<int, (int Depth, CategorisationEntry Entry)>();

        foreach (var start in ordinateIdsOfCell)
        {
            var depth = 0;
            int? current = start;
            var visited = new HashSet<int>();

            while (current is { } currentId && visited.Add(currentId))
            {
                if (ownCategorisationsByOrdinateId.TryGetValue(currentId, out var cats))
                {
                    foreach (var c in cats)
                    {
                        if (!chosen.TryGetValue(c.DimensionId, out var existing) || depth < existing.Depth)
                        {
                            chosen[c.DimensionId] = (depth, c);
                        }
                    }
                }

                current = parentByOrdinateId.TryGetValue(currentId, out var parent) ? parent : null;
                depth++;
            }
        }

        var alive = chosen.Values.Where(v => v.Entry.IsMetric || !v.Entry.IsDefaultMember).ToList();

        var metricEntries = alive.Where(v => v.Entry.IsMetric).ToList();
        var ordered = new List<CategorisationEntry>(alive.Count);
        if (metricEntries.Count == 1)
        {
            ordered.Add(metricEntries[0].Entry);
        }

        ordered.AddRange(alive
            .Where(v => !v.Entry.IsMetric)
            .OrderBy(v => v.Entry.DimensionXbrlCode, StringComparer.Ordinal)
            .Select(v => v.Entry));

        return string.Join("|", ordered.Select(e => e.Dps));
    }

    /// <summary>
    /// Compares <c>mTableCell.DPS</c> against the closure recomposed through <c>ParentOrdinateID</c>
    /// from <c>mCellPosition</c> and <c>mOrdinateCategorisation</c>, applying the signature rule
    /// literally.
    ///
    /// This test used to name three exceptions (<c>CellID</c> 91076/91081/91086, of <c>F_44.04</c>,
    /// an ABSTRACT ordinate with its own cells). They were closed as a side effect of the "one
    /// dimension, one axis" arbitration: by removing the categorisations propagated to axes they did
    /// not belong to, the closure through <c>ParentOrdinateID</c> no longer trips on those three
    /// cells; the real list went from <c>[91076,91081,91086]</c> to <c>[]</c>. Chasing the old list
    /// after fixing what it documented would be the wrong pattern, so the test is stated as the
    /// PROPERTY (zero discrepancies), never as a count or as the <c>CellID</c> values of a state
    /// already superseded. If a future regression reopens any discrepancy -- these three or others
    /// -- this test must fail again, and it does: the assertion below demands
    /// <c>failures.Count == 0</c> with no named exception.
    /// </summary>
    [DataFact]
    public void CellDps_IsExactlyTheInheritedCompositionOfOrdinateCategorisationDps()
    {
        var ownCategorisationsByOrdinateId = BuildOwnCategorisationsByOrdinateId(fixture.GeneratedConnection);
        var parentByOrdinateId = BuildClosedParentByOrdinateId(fixture.GeneratedConnection);

        var cellRows = QueryHelpers.Rows(fixture.GeneratedConnection, "SELECT \"CellID\", \"DPS\" FROM \"mTableCell\" WHERE \"DPS\" IS NOT NULL", 2);
        Assert.True(cellRows.Count > 0);

        var positionRows = QueryHelpers.Rows(fixture.GeneratedConnection, "SELECT \"CellID\", \"OrdinateID\" FROM \"mCellPosition\"", 2);
        var ordinateIdsByCell = new Dictionary<int, List<int>>();
        foreach (var row in positionRows)
        {
            var cellId = int.Parse(row[0]!);
            var ordinateId = int.Parse(row[1]!);
            if (!ordinateIdsByCell.TryGetValue(cellId, out var list))
            {
                list = [];
                ordinateIdsByCell[cellId] = list;
            }

            list.Add(ordinateId);
        }

        var compared = 0;
        var failures = new List<string>();

        foreach (var row in cellRows)
        {
            var cellId = int.Parse(row[0]!);
            var actualDps = row[1]!;

            if (!ordinateIdsByCell.TryGetValue(cellId, out var ordinateIds))
            {
                failures.Add($"CellID={cellId}: has no mCellPosition (anomaly).");
                continue;
            }

            compared++;
            var expectedDps = ComposeExpectedDps(ordinateIds, ownCategorisationsByOrdinateId, parentByOrdinateId);

            if (!string.Equals(expectedDps, actualDps, StringComparison.Ordinal))
            {
                failures.Add($"CellID={cellId}: emitted='{actualDps}' recomposed-by-inheritance='{expectedDps}'");
            }
        }

        // The census is neither pinned to a number (it breaks with --all) nor loosened to a
        // threshold (a threshold does not catch thousands of cells being lost along the way -- e.g.
        // "has no mCellPosition" subtracting from `compared` without anyone noticing). It is
        // DERIVED, over the same database, from the same condition used by the SELECT of `cellRows`
        // (DPS NOT NULL) plus the structural condition that guarantees it (DPS is NULL if and only
        // if IsShaded=1, hence "DPS NOT NULL" == "IsShaded=0" -- the IsShaded condition is kept
        // explicit because it is the BUSINESS condition, not an accident of NULL). This equality is
        // STRICTER than a pinned number: that one would expire with a new release or a different
        // set of taxonomies; this one does not, because it compares `compared` against the census
        // of the database itself.
        var nonShadedWithDps = QueryHelpers.Scalar(
            fixture.GeneratedConnection,
            "SELECT COUNT(*) FROM \"mTableCell\" WHERE \"IsShaded\" = 0 AND \"DPS\" IS NOT NULL");
        Assert.True(nonShadedWithDps > 90000, $"Derived census suspiciously small: {nonShadedWithDps} non-shaded cells with DPS.");
        Assert.Equal(
            nonShadedWithDps,
            compared);

        // The recomposed DPS closure matches the emitted one in 100% of the cells. No named
        // exception: if a single discrepancy appears again, regression or new, this Assert must
        // fail and show it.
        Assert.True(
            failures.Count == 0,
            $"{failures.Count}/{compared} cells whose recomposed DPS closure does NOT match the "
            + "emitted one:\n"
            + string.Join("\n", failures.Take(30)));
    }
}
