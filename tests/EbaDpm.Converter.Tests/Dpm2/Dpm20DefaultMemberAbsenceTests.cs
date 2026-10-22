using EbaDpm.Converter.Core.Access.Dpm20;
using EbaDpm.Converter.Tests.Infrastructure;
using EbaDpm.Converter.Tests.Schema;

namespace EbaDpm.Converter.Tests.Dpm2;

/// <summary>
/// "The reset comes from the ABSENCE, and that is why it is not an empirical rule" -
/// <c>Dpm20AxisAndCellLoader.ProjectVariableContextOntoOrdinates</c> writes a reset (the domain's
/// default member) for a dimension of an ordinate EXCLUSIVELY when NO cell of that ordinate carries
/// that dimension in the context of its variable. The corollary that supports it, as measured: the
/// variable's context (<c>VariableVersion.ContextID</c> -&gt; <c>ContextComposition</c>) NEVER
/// contains an item flagged <c>IsDefaultItem</c> - zero pairs out of the whole universe.
///
/// This test measures that corollary COMPLETELY INDEPENDENTLY of <c>Dpm20AxisAndCellLoader</c>: it
/// uses only <see cref="Dpm20AccessReader"/> (public reads, none of which reuses the production
/// algorithm) over the current <see cref="Dpm20AccessReaderFixture.TableVersions"/>. The size of every step of
/// the universe (tables, variables, contexts, pairs) is DERIVED with an independent OLEDB census
/// (<see cref="AccessCensus"/>) and compared for equality, so a reader that returned one row fewer
/// would fail instead of shrinking the universe unnoticed. The result - ZERO - is not a threshold
/// that can be relaxed: a single default would be enough to refute it.
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
    // The slow test of the "Dpm2" collection: it walks the real pairs through OLEDB, a volume that
    // cannot be cached (it is not a repeated call, it is a single large query). The rest of the
    // collection stays in the fast loop: only this method is tagged, not the class nor the collection.
    [Trait("Tier", "RealData")]
    [DataFact]
    public void TheVariableContext_NeverCarriesADefaultMember()
    {
        var reader = fixture.Reader;
        var accdb = RepoPaths.AccessDpm20DatabasePath;
        var releaseId = AccessCensus.ReleaseId(accdb, RepoPaths.Cutoff42ReleaseCode);
        var tablesInForce = $"SELECT [TableVID] FROM [TableVersion] WHERE {AccessCensus.InForce(releaseId)}";
        var variablesOfCells =
            "SELECT DISTINCT [VariableVID] FROM [TableVersionCell] "
            + $"WHERE [IsExcluded] = 0 AND [VariableVID] IS NOT NULL AND [TableVID] IN ({tablesInForce})";
        var contextsOfVariables =
            "SELECT DISTINCT [ContextID] FROM [VariableVersion] "
            + $"WHERE [ContextID] IS NOT NULL AND [VariableVID] IN ({variablesOfCells})";

        // The same function the synthetic positive control (DefaultMemberPairsTests) runs. It reads
        // ItemCategory.IsDefaultItem over ALL the rows, without restricting by release, so the check
        // is as strict as possible: ANY row of ANY release that flags the ItemID counts.
        var tableVIds = fixture.TableVersions.Select(t => t.TableVId).ToList();
        var result = DefaultMemberPairs.Count(reader, tableVIds);

        AssertUniverse("current TableVID", AccessCensus.Scalar(accdb, $"SELECT COUNT(1) FROM ({tablesInForce})"), result.TableVIdCount);
        AssertUniverse("distinct VariableVID", AccessCensus.Scalar(accdb, $"SELECT COUNT(1) FROM ({variablesOfCells})"), result.VariableVIdCount);
        AssertUniverse("distinct ContextID", AccessCensus.Scalar(accdb, $"SELECT COUNT(1) FROM ({contextsOfVariables})"), result.ContextIdCount);

        // The expected pair count streams the whole ContextComposition table and counts the rows
        // whose context belongs to the independently derived context set (a nested IN is too slow in ACE).
        var expectedContexts = AccessCensus.Ints(accdb, contextsOfVariables).ToHashSet();
        var expectedPairs = AccessCensus.Ints(accdb, "SELECT [ContextID] FROM [ContextComposition]").LongCount(expectedContexts.Contains);
        AssertUniverse("(Context,Property,Item) pairs", expectedPairs, result.PairCount);

        // The check examines something: the source does flag default members.
        Assert.True(result.DefaultItemIdCount > 0, "No ItemCategory row is flagged IsDefaultItem: the zero below would prove nothing.");

        Assert.True(
            result.DeclaredDefaultItemIds.Count == 0,
            $"{result.DeclaredDefaultItemIds.Count} ItemID flagged IsDefaultItem DO appear in the context of some variable "
            + $"(out of {result.DeclaredItemIdCount} distinct declared ItemID, {result.PairCount} pairs): "
            + string.Join(", ", result.DeclaredDefaultItemIds.Take(20)));
        Assert.True(
            result.DeclaredDefaultPairs == 0,
            $"{result.DeclaredDefaultPairs} of the {result.PairCount} (Context,Property,Item) pairs carry a default member.");
    }

    private static void AssertUniverse(string what, long expected, long actual)
        => Assert.True(expected == actual, $"{what}: the independent Access census counts {expected}, the reader walk found {actual}.");
}
