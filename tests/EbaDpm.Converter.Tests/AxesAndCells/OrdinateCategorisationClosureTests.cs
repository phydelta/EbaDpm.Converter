using EbaDpm.Converter.Tests.Dictionary;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Tests.AxesAndCells;

/// <summary>
/// Together with <c>mAxisOrdinate.OrdinateCode</c>, the most sensitive comparison of the project:
/// <c>mOrdinateCategorisation</c> by EFFECTIVE CLOSURE, not row by row.
///
/// The Access materialises the COMPLETE closure on each ordinate; our output does the same (each
/// closed ordinate emits its own Access categorisation PLUS the explicit resets that cancel what
/// the parent had and the child does not), so the own set of rows of a generated ordinate, after
/// discarding default members (except the metric), IS ALREADY the semantic closure, with no need
/// to walk the tree.
///
/// The REFERENCE, in contrast, stores a DELTA: its closure must be rebuilt by walking the tree
/// from the ordinate to the root, keeping the value of the NEAREST ANCESTOR for each dimension,
/// and then discarding default members (except the metric). That is exactly the input of the
/// signature algorithm. Measured: 6,877 / 6,877 = 100.0000% over the CLOSED ordinates that
/// "contribute data" against the primary 3.2 reference. That universe is NOT "touches some cell
/// position" (abstract headers also carry positions): it is exactly the complement of
/// <c>IsAbstractHeader = 1</c> within the closed ordinates (6,877 + 649 = 7,526 in 3.2), so the
/// right filter is <c>IsAbstractHeader = 0</c>; see <c>BuildClosureIndex</c>. The comparison is
/// made ordinate by ordinate, by business key (never by ID).
///
/// This is NOT a relaxation: it records the measured fact that the compressed data of the
/// reference and ours DENOTE THE SAME THING. A test comparing the LITERAL set of rows would fail
/// by design (we emit about 26,281 rows where 3.2 has 15,724), so that is not tested here.
/// </summary>
[Collection("AxesAndCells")]
[Trait("Tier", "RealData")]
public sealed class OrdinateCategorisationClosureTests
{
    private static readonly HashSet<string> TargetTaxonomies = new(StringComparer.Ordinal)
    {
        "ae_3.2", "corep_3.2", "gsii_3.2", "if_3.2",
    };

    private readonly AxisAndCellFixture _fixture;

    public OrdinateCategorisationClosureTests(AxisAndCellFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>Everything needed to compute the closure of the closed ordinates of ONE connection.</summary>
    private sealed class ClosureIndex
    {
        public required Dictionary<int, int?> ParentByOrdinateId { get; init; }
        public required Dictionary<int, List<(int DimensionId, int MemberId)>> OwnCategorisationByOrdinateId { get; init; }
        public required Dictionary<int, string> DimensionXbrlCodeById { get; init; }
        public required Dictionary<int, int> DomainIdByDimensionId { get; init; }
        public required Dictionary<int, int?> DefaultMemberIdByDomainId { get; init; }
        public required Dictionary<int, string?> MemberCodeById { get; init; }
        public required HashSet<int> MetricDimensionIds { get; init; }
        public required HashSet<int> LeafOrdinateIds { get; init; }

        /// <summary>
        /// Closure of <paramref name="ordinateId"/>: walks the tree towards the root accumulating,
        /// per dimension, the value of the NEAREST ANCESTOR (starting with the ordinate itself),
        /// and then discards default members, except in the METRIC dimension.
        /// </summary>
        public HashSet<(string Dimension, string? Member)> Closure(int ordinateId)
        {
            var winningMemberByDimension = new Dictionary<int, int>();

            // The METRIC is NEVER inherited through the tree: every ordinate with its own metric
            // declares it, and no ordinate inherits its parent's. It is resolved BEFORE the
            // general walk, looking ONLY at the own row of the starting ordinate, and stays out of
            // the ancestor loop below (which only handles NON-metric dimensions).
            // This holds for 3.2 (no ordinate inherits it; it is the reference compared in this
            // file) and for our own output; it does NOT hold to interpret 4.0 (465 ordinates
            // inherit it) or 4.2 (2,486) if this code is ever reused against them.
            if (OwnCategorisationByOrdinateId.TryGetValue(ordinateId, out var ownOfStart))
            {
                foreach (var (dimensionId, memberId) in ownOfStart)
                {
                    if (MetricDimensionIds.Contains(dimensionId))
                    {
                        winningMemberByDimension[dimensionId] = memberId;
                    }
                }
            }

            var current = (int?)ordinateId;
            var visited = new HashSet<int>();

            while (current is { } id && visited.Add(id))
            {
                if (OwnCategorisationByOrdinateId.TryGetValue(id, out var own))
                {
                    foreach (var (dimensionId, memberId) in own)
                    {
                        if (MetricDimensionIds.Contains(dimensionId))
                        {
                            continue; // already resolved above, no inheritance
                        }

                        winningMemberByDimension.TryAdd(dimensionId, memberId); // the nearest one already set wins
                    }
                }

                current = ParentByOrdinateId.GetValueOrDefault(id);
            }

            var result = new HashSet<(string, string?)>();
            foreach (var (dimensionId, memberId) in winningMemberByDimension)
            {
                var isMetric = MetricDimensionIds.Contains(dimensionId);
                if (!isMetric)
                {
                    var domainId = DomainIdByDimensionId.GetValueOrDefault(dimensionId, -1);
                    var defaultMemberId = DefaultMemberIdByDomainId.GetValueOrDefault(domainId);
                    if (defaultMemberId.HasValue && defaultMemberId.Value == memberId)
                    {
                        continue; // default member: discarded
                    }
                }

                var dimensionCode = DimensionXbrlCodeById.GetValueOrDefault(dimensionId, $"?{dimensionId}");
                var memberCode = MemberCodeById.GetValueOrDefault(memberId);
                result.Add((dimensionCode, memberCode));
            }

            return result;
        }

        /// <summary>
        /// The closure of the GENERATED output does not need a tree walk: every closed ordinate
        /// already carries its own complete closure by construction. Walking the tree would give
        /// the same result, but computing it directly is the most literal check of "the own rows
        /// already are the closure", which is precisely the property the converter promises.
        /// </summary>
        public HashSet<(string Dimension, string? Member)> OwnRowsAsClosure(int ordinateId)
        {
            var result = new HashSet<(string, string?)>();
            if (!OwnCategorisationByOrdinateId.TryGetValue(ordinateId, out var own))
            {
                return result;
            }

            foreach (var (dimensionId, memberId) in own)
            {
                var isMetric = MetricDimensionIds.Contains(dimensionId);
                if (!isMetric)
                {
                    var domainId = DomainIdByDimensionId.GetValueOrDefault(dimensionId, -1);
                    var defaultMemberId = DefaultMemberIdByDomainId.GetValueOrDefault(domainId);
                    if (defaultMemberId.HasValue && defaultMemberId.Value == memberId)
                    {
                        continue;
                    }
                }

                var dimensionCode = DimensionXbrlCodeById.GetValueOrDefault(dimensionId, $"?{dimensionId}");
                var memberCode = MemberCodeById.GetValueOrDefault(memberId);
                result.Add((dimensionCode, memberCode));
            }

            return result;
        }
    }

    private static ClosureIndex BuildClosureIndex(SqliteConnection connection, IReadOnlySet<int> tableIds)
    {
        var tableIdList = tableIds.Count == 0 ? "-1" : string.Join(",", tableIds);

        var ordinateRows = QueryHelpers.Rows(
            connection,
            $"""
            SELECT o."OrdinateID", o."ParentOrdinateID"
            FROM "mAxisOrdinate" o
            JOIN "mAxis" a       ON a."AxisID" = o."AxisID"
            JOIN "mTableAxis" ta ON ta."AxisID" = a."AxisID"
            WHERE a."IsOpenAxis" = 0 AND ta."TableID" IN ({tableIdList})
            """,
            2);

        var parentByOrdinateId = ordinateRows.ToDictionary(
            r => int.Parse(r[0]!),
            r => r[1] is null ? (int?)null : int.Parse(r[1]!));

        var categorisationRows = QueryHelpers.Rows(
            connection,
            $"""
            SELECT oc."OrdinateID", oc."DimensionID", oc."MemberID"
            FROM "mOrdinateCategorisation" oc
            JOIN "mAxisOrdinate" o ON o."OrdinateID" = oc."OrdinateID"
            JOIN "mAxis" a         ON a."AxisID" = o."AxisID"
            JOIN "mTableAxis" ta   ON ta."AxisID" = a."AxisID"
            WHERE a."IsOpenAxis" = 0 AND ta."TableID" IN ({tableIdList})
            """,
            3);

        var ownByOrdinate = new Dictionary<int, List<(int, int)>>();
        foreach (var row in categorisationRows)
        {
            var ordinateId = int.Parse(row[0]!);
            var dimensionId = int.Parse(row[1]!);
            var memberId = int.Parse(row[2]!);

            if (!ownByOrdinate.TryGetValue(ordinateId, out var list))
            {
                list = [];
                ownByOrdinate[ordinateId] = list;
            }

            list.Add((dimensionId, memberId));
        }

        var dimensionRows = QueryHelpers.Rows(connection, "SELECT \"DimensionID\", \"DimensionXBRLCode\", \"DomainID\" FROM \"mDimension\"", 3);
        var dimensionXbrlCodeById = dimensionRows.ToDictionary(r => int.Parse(r[0]!), r => r[1] ?? "?");
        var domainIdByDimensionId = dimensionRows.ToDictionary(r => int.Parse(r[0]!), r => int.Parse(r[2]!));
        var metricDimensionIds = dimensionRows.Where(r => r[1] == "MET").Select(r => int.Parse(r[0]!)).ToHashSet();

        var memberRows = QueryHelpers.Rows(connection, "SELECT \"MemberID\", \"MemberCode\", \"DomainID\", \"IsDefaultMember\" FROM \"mMember\"", 4);
        var memberCodeById = memberRows.ToDictionary(r => int.Parse(r[0]!), r => r[1]);

        var defaultMemberIdByDomainId = new Dictionary<int, int?>();
        foreach (var row in memberRows.Where(r => r[3] == "1"))
        {
            var domainId = int.Parse(row[2]!);
            var memberId = int.Parse(row[0]!);
            defaultMemberIdByDomainId.TryAdd(domainId, memberId);
        }

        // "With cells (those that contribute data)" is, as measured, EXACTLY the complement of
        // "abstract header" within the closed ordinates (6,877 + 649 = 7,526 in 3.2; the two
        // figures add up to the total). It is NOT "touches some cell position": abstract ordinates
        // ALSO carry positions (always on shaded cells), but they are deliberately excluded from
        // "contribute data" because there the reference generator sometimes RAISES a pair that
        // the Access leaves on the children, and that raise is not what the effective closure
        // compares. The right criterion is therefore <c>IsAbstractHeader = 0</c>.
        var leafRows = QueryHelpers.Rows(
            connection,
            $"""
            SELECT o."OrdinateID"
            FROM "mAxisOrdinate" o
            JOIN "mAxis" a       ON a."AxisID" = o."AxisID"
            JOIN "mTableAxis" ta ON ta."AxisID" = a."AxisID"
            WHERE a."IsOpenAxis" = 0 AND o."IsAbstractHeader" = 0 AND ta."TableID" IN ({tableIdList})
            """,
            1);
        var leafOrdinateIds = leafRows.Select(r => int.Parse(r[0]!)).ToHashSet();

        return new ClosureIndex
        {
            ParentByOrdinateId = parentByOrdinateId,
            OwnCategorisationByOrdinateId = ownByOrdinate,
            DimensionXbrlCodeById = dimensionXbrlCodeById,
            DomainIdByDimensionId = domainIdByDimensionId,
            DefaultMemberIdByDomainId = defaultMemberIdByDomainId,
            MemberCodeById = memberCodeById,
            MetricDimensionIds = metricDimensionIds,
            LeafOrdinateIds = leafOrdinateIds,
        };
    }

    [DataFact]
    public void EffectiveClosure_OfOrdinatesWithCells_MatchesReference32_Exactly()
    {
        var tableIdsGenerated = BusinessKeySupport.BuildTableIdsByBusinessKey(_fixture.GeneratedConnection, TargetTaxonomies);
        var tableIdsReference = BusinessKeySupport.BuildTableIdsByBusinessKey(_fixture.Reference32Connection, TargetTaxonomies);

        var generatedPathIndex = BusinessKeySupport.BuildClosedOrdinatePathIndex(_fixture.GeneratedConnection, tableIdsGenerated.Values.ToHashSet());
        var referencePathIndex = BusinessKeySupport.BuildClosedOrdinatePathIndex(_fixture.Reference32Connection, tableIdsReference.Values.ToHashSet());

        var generatedClosure = BuildClosureIndex(_fixture.GeneratedConnection, tableIdsGenerated.Values.ToHashSet());
        var referenceClosure = BuildClosureIndex(_fixture.Reference32Connection, tableIdsReference.Values.ToHashSet());

        var compared = 0;
        var mismatches = new List<string>();
        var missingInGenerated = new List<string>();

        foreach (var (tableKey, refTableId) in tableIdsReference)
        {
            if (!tableIdsGenerated.TryGetValue(tableKey, out var genTableId))
            {
                continue; // checked elsewhere (table coverage)
            }

            foreach (var ((tableId, axis, path), refOrdinateId) in referencePathIndex)
            {
                if (tableId != refTableId || !referenceClosure.LeafOrdinateIds.Contains(refOrdinateId))
                {
                    continue; // only the ordinates that carry cells
                }

                if (!generatedPathIndex.TryGetValue((genTableId, axis, path), out var genOrdinateId))
                {
                    missingInGenerated.Add($"{tableKey.Taxonomy}/{tableKey.TableCode}/{axis}/{path}: ordinate missing from the generated output");
                    continue;
                }

                compared++;
                var refSet = referenceClosure.Closure(refOrdinateId);
                var genSet = generatedClosure.OwnRowsAsClosure(genOrdinateId);

                if (!refSet.SetEquals(genSet))
                {
                    var missing = refSet.Except(genSet).Select(p => $"+{p.Dimension}({p.Member})");
                    var extra = genSet.Except(refSet).Select(p => $"-{p.Dimension}({p.Member})");
                    mismatches.Add(
                        $"{tableKey.Taxonomy}/{tableKey.TableCode}/{axis}/{path}: " +
                        string.Join(" ", missing.Concat(extra)));
                }
            }
        }

        Assert.True(compared > 6000, $"Only {compared} ordinates with cells were compared: matching failed wholesale (about 6,877 expected).");
        Assert.True(
            missingInGenerated.Count == 0,
            $"{missingInGenerated.Count}/{compared} ordinates with cells of the 3.2 reference missing from the generated output:\n" + string.Join("\n", missingInGenerated.Take(20)));
        Assert.True(
            mismatches.Count == 0,
            $"{mismatches.Count}/{compared} ordinates with an effective closure different from the 3.2 reference (critical layer):\n" + string.Join("\n", mismatches.Take(30)));
    }

    // ------------------------------------------------------------------
    // Structural invariant (no reference): the closure of the GENERATED output computed by
    // walking the tree (the full algorithm, as for the reference) coincides with the closure
    // taken directly from the own rows of the ordinate (the shortcut the converter promises). If
    // this failed, the "safe form" would not be correctly restoring what the parent contributes
    // and the child does not, the same bug that the signatures would silently inherit.
    // ------------------------------------------------------------------

    [DataFact]
    public void OwnRows_OfGeneratedClosedOrdinates_AlreadyEqualTheTreeWalkedClosure()
    {
        var tableIdsGenerated = BusinessKeySupport.BuildTableIdsByBusinessKey(_fixture.GeneratedConnection, TargetTaxonomies);
        var generatedClosure = BuildClosureIndex(_fixture.GeneratedConnection, tableIdsGenerated.Values.ToHashSet());

        var compared = 0;
        var mismatches = new List<string>();

        foreach (var ordinateId in generatedClosure.LeafOrdinateIds)
        {
            compared++;
            var walked = generatedClosure.Closure(ordinateId);
            var own = generatedClosure.OwnRowsAsClosure(ordinateId);

            if (!walked.SetEquals(own))
            {
                var missing = walked.Except(own).Select(p => $"+{p.Dimension}({p.Member})");
                var extra = own.Except(walked).Select(p => $"-{p.Dimension}({p.Member})");
                mismatches.Add($"OrdinateID={ordinateId}: " + string.Join(" ", missing.Concat(extra)));
            }
        }

        Assert.True(compared > 0);
        Assert.True(
            mismatches.Count == 0,
            $"{mismatches.Count}/{compared} generated ordinates where the own rows do NOT reproduce the closure walked through the tree (bug in BuildSafeFormCategorisations):\n" + string.Join("\n", mismatches.Take(30)));
    }
}
