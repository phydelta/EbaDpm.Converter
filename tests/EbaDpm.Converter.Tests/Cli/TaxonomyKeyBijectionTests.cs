using EbaDpm.Converter.Core.Mapping;
using EbaDpm.Converter.Tests.Dictionary;
using EbaDpm.Converter.Tests.Infrastructure;
using EbaDpm.Converter.Tests.Schema;

namespace EbaDpm.Converter.Tests.Cli;

/// <summary>
/// The <c>TaxonomyKey</c> is BIJECTIVE over the whole Access database: one distinct key per
/// taxonomy, none shared and no taxonomy with two. This is an invariant of the SOURCE; it is
/// checked WITHOUT looking at any reference: it protects the taxonomies that have no
/// reference as much as the comparable ones.
///
/// It reuses <see cref="DictionaryFixture.AccessReader"/> (already open for the "Dictionary"
/// collection): <c>ReadTaxonomies()</c> and <c>ReadModules()</c> do not filter by selection, so it
/// does not matter that the fixture only converts <c>COREP 3.2</c>; it reads the whole Access
/// database.
/// </summary>
[Collection("Dictionary")]
public sealed class TaxonomyKeyBijectionTests
{
    private readonly DictionaryFixture _fixture;

    public TaxonomyKeyBijectionTests(DictionaryFixture fixture)
    {
        _fixture = fixture;
    }

    [DataFact]
    public void TaxonomyKey_IsBijective_OverTheWholeAccess()
    {
        var taxonomies = _fixture.AccessReader.ReadTaxonomies().ToList();
        var modules = _fixture.AccessReader.ReadModules().ToList();

        Assert.True(taxonomies.Count > 0, "No taxonomy was read from the Access database.");
        Assert.True(modules.Count > 0, "No module was read from the Access database.");

        // If some module had no extractable key, or if a single taxonomy yielded two different
        // keys, this would already throw TaxonomyKeyResolutionException, so nothing more would
        // need checking. That it does not throw is the first half of the invariant.
        var keyByTaxonomyId = TaxonomyKeyResolver.Resolve(modules);

        // Every taxonomy has modules (and therefore a key): none is left unresolved.
        var taxonomyIdsWithModules = modules.Select(m => m.TaxonomyId).ToHashSet();
        var taxonomiesWithoutAnyModule = taxonomies
            .Where(t => !taxonomyIdsWithModules.Contains(t.TaxonomyId))
            .ToList();
        Assert.True(
            taxonomiesWithoutAnyModule.Count == 0,
            $"{taxonomiesWithoutAnyModule.Count} taxonomies without any module (no resolvable TaxonomyKey): " +
            string.Join(", ", taxonomiesWithoutAnyModule.Take(10).Select(t => t.TaxonomyCode)));

        Assert.Equal(taxonomies.Count, keyByTaxonomyId.Count); // 0 taxonomies with two keys (Resolve already guarantees it) and 0 without a key

        var distinctKeys = keyByTaxonomyId.Values.Distinct(StringComparer.Ordinal).Count();
        Assert.Equal(keyByTaxonomyId.Count, distinctKeys); // 0 keys shared by two taxonomies

        // Derived from the Access source itself (plain COUNT(1) of [Taxonomy]), not from the reader
        // under test.
        var sourceTaxonomies = AccessCensus.Count(RepoPaths.AccessDatabasePath, "Taxonomy");
        Assert.True(
            sourceTaxonomies == taxonomies.Count,
            $"Taxonomies read by the reader: expected {sourceTaxonomies} (COUNT(1) of [Taxonomy] in '{RepoPaths.AccessDatabaseFileName}'), actual {taxonomies.Count}.");
        Assert.True(
            sourceTaxonomies == distinctKeys,
            $"Distinct TaxonomyKey values: expected {sourceTaxonomies} (COUNT(1) of [Taxonomy] in '{RepoPaths.AccessDatabaseFileName}'), actual {distinctKeys}.");
    }
}
