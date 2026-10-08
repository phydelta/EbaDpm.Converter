using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Core.Validation.Checks;

/// <summary>The comparable universe of plane B, derived from the two databases, never requested on the command line.</summary>
public sealed record ComparableUniverse(
    IReadOnlySet<string> Taxonomies,
    IReadOnlyList<string> GeneratedOnlyTaxonomies,
    IReadOnlyList<string> ReferenceOnlyTaxonomies);

public static class ComparableUniverseResolver
{
    public static ComparableUniverse Resolve(SqliteConnection generated, SqliteConnection reference)
    {
        var generatedCodes = PlaneBSupport.Codes(generated, "SELECT \"TaxonomyCode\" FROM \"mTaxonomy\"")
            .Select(Normalization.TaxonomyCode).ToHashSet(StringComparer.Ordinal);
        var referenceCodes = PlaneBSupport.Codes(reference, "SELECT \"TaxonomyCode\" FROM \"mTaxonomy\"")
            .Select(Normalization.TaxonomyCode).ToHashSet(StringComparer.Ordinal);

        var common = generatedCodes.Intersect(referenceCodes, StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        var generatedOnly = generatedCodes.Except(referenceCodes, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var referenceOnly = referenceCodes.Except(generatedCodes, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();

        return new ComparableUniverse(common, generatedOnly, referenceOnly);
    }
}
