using EbaDpm.Converter.Tests.Dictionary;

namespace EbaDpm.Converter.Tests.AxesAndCells;

/// <summary>
/// Data point signatures. Invariants of the CRITICAL layer, checked WITHOUT looking at any
/// reference: they are the only tests that detect a signature error in the taxonomies that have
/// no counterpart in any reference database.
/// </summary>
[Collection("AxesAndCells")]
[Trait("Tier", "RealData")]
public sealed class SignatureStructuralTests
{
    private readonly AxisAndCellFixture _fixture;

    public SignatureStructuralTests(AxisAndCellFixture fixture)
    {
        _fixture = fixture;
    }

    // ------------------------------------------------------------------
    // "MET(...) ALWAYS goes first" and the rest is sorted ORDINALLY (StringComparer.Ordinal, NEVER
    // culture-aware nor case-insensitive) by dimension XBRL code.
    // ------------------------------------------------------------------

    [DataFact]
    public void CellDps_MetGoesFirst_AndTheRestIsOrdinalAscendingByDimensionCode()
    {
        var rows = QueryHelpers.Rows(_fixture.GeneratedConnection, "SELECT \"CellID\", \"DPS\" FROM \"mTableCell\" WHERE \"DPS\" IS NOT NULL", 2);
        Assert.True(rows.Count > 0, "There is no cell with a non-null DPS: the invariant would be vacuous.");

        var failures = new List<string>();

        foreach (var row in rows)
        {
            var cellId = row[0];
            var dps = row[1]!;
            var parts = dps.Split('|');

            var metParts = parts.Where(p => p.StartsWith("MET(", StringComparison.Ordinal)).ToList();
            if (metParts.Count > 1)
            {
                failures.Add($"CellID={cellId}: {metParts.Count} MET(...) pairs in '{dps}' (at most one is allowed)");
                continue;
            }

            if (metParts.Count == 1 && !parts[0].StartsWith("MET(", StringComparison.Ordinal))
            {
                failures.Add($"CellID={cellId}: MET(...) does not come first in '{dps}'");
                continue;
            }

            var rest = parts.Where(p => !p.StartsWith("MET(", StringComparison.Ordinal)).ToList();
            var dimCodes = rest.Select(p =>
            {
                var idx = p.IndexOf('(');
                return idx > 0 ? p[..idx] : p;
            }).ToList();

            var sortedOrdinal = dimCodes.OrderBy(c => c, StringComparer.Ordinal).ToList();
            if (!dimCodes.SequenceEqual(sortedOrdinal, StringComparer.Ordinal))
            {
                failures.Add($"CellID={cellId}: order is NOT ordinal ascending: [{string.Join(",", dimCodes)}] en '{dps}'");
            }
        }

        Assert.True(
            failures.Count == 0,
            $"{failures.Count}/{rows.Count} cells violate 'MET first + rest ordinal ascending':\n" + string.Join("\n", failures.Take(30)));
    }

    // ------------------------------------------------------------------
    // The STRONGEST check on signatures: the signature of EACH cell is exactly the composition,
    // by inheritance over the ordinate tree, of the DPS values of mOrdinateCategorisation. It ties
    // mOrdinateCategorisation and mTableCell together: it reproduces, in the test, the same
    // algorithm that ComposeCellSignature implements in AxisAndCellLoader, but reading ONLY what
    // was written to the generated SQLite (no in-memory state of the loader is reused).
    // ------------------------------------------------------------------

    private sealed record CategorisationEntry(int DimensionId, string Dps, string DimensionXbrlCode, bool IsDefaultMember, bool IsMetric);

    private static Dictionary<int, List<CategorisationEntry>> BuildOwnCategorisationsByOrdinateId(Microsoft.Data.Sqlite.SqliteConnection connection)
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
            var isMetric = dimensionXbrlCode == "MET";

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
    /// <c>OrdinateID -&gt; ParentOrdinateID</c> ONLY for ordinates of CLOSED axes. As in production
    /// (<c>ClosedOrdinateTree</c>), an ordinate of an open axis has no entry, so
    /// <see cref="Dictionary{TKey,TValue}.TryGetValue"/> fails and the walk treats it as a root
    /// without ancestry.
    /// </summary>
    private static Dictionary<int, int?> BuildClosedParentByOrdinateId(Microsoft.Data.Sqlite.SqliteConnection connection)
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

    [DataFact]
    public void CellDps_IsExactlyTheInheritedCompositionOfOrdinateCategorisationDps()
    {
        var ownCategorisationsByOrdinateId = BuildOwnCategorisationsByOrdinateId(_fixture.GeneratedConnection);
        var parentByOrdinateId = BuildClosedParentByOrdinateId(_fixture.GeneratedConnection);

        var cellRows = QueryHelpers.Rows(_fixture.GeneratedConnection, "SELECT \"CellID\", \"DPS\" FROM \"mTableCell\" WHERE \"DPS\" IS NOT NULL", 2);
        Assert.True(cellRows.Count > 0);

        var positionRows = QueryHelpers.Rows(_fixture.GeneratedConnection, "SELECT \"CellID\", \"OrdinateID\" FROM \"mCellPosition\"", 2);
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

        Assert.True(compared > 60000, $"Only {compared} cells were recomposed: matching failed wholesale.");
        Assert.True(
            failures.Count == 0,
            $"{failures.Count}/{compared} cells whose DPS is NOT the inherited composition of mOrdinateCategorisation.DPS (critical layer):\n" + string.Join("\n", failures.Take(30)));
    }

    // ------------------------------------------------------------------
    // Self-consistency of the ID variant. The IDs in the DatapointSignature bracket
    // (mOrdinateCategorisation.DimensionMemberSignature) must resolve, in OUR OWN mHierarchy and
    // mMember, to the rows whose CODE is the one appearing in the DPS bracket. Required at 100%:
    // this check replaces the literal comparison against the reference (IDs of different
    // databases must not be compared).
    // ------------------------------------------------------------------

    private static (string? HierarchyPart, string? MemberPart, string? IncludedPart) SplitBracket(string pair)
    {
        var start = pair.IndexOf("*[", StringComparison.Ordinal);
        if (start < 0)
        {
            return (null, null, null);
        }

        var end = pair.IndexOf(']', start);
        var content = pair[(start + 2)..end];
        var segments = content.Split(';');

        return segments.Length switch
        {
            1 => (segments[0], null, null),
            3 => (segments[0], segments[1], segments[2]),
            _ => throw new InvalidOperationException($"Bracket with {segments.Length} segments, 1 or 3 expected: '{pair}'"),
        };
    }

    [DataFact]
    public void DatapointSignatureBracket_IdsResolve_ToTheRowsWhoseCodeIsInTheDpsBracket_OnOrdinateCategorisation()
    {
        var hierarchyCodeById = QueryHelpers.Rows(_fixture.GeneratedConnection, "SELECT \"HierarchyID\", \"HierarchyCode\" FROM \"mHierarchy\"", 2)
            .ToDictionary(r => int.Parse(r[0]!), r => r[1]);
        var memberCodeById = QueryHelpers.Rows(_fixture.GeneratedConnection, "SELECT \"MemberID\", \"MemberCode\" FROM \"mMember\"", 2)
            .ToDictionary(r => int.Parse(r[0]!), r => r[1]);

        var rows = QueryHelpers.Rows(
            _fixture.GeneratedConnection,
            """
            SELECT "OrdinateID", "DimensionID", "DPS", "DimensionMemberSignature" FROM "mOrdinateCategorisation"
            WHERE "DPS" LIKE '%*[%'
            """,
            4);

        Assert.True(rows.Count > 0, "There is no categorisation with a half-open axis bracket: the invariant would be vacuous.");

        var checkedRows = 0;
        var threePartRows = 0;
        var failures = new List<string>();

        foreach (var row in rows)
        {
            var ordinateId = row[0];
            var dimensionId = row[1];
            var dps = row[2]!;
            var dms = row[3]!;

            var (dpsHierarchy, dpsMember, dpsIncluded) = SplitBracket(dps);
            var (dmsHierarchy, dmsMember, dmsIncluded) = SplitBracket(dms);

            if (dpsHierarchy is null || dmsHierarchy is null)
            {
                failures.Add($"OrdinateID={ordinateId}/DimensionID={dimensionId}: bracket present in one variant and absent from the other (DPS='{dps}' DMS='{dms}')");
                continue;
            }

            checkedRows++;

            if (!int.TryParse(dmsHierarchy, out var hierarchyId) || !hierarchyCodeById.TryGetValue(hierarchyId, out var resolvedHierarchyCode))
            {
                failures.Add($"OrdinateID={ordinateId}: HierarchyID '{dmsHierarchy}' in the DatapointSignature bracket does not resolve in mHierarchy (DPS='{dps}' DMS='{dms}')");
                continue;
            }

            if (!string.Equals(resolvedHierarchyCode, dpsHierarchy, StringComparison.Ordinal))
            {
                failures.Add($"OrdinateID={ordinateId}: HierarchyID {hierarchyId} resolves to HierarchyCode='{resolvedHierarchyCode}', but the DPS bracket says '{dpsHierarchy}' (DPS='{dps}' DMS='{dms}')");
                continue;
            }

            if (dpsMember is null && dmsMember is null)
            {
                continue; // one-part bracket: no starting member
            }

            threePartRows++;

            if (dpsMember is null || dmsMember is null)
            {
                failures.Add($"OrdinateID={ordinateId}: number of bracket parts differs between DPS and DMS (DPS='{dps}' DMS='{dms}')");
                continue;
            }

            if (!int.TryParse(dmsMember, out var startMemberId) || !memberCodeById.TryGetValue(startMemberId, out var resolvedMemberCode))
            {
                failures.Add($"OrdinateID={ordinateId}: MemberID '{dmsMember}' (starting member) in the DatapointSignature bracket does not resolve in mMember (DPS='{dps}' DMS='{dms}')");
                continue;
            }

            if (!string.Equals(resolvedMemberCode, dpsMember, StringComparison.Ordinal))
            {
                failures.Add($"OrdinateID={ordinateId}: MemberID {startMemberId} resolves to MemberCode='{resolvedMemberCode}', but the DPS bracket says '{dpsMember}' (DPS='{dps}' DMS='{dms}')");
                continue;
            }

            if (!string.Equals(dpsIncluded, dmsIncluded, StringComparison.Ordinal))
            {
                failures.Add($"OrdinateID={ordinateId}: inclusion flag differs between DPS ('{dpsIncluded}') y DMS ('{dmsIncluded}')");
            }
        }

        Assert.True(checkedRows > 0, "No row with a bracket was checked: matching failed.");
        Assert.True(threePartRows > 0, "No row with a THREE-part bracket (starting member) was checked: that case would remain uncovered.");
        Assert.True(
            failures.Count == 0,
            $"{failures.Count}/{checkedRows} mOrdinateCategorisation rows where the DatapointSignature bracket does NOT resolve, by code, to the DPS one (required at 100%):\n" + string.Join("\n", failures.Take(30)));
    }

    // ------------------------------------------------------------------
    // Consistency counter: cells whose winning metric categorisation comes from an ANCESTOR
    // ordinate (not from the ordinate directly attached to the cell). It must be 0: our output
    // guarantees that every ordinate with its own metric declares it.
    // ------------------------------------------------------------------

    [DataFact]
    public void MetricInheritedFromAncestorCellCount_IsZero()
    {
        Assert.Equal(0, _fixture.AxisAndCellResult.MetricInheritedFromAncestorCellCount);
    }

    // ------------------------------------------------------------------
    // "DatapointSignature is identical to DPS except inside the brackets": for every cell whose
    // DPS carries no half-open restriction bracket, the two variants must be literally IDENTICAL.
    // Purely structural check, without any reference.
    // ------------------------------------------------------------------

    [DataFact]
    public void DatapointSignature_EqualsDps_Literally_ForEveryCellWithoutABracket()
    {
        var rows = QueryHelpers.Rows(
            _fixture.GeneratedConnection,
            "SELECT \"CellID\", \"DPS\", \"DatapointSignature\" FROM \"mTableCell\" WHERE \"DPS\" IS NOT NULL",
            3);
        Assert.True(rows.Count > 0);

        var withoutBracket = 0;
        var failures = new List<string>();

        foreach (var row in rows)
        {
            var dps = row[1]!;
            if (dps.Contains("*[", StringComparison.Ordinal))
            {
                continue; // compared separately (with a bracket the two variants CAN differ)
            }

            withoutBracket++;
            var datapointSignature = row[2]!;
            if (!string.Equals(dps, datapointSignature, StringComparison.Ordinal))
            {
                failures.Add($"CellID={row[0]}: DPS='{dps}' DatapointSignature='{datapointSignature}'");
            }
        }

        Assert.True(withoutBracket > 0, "There is no cell without a bracket: the invariant would be vacuous.");
        Assert.True(
            failures.Count == 0,
            $"{failures.Count}/{withoutBracket} cells WITHOUT a bracket where DatapointSignature differs literally from DPS:\n" + string.Join("\n", failures.Take(30)));
    }
}
