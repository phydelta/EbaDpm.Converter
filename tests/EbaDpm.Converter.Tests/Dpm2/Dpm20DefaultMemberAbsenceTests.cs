using EbaDpm.Converter.Core.Access.Dpm20;

namespace EbaDpm.Converter.Tests.Dpm2;

/// <summary>
/// "The reset comes from the ABSENCE, and that is why it is not an empirical rule" -
/// <c>Dpm20AxisAndCellLoader.ProjectVariableContextOntoOrdinates</c> writes a reset (the domain's
/// default member) for a dimension of an ordinate EXCLUSIVELY when NO cell of that ordinate carries
/// that dimension in the context of its variable. The corollary that supports it, as measured: the
/// variable's context (<c>VariableVersion.ContextID</c> -&gt; <c>ContextComposition</c>) NEVER
/// contains an item flagged <c>IsDefaultItem</c> - 0 out of 487,399 pairs.
///
/// This test measures that corollary COMPLETELY INDEPENDENTLY of <c>Dpm20AxisAndCellLoader</c>: it
/// uses only <see cref="Dpm20AccessReader"/> (public reads, none of which reuses the production
/// algorithm) over the current <see cref="Dpm20AccessReaderFixture.TableVersions"/> (929).
/// Measured: 487,399 pairs, so this universe matches that of the <c>--all</c> conversion for this
/// corollary. The result - ZERO - is not a threshold that can be relaxed: a single default would
/// be enough to refute it.
///
/// From here follows the corollary "no reset row can have a member that the variable's context
/// declares" BY LOGICAL CONSTRUCTION, not by a second measurement: if the variable's context NEVER
/// declares the default member of any domain (this test), then whenever a dimension DOES appear in
/// the context of some cell, the member it carries can NEVER be the one a reset would write. The
/// two conditions are mutually exclusive by the definition of the source data itself, not by a
/// coincidence measured on the output.
/// </summary>
[Collection("Dpm2")]
public sealed class Dpm20DefaultMemberAbsenceTests(Dpm20AccessReaderFixture fixture)
{
    // The only SLOW test (~19 s) of the "Dpm2" collection: it walks 487,399 real pairs through
    // OLEDB, a volume that cannot be cached (it is not a repeated call, it is a single large
    // query). The rest of the collection stays in the fast loop: only this method is tagged, not
    // the class nor the collection.
    [Trait("Tier", "RealData")]
    [DataFact]
    public void TheVariableContext_NeverCarriesADefaultMember()
    {
        var reader = fixture.Reader;

        // Step 1: ALL the TableVersionCell of the 929 current tables -> non-null VariableVID.
        var tableVIds = fixture.TableVersions.Select(t => t.TableVId).ToList();
        Assert.True(tableVIds.Count > 900, $"Only {tableVIds.Count} current TableVID: the universe is unexpectedly small.");

        var variableVIds = reader.ReadTableVersionCellsByTableVIds(tableVIds)
            .Where(c => !c.IsExcluded && c.VariableVId is not null)
            .Select(c => c.VariableVId!.Value)
            .Distinct()
            .ToList();
        Assert.True(variableVIds.Count > 50000, $"Only {variableVIds.Count} distinct VariableVID: the universe is unexpectedly small.");

        // Step 2: VariableVersion.ContextID of those variables (the ALREADY RESOLVED context of the data point).
        var contextIds = reader.ReadVariableVersionsByVariableVIds(variableVIds)
            .Where(v => v.ContextId is not null)
            .Select(v => v.ContextId!.Value)
            .Distinct()
            .ToList();
        Assert.True(contextIds.Count > 10000, $"Only {contextIds.Count} distinct ContextID: the universe is unexpectedly small.");

        // Step 3: ContextComposition of those contexts -> the ItemIDs they declare.
        var contextCompositionRows = reader.ReadContextCompositionsByContextIds(contextIds).ToList();
        Assert.True(contextCompositionRows.Count > 100000, $"Only {contextCompositionRows.Count} (Context,Property,Item) pairs: the universe is unexpectedly small.");

        var declaredItemIds = contextCompositionRows.Select(r => r.ItemId).Distinct().ToHashSet();

        // Step 4: ItemCategory.IsDefaultItem - ALL the rows, without restricting by release, so
        // that the check is as strict as possible: if ANY row of ANY release flags that ItemID as
        // the default member of its domain, it counts as a violation.
        var defaultItemIds = reader.ReadItemCategories()
            .Where(ic => ic.IsDefaultItem)
            .Select(ic => ic.ItemId)
            .ToHashSet();

        var declaredDefaultItemIds = declaredItemIds.Intersect(defaultItemIds).ToList();

        Assert.True(
            declaredDefaultItemIds.Count == 0,
            $"{declaredDefaultItemIds.Count} ItemID flagged IsDefaultItem DO appear in the context of some variable "
            + $"(out of {declaredItemIds.Count} distinct declared ItemID, {contextCompositionRows.Count} pairs): "
            + string.Join(", ", declaredDefaultItemIds.Take(20)));

        var declaredDefaultPairs = contextCompositionRows.Count(r => defaultItemIds.Contains(r.ItemId));
        // Measured independently: 487,399 pairs. The "current" universe matches that of the --all
        // conversion for this corollary (the 53 subcategories add no new pairs here).
        Assert.Equal(487399, contextCompositionRows.Count);
        Assert.Equal(0, declaredDefaultPairs);
    }
}
