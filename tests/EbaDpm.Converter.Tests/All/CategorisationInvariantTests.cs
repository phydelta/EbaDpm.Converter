using EbaDpm.Converter.Tests.Dictionary;

namespace EbaDpm.Converter.Tests.All;

/// <summary>
/// Categorisation invariants A-OCA-01/01b/03/08/09, over <c>--all</c>.
///
/// An earlier version of A-OCA-01 pinned the effective closure at 99.8173 % with 143 residues.
/// That figure is OBSOLETE: since the metric is no longer inherited, the 143 impossible
/// default-member resets no longer exist and the correct criterion is <b>100.0000 %</b>. A test
/// pinned at 99.8173 % would accept 143 failures, so it is not written that way here.
/// </summary>
[Collection("All")]
[Trait("Tier", "RealData")]
public sealed class CategorisationInvariantTests
{
    private readonly AllFixture _fixture;

    public CategorisationInvariantTests(AllFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>Everything needed to compute the closure of the closed ordinates, over ALL of the --all output (not restricted to a subset of taxonomies).</summary>
    private sealed class ClosureIndex
    {
        public required Dictionary<int, int?> ParentByOrdinateId { get; init; }
        public required Dictionary<int, List<(int DimensionId, int MemberId)>> OwnCategorisationByOrdinateId { get; init; }
        public required Dictionary<int, int> DomainIdByDimensionId { get; init; }
        public required Dictionary<int, int?> DefaultMemberIdByDomainId { get; init; }
        public required HashSet<int> MetricDimensionIds { get; init; }
        public required HashSet<int> LeafOrdinateIds { get; init; }

        /// <summary>Closure of <paramref name="ordinateId"/>: walks towards the root, the NEAREST ancestor wins per dimension; the METRIC is never inherited; default members are discarded except for the metric.</summary>
        public HashSet<(int Dimension, int Member)> Closure(int ordinateId)
        {
            var winningMemberByDimension = new Dictionary<int, int>();

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
                            continue;
                        }

                        winningMemberByDimension.TryAdd(dimensionId, memberId);
                    }
                }

                current = ParentByOrdinateId.GetValueOrDefault(id);
            }

            var result = new HashSet<(int, int)>();
            foreach (var (dimensionId, memberId) in winningMemberByDimension)
            {
                if (!MetricDimensionIds.Contains(dimensionId))
                {
                    var domainId = DomainIdByDimensionId.GetValueOrDefault(dimensionId, -1);
                    var defaultMemberId = DefaultMemberIdByDomainId.GetValueOrDefault(domainId);
                    if (defaultMemberId.HasValue && defaultMemberId.Value == memberId)
                    {
                        continue;
                    }
                }

                result.Add((dimensionId, memberId));
            }

            return result;
        }

        public HashSet<(int Dimension, int Member)> OwnRowsAsClosure(int ordinateId)
        {
            var result = new HashSet<(int, int)>();
            if (!OwnCategorisationByOrdinateId.TryGetValue(ordinateId, out var own))
            {
                return result;
            }

            foreach (var (dimensionId, memberId) in own)
            {
                if (!MetricDimensionIds.Contains(dimensionId))
                {
                    var domainId = DomainIdByDimensionId.GetValueOrDefault(dimensionId, -1);
                    var defaultMemberId = DefaultMemberIdByDomainId.GetValueOrDefault(domainId);
                    if (defaultMemberId.HasValue && defaultMemberId.Value == memberId)
                    {
                        continue;
                    }
                }

                result.Add((dimensionId, memberId));
            }

            return result;
        }
    }

    private ClosureIndex BuildClosureIndex()
    {
        var ordinateRows = QueryHelpers.Rows(
            _fixture.GeneratedConnection,
            """
            SELECT o."OrdinateID", o."ParentOrdinateID"
            FROM "mAxisOrdinate" o
            JOIN "mAxis" a ON a."AxisID" = o."AxisID"
            WHERE a."IsOpenAxis" = 0
            """,
            2);

        var parentByOrdinateId = ordinateRows.ToDictionary(r => int.Parse(r[0]!), r => r[1] is null ? (int?)null : int.Parse(r[1]!));

        var categorisationRows = QueryHelpers.Rows(
            _fixture.GeneratedConnection,
            """
            SELECT oc."OrdinateID", oc."DimensionID", oc."MemberID"
            FROM "mOrdinateCategorisation" oc
            JOIN "mAxisOrdinate" o ON o."OrdinateID" = oc."OrdinateID"
            JOIN "mAxis" a         ON a."AxisID" = o."AxisID"
            WHERE a."IsOpenAxis" = 0
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

        var dimensionRows = QueryHelpers.Rows(_fixture.GeneratedConnection, "SELECT \"DimensionID\", \"DimensionXBRLCode\", \"DomainID\" FROM \"mDimension\"", 3);
        var domainIdByDimensionId = dimensionRows.ToDictionary(r => int.Parse(r[0]!), r => int.Parse(r[2]!));
        var metricDimensionIds = dimensionRows.Where(r => r[1] == "MET").Select(r => int.Parse(r[0]!)).ToHashSet();

        var memberRows = QueryHelpers.Rows(_fixture.GeneratedConnection, "SELECT \"MemberID\", \"DomainID\", \"IsDefaultMember\" FROM \"mMember\"", 3);
        var defaultMemberIdByDomainId = new Dictionary<int, int?>();
        foreach (var row in memberRows.Where(r => r[2] == "1"))
        {
            defaultMemberIdByDomainId.TryAdd(int.Parse(row[1]!), int.Parse(row[0]!));
        }

        var leafRows = QueryHelpers.Rows(
            _fixture.GeneratedConnection,
            """
            SELECT o."OrdinateID" FROM "mAxisOrdinate" o
            JOIN "mAxis" a ON a."AxisID" = o."AxisID"
            WHERE a."IsOpenAxis" = 0 AND o."IsAbstractHeader" = 0
            """,
            1);

        return new ClosureIndex
        {
            ParentByOrdinateId = parentByOrdinateId,
            OwnCategorisationByOrdinateId = ownByOrdinate,
            DomainIdByDimensionId = domainIdByDimensionId,
            DefaultMemberIdByDomainId = defaultMemberIdByDomainId,
            MetricDimensionIds = metricDimensionIds,
            LeafOrdinateIds = leafRows.Select(r => int.Parse(r[0]!)).ToHashSet(),
        };
    }

    /// <summary>
    /// A-OCA-01, over --all (128 taxonomies, not just a small subset): the closure walked through
    /// the tree matches EXACTLY (100.0000 %) the ordinate's own rows, for EVERY closed non-abstract
    /// ordinate of the complete output.
    /// </summary>
    [DataFact]
    public void OwnRows_OfEveryClosedOrdinate_AlreadyEqualTheTreeWalkedClosure_OnAll()
    {
        var closure = BuildClosureIndex();

        var compared = 0;
        var mismatches = new List<string>();

        foreach (var ordinateId in closure.LeafOrdinateIds)
        {
            compared++;
            var walked = closure.Closure(ordinateId);
            var own = closure.OwnRowsAsClosure(ordinateId);

            if (!walked.SetEquals(own))
            {
                mismatches.Add($"OrdinateID={ordinateId}");
            }
        }

        Assert.True(compared > 0, "No closed ordinate was compared: the --all universe is empty.");
        Assert.True(
            mismatches.Count == 0,
            $"{mismatches.Count}/{compared} closed ordinates of --all whose tree-walked closure " +
            $"does NOT match their own rows (A-OCA-01, critical layer - 100.0000 % required, NOT " +
            $"99.8173 %, which is the obsolete figure). Examples: {string.Join(",", mismatches.Take(20))}");
    }

    /// <summary>
    /// A-OCA-01b: direct corollary of A-OCA-01. <c>SkippedDefaultMemberResets</c> is 0 in any
    /// conversion -- it is the cheapest self-check of the whole categorisation, a single counter
    /// that the converter already prints.
    /// </summary>
    [DataFact]
    public void SkippedDefaultMemberResets_IsZero_OnAll()
    {
        Assert.True(
            _fixture.AxisAndCellResult.SkippedDefaultMemberResets == 0,
            $"SkippedDefaultMemberResets = {_fixture.AxisAndCellResult.SkippedDefaultMemberResets} over --all " +
            "(exactly 0 expected, A-OCA-01b: the 143 impossible resets no longer exist).");
    }

    /// <summary>Consistency check of the no-metric-inheritance rule: cells whose metric would come from an ancestor. Currently 0.</summary>
    [DataFact]
    public void MetricInheritedFromAncestorCellCount_IsZero_OnAll()
    {
        Assert.True(
            _fixture.AxisAndCellResult.MetricInheritedFromAncestorCellCount == 0,
            $"MetricInheritedFromAncestorCellCount = {_fixture.AxisAndCellResult.MetricInheritedFromAncestorCellCount} " +
            "over --all (exactly 0 expected).");
    }

    /// <summary>
    /// A-OCA-03: every categorisation member belongs to the domain of its dimension, except for
    /// the 9999 sentinel.
    ///
    /// <c>m."DomainID" &lt;&gt; d."DomainID"</c> would be NULL-unsafe: <c>mMember.DomainID</c> is
    /// <c>int?</c> in the source (<c>AccessMemberRow.DomainId</c>) and is written verbatim
    /// (<c>DictionaryLoader.LoadMembers</c>), so in theory it CAN be NULL in the output (currently
    /// measured as 0 rows, but that is a measured fact, not a guarantee of the type). With
    /// <c>&lt;&gt;</c>, a row with <c>m.DomainID IS NULL</c> would yield NULL in the comparison and
    /// would NOT be counted as a violation -- a false-green mechanism. <c>IS NOT</c> (NULL-safe) is
    /// used: if <c>m.DomainID</c> were NULL, the member does not belong to the domain of its
    /// dimension (which is <c>NOT NULL</c> as the PK of <c>mDomain</c>), so it MUST count as a
    /// violation. <c>m."MemberID" &lt;&gt; 9999</c> is safe as is: <c>MemberID</c> is the PK of
    /// <c>mMember</c>, never NULL.
    /// </summary>
    [DataFact]
    public void EveryCategorisationMember_BelongsToTheDomainOfItsDimension_ExcludingTheOpenSentinel()
    {
        var violations = QueryHelpers.Scalar(
            _fixture.GeneratedConnection,
            """
            SELECT COUNT(*) FROM "mOrdinateCategorisation" oc
            JOIN "mDimension" d ON d."DimensionID" = oc."DimensionID"
            JOIN "mMember" m    ON m."MemberID" = oc."MemberID"
            WHERE m."MemberID" <> 9999 AND m."DomainID" IS NOT d."DomainID"
            """);

        Assert.True(violations == 0, $"{violations} mOrdinateCategorisation rows whose member does not belong to the domain of its dimension, excluding the 9999 sentinel (A-OCA-03).");

        // And, with the sentinel included: they are EXACTLY the open axes (A-SEN-07 already
        // requires it; it is corroborated here from the categorisation angle).
        var sentinelRows = QueryHelpers.Scalar(_fixture.GeneratedConnection, "SELECT COUNT(*) FROM \"mOrdinateCategorisation\" WHERE \"MemberID\" = 9999");
        var openAxisCount = QueryHelpers.Scalar(_fixture.GeneratedConnection, "SELECT COUNT(*) FROM \"mAxis\" WHERE \"IsOpenAxis\" = 1");
        Assert.True(sentinelRows == openAxisCount, $"Rows with MemberID=9999: {sentinelRows} versus open axes: {openAxisCount} (should match, A-SEN-07/A-OCA-03).");
    }

    /// <summary>A-OCA-08: the starting member of mOpenAxisValueRestriction, when present, belongs to the hierarchy it points to.</summary>
    [DataFact]
    public void StartingMember_WhenPresent_BelongsToItsOwnHierarchy()
    {
        var violations = QueryHelpers.Scalar(
            _fixture.GeneratedConnection,
            """
            SELECT COUNT(*) FROM "mOpenAxisValueRestriction" r
            WHERE r."HierarchyStartingMemberID" IS NOT NULL
              AND NOT EXISTS (
                  SELECT 1 FROM "mHierarchyNode" n
                  WHERE n."HierarchyID" = r."HierarchyID" AND n."MemberID" = r."HierarchyStartingMemberID"
              )
            """);

        Assert.True(violations == 0, $"{violations} mOpenAxisValueRestriction rows whose HierarchyStartingMemberID does not belong to the hierarchy it points to (A-OCA-08).");
    }

    /// <summary>A-OCA-09: no open-axis restriction on a hierarchy of a TYPED domain.</summary>
    [DataFact]
    public void NoOpenAxisValueRestriction_PointsToAHierarchyOfATypedDomain()
    {
        var violations = QueryHelpers.Scalar(
            _fixture.GeneratedConnection,
            """
            SELECT COUNT(*) FROM "mOpenAxisValueRestriction" r
            JOIN "mHierarchy" h ON h."HierarchyID" = r."HierarchyID"
            JOIN "mDomain" d    ON d."DomainID" = h."DomainID"
            WHERE d."IsTypedDomain" = 1
            """);

        Assert.True(violations == 0, $"{violations} open-axis restrictions on hierarchies of a typed domain (A-OCA-09).");
    }
}
