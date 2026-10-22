using EbaDpm.Converter.Core.Access.Dpm20;

namespace EbaDpm.Converter.Tests.Infrastructure;

/// <summary>
/// The walk behind the "variable contexts never carry a default member" corollary, as a function
/// so that the real-data test and its synthetic positive control run the SAME code: table
/// versions -&gt; non-excluded cells with a variable -&gt; <c>VariableVersion.ContextID</c> -&gt;
/// <c>ContextComposition</c> pairs -&gt; pairs whose <c>ItemID</c> is flagged <c>IsDefaultItem</c>
/// in ANY <c>ItemCategory</c> row (any release, as strict as possible).
/// </summary>
internal static class DefaultMemberPairs
{
    internal sealed record Result(
        int TableVIdCount,
        int VariableVIdCount,
        int ContextIdCount,
        int PairCount,
        int DeclaredItemIdCount,
        int DefaultItemIdCount,
        IReadOnlyList<int> DeclaredDefaultItemIds,
        int DeclaredDefaultPairs);

    public static Result Count(Dpm20AccessReader reader, IReadOnlyList<int> tableVIds)
    {
        var variableVIds = reader.ReadTableVersionCellsByTableVIds(tableVIds)
            .Where(c => !c.IsExcluded && c.VariableVId is not null)
            .Select(c => c.VariableVId!.Value)
            .Distinct()
            .ToList();

        var contextIds = reader.ReadVariableVersionsByVariableVIds(variableVIds)
            .Where(v => v.ContextId is not null)
            .Select(v => v.ContextId!.Value)
            .Distinct()
            .ToList();

        var pairs = reader.ReadContextCompositionsByContextIds(contextIds).ToList();
        var declaredItemIds = pairs.Select(r => r.ItemId).Distinct().ToHashSet();

        var defaultItemIds = reader.ReadItemCategories()
            .Where(ic => ic.IsDefaultItem)
            .Select(ic => ic.ItemId)
            .ToHashSet();

        return new Result(
            tableVIds.Count,
            variableVIds.Count,
            contextIds.Count,
            pairs.Count,
            declaredItemIds.Count,
            defaultItemIds.Count,
            declaredItemIds.Intersect(defaultItemIds).Order().ToList(),
            pairs.Count(r => defaultItemIds.Contains(r.ItemId)));
    }
}
