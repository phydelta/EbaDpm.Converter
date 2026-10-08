using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Core.Validation.Checks;

/// <summary>
/// Builds and computes the EFFECTIVE CLOSURE of <c>mOrdinateCategorisation</c> over the CLOSED
/// ordinates of a connection. A single place, used by A-OCA-01 (plane A, no reference) and
/// B-OCA-01 (plane B, against a reference), which avoids duplicating the logic.
/// </summary>
public sealed class ClosureIndex
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
    /// In OUR output the metric is not inherited and every closed ordinate already carries its
    /// full closure by construction: the own rows, after discarding default members (except the
    /// metric), ARE the semantic closure. Use for the GENERATED side.
    /// </summary>
    public HashSet<(string Dimension, string? Member)> OwnRowsAsClosure(int ordinateId)
    {
        if (!OwnCategorisationByOrdinateId.TryGetValue(ordinateId, out var own))
        {
            return [];
        }

        var winning = own.GroupBy(x => x.DimensionId).ToDictionary(g => g.Key, g => g.First().MemberId);
        return ToClosureSet(winning);
    }

    /// <summary>
    /// Walks the tree towards the root accumulating, per dimension, the value of the NEAREST
    /// ANCESTOR, with NO exception for the metric. This is how the 4.0/4.2 references must be read
    /// (465 and 2,486 ordinates inherit the metric there); never use this variant on our own
    /// output.
    /// </summary>
    public HashSet<(string Dimension, string? Member)> WalkedClosureAllowingMetricInheritance(int ordinateId)
    {
        var winning = new Dictionary<int, int>();
        var current = (int?)ordinateId;
        var visited = new HashSet<int>();
        while (current is { } id && visited.Add(id))
        {
            if (OwnCategorisationByOrdinateId.TryGetValue(id, out var own))
            {
                foreach (var (dimensionId, memberId) in own)
                {
                    winning.TryAdd(dimensionId, memberId);
                }
            }

            current = ParentByOrdinateId.GetValueOrDefault(id);
        }

        return ToClosureSet(winning);
    }

    /// <summary>
    /// Same as <see cref="WalkedClosureAllowingMetricInheritance"/> but resolving the metric
    /// ONLY from the own row of the starting ordinate (never inherited). It is the self-check of
    /// A-OCA-01 in plane A: it must give exactly the same as <see cref="OwnRowsAsClosure"/> over
    /// the generated output itself.
    /// </summary>
    public HashSet<(string Dimension, string? Member)> WalkedClosureExcludingMetricInheritance(int ordinateId)
    {
        var winning = new Dictionary<int, int>();
        if (OwnCategorisationByOrdinateId.TryGetValue(ordinateId, out var ownOfStart))
        {
            foreach (var (dimensionId, memberId) in ownOfStart.Where(x => MetricDimensionIds.Contains(x.DimensionId)))
            {
                winning[dimensionId] = memberId;
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

                    winning.TryAdd(dimensionId, memberId);
                }
            }

            current = ParentByOrdinateId.GetValueOrDefault(id);
        }

        return ToClosureSet(winning);
    }

    private HashSet<(string, string?)> ToClosureSet(Dictionary<int, int> winningMemberByDimension)
    {
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

public static class ClosureIndexBuilder
{
    public static ClosureIndex Build(SqliteConnection connection, IReadOnlySet<int> tableIds)
    {
        var tableIdList = tableIds.Count == 0 ? "-1" : string.Join(",", tableIds);

        var parentByOrdinateId = SqlHelpers.Rows(
                connection,
                $"""
                SELECT o."OrdinateID", o."ParentOrdinateID"
                FROM "mAxisOrdinate" o
                JOIN "mAxis" a ON a."AxisID" = o."AxisID"
                JOIN "mTableAxis" ta ON ta."AxisID" = a."AxisID"
                WHERE a."IsOpenAxis" = 0 AND ta."TableID" IN ({tableIdList})
                """,
                2)
            .ToDictionary(r => int.Parse(r[0]!), r => r[1] is null ? (int?)null : int.Parse(r[1]!));

        var ownByOrdinate = new Dictionary<int, List<(int, int)>>();
        foreach (var row in SqlHelpers.Rows(
                     connection,
                     $"""
                     SELECT oc."OrdinateID", oc."DimensionID", oc."MemberID"
                     FROM "mOrdinateCategorisation" oc
                     JOIN "mAxisOrdinate" o ON o."OrdinateID" = oc."OrdinateID"
                     JOIN "mAxis" a ON a."AxisID" = o."AxisID"
                     JOIN "mTableAxis" ta ON ta."AxisID" = a."AxisID"
                     WHERE a."IsOpenAxis" = 0 AND ta."TableID" IN ({tableIdList})
                     """,
                     3))
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

        var dimensionRows = SqlHelpers.Rows(connection, "SELECT \"DimensionID\", \"DimensionXBRLCode\", \"DomainID\" FROM \"mDimension\"", 3);
        var dimensionXbrlCodeById = dimensionRows.ToDictionary(r => int.Parse(r[0]!), r => r[1] ?? "?");
        var domainIdByDimensionId = dimensionRows.ToDictionary(r => int.Parse(r[0]!), r => int.Parse(r[2]!));
        var metricDimensionIds = dimensionRows.Where(r => r[1] == "MET").Select(r => int.Parse(r[0]!)).ToHashSet();

        var memberRows = SqlHelpers.Rows(connection, "SELECT \"MemberID\", \"MemberCode\", \"DomainID\", \"IsDefaultMember\" FROM \"mMember\"", 4);
        var memberCodeById = memberRows.ToDictionary(r => int.Parse(r[0]!), r => r[1]);
        var defaultMemberIdByDomainId = new Dictionary<int, int?>();
        foreach (var row in memberRows.Where(r => r[3] == "1"))
        {
            defaultMemberIdByDomainId.TryAdd(int.Parse(row[2]!), int.Parse(row[0]!));
        }

        var leafOrdinateIds = SqlHelpers.Rows(
                connection,
                $"""
                SELECT o."OrdinateID"
                FROM "mAxisOrdinate" o
                JOIN "mAxis" a ON a."AxisID" = o."AxisID"
                JOIN "mTableAxis" ta ON ta."AxisID" = a."AxisID"
                WHERE a."IsOpenAxis" = 0 AND o."IsAbstractHeader" = 0 AND ta."TableID" IN ({tableIdList})
                """,
                1)
            .Select(r => int.Parse(r[0]!)).ToHashSet();

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
}
