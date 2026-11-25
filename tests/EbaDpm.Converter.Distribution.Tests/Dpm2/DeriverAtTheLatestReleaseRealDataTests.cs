using EbaDpm.Converter.Distribution.Access.Dpm20;
using EbaDpm.Converter.Distribution.Mapping.Dpm20;
using EbaDpm.Converter.Distribution.Tests.Infrastructure;
using EbaDpm.Converter.Distribution.Tests.Schema;

namespace EbaDpm.Converter.Distribution.Tests.Dpm2;

/// <summary>
/// <see cref="Dpm20TaxonomyDeriver"/> on the real 4.2.1 and 4.3 databases at the LATEST release of
/// each (the reader without a requested cutoff), compared with the independent derivation from Access
/// (<see cref="AccessCensus"/>). Since #73 a converted file only holds the taxonomies of its own
/// release, so this is where the derivation across SEVERAL start releases (a module version that
/// started in an earlier release and is still in force) is checked on real data: the taxonomies are
/// the distinct (framework, start release) of the module versions in force, and the tables of each
/// are the ones the independent derivation finds.
/// </summary>
[Trait("Tier", "RealData")]
public sealed class DeriverAtTheLatestReleaseRealDataTests
{
    private static void Check(string accdb, string latestReleaseCode)
    {
        using var reader = new Dpm20AccessReader(accdb);
        reader.Open();
        Assert.Equal(latestReleaseCode, reader.CutoffReleaseCode);
        Assert.False(reader.CutoffReleaseRequested);

        var tableVersions = reader.ReadTableVersions().ToList();
        var derivation = Dpm20TaxonomyDeriver.Derive(
            reader.ReadReleases().ToList(),
            reader.ReadFrameworks().ToList(),
            reader.ReadModules().ToList(),
            reader.ReadModuleVersions().ToList(),
            reader.ReadModuleVersionCompositions().ToList(),
            tableVersions,
            reader.CutoffReleaseId);

        var expectedModules = AccessCensus.EmittedModuleCodesByTaxonomy(accdb, latestReleaseCode);
        Assert.NotEmpty(expectedModules);
        Assert.Equal(
            expectedModules.Keys.OrderBy(k => k, StringComparer.Ordinal),
            derivation.Taxonomies.Select(t => t.TaxonomyCode).OrderBy(k => k, StringComparer.Ordinal));

        // Positive control: some taxonomy belongs to an EARLIER release than the latest one, i.e. a
        // module version that started earlier is still in force. Without it the test would not
        // exercise the "taxonomy of the start release, not of the cutoff" rule.
        Assert.Contains(expectedModules.Keys, k => !k.EndsWith(" " + latestReleaseCode, StringComparison.Ordinal));

        var tableCodeByVId = tableVersions.ToDictionary(tv => tv.TableVId, tv => tv.Code);
        var actual = derivation.Taxonomies.ToDictionary(
            t => t.TaxonomyCode,
            t => derivation.TableVIdsByTaxonomyId[t.TaxonomyId].Select(v => tableCodeByVId[v]).ToList(),
            StringComparer.Ordinal);
        var expectedTables = AccessCensus.TableCodesByTaxonomy(accdb, latestReleaseCode);
        Assert.Equal(expectedTables.Keys.OrderBy(k => k, StringComparer.Ordinal), actual.Keys.OrderBy(k => k, StringComparer.Ordinal));
        var problems = new List<string>();
        foreach (var (code, codes) in expectedTables)
        {
            if (!codes.SetEquals(actual[code]))
            {
                problems.Add($"{code}: only in Access [{string.Join(",", codes.Except(actual[code]))}], only derived [{string.Join(",", actual[code].Except(codes))}]");
            }
        }

        Assert.True(problems.Count == 0, string.Join(" | ", problems));
    }

    [DataFact]
    public void Dpm2042_1Database_AtRelease421_TaxonomiesAndTables_EqualTheIndependentDerivation()
    {
        RepoPaths.EnsureAccessDpm20DatabaseExists();
        Check(RepoPaths.AccessDpm20DatabasePath, Dpm2042_1ValidateFixture.Release421Code);
    }

    [DataFact]
    public void Dpm2043Database_AtRelease43_TaxonomiesAndTables_EqualTheIndependentDerivation()
    {
        RepoPaths.EnsureAccessDpm20Release43DatabaseExists();
        Check(RepoPaths.AccessDpm20Release43DatabasePath, Dpm2043ValidateFixture.Release43Code);
    }

    [DataFact]
    public void Dpm2044Database_AtRelease44_TaxonomiesAndTables_EqualTheIndependentDerivation()
    {
        RepoPaths.EnsureAccessDpm20Release44DatabaseExists();
        Check(RepoPaths.AccessDpm20Release44DatabasePath, RepoPaths.Release44Code);
    }
}
