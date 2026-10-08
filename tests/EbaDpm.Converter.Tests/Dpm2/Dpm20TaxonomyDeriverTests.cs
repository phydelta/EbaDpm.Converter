using EbaDpm.Converter.Core.Mapping.Dpm20;
using EbaDpm.Converter.Tests.Schema;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Tests.Dpm2;

/// <summary>
/// <c>Dpm20TaxonomyDeriver</c>: the taxonomy derivation. Figures measured against the source, all
/// exact except where containment (not equality) is the right comparison.
/// </summary>
[Collection("Dpm2")]
public sealed class Dpm20TaxonomyDeriverTests(Dpm20AccessReaderFixture fixture)
{
    /// <summary>
    /// EXACT breakdown per taxonomy, summing to 847 (it was 846 before the abstract-table closure
    /// rule): a module composition declared <c>C_34.02.b</c> as an ABSTRACT table in <c>if 4.2</c>,
    /// and the abstract-closure rule adds its current concrete children -- the pair
    /// <c>(if 4.2, C_34.02.b)</c> goes from 0 to 1, and only <c>if 4.2</c> changes (47 -&gt; 48):
    /// no other taxonomy moves.
    /// </summary>
    private static readonly Dictionary<string, int> ExpectedTableCountByTaxonomyCode = new(StringComparer.Ordinal)
    {
        ["ae 4.2"] = 23,
        ["corep 4.0"] = 2,
        ["corep 4.2"] = 159,
        ["esg 4.0"] = 3,
        ["esg 4.1"] = 4,
        ["esg 4.2"] = 25,
        ["fc 3.5"] = 1,
        ["fc 4.0"] = 1,
        ["fc 4.2"] = 13,
        ["finrep 4.0"] = 3,
        ["finrep 4.2"] = 136,
        ["fp 4.2"] = 15,
        ["gsii 4.2"] = 1,
        ["if 4.0"] = 2,
        ["if 4.2"] = 48,
        ["imprac 4.2"] = 4,
        ["ipu 4.2"] = 1,
        ["irrbb 4.2"] = 18,
        ["mica 4.0"] = 1,
        ["mica 4.1"] = 3,
        ["mica 4.2"] = 19,
        ["mrel 4.2"] = 10,
        ["pay 4.1"] = 1,
        ["pay 4.2"] = 20,
        ["pillar3 4.0"] = 3,
        ["pillar3 4.1"] = 19,
        ["pillar3 4.2"] = 193,
        ["rem 3.5"] = 2,
        ["rem 4.0"] = 3,
        ["rem 4.2"] = 39,
        ["res 4.2"] = 38,
        ["sbp 4.2"] = 37,
    };

    private Dpm20TaxonomyDerivationResult Derive()
        => Dpm20TaxonomyDeriver.Derive(
            fixture.Releases,
            fixture.Frameworks,
            fixture.Modules,
            fixture.ModuleVersions,
            fixture.ModuleVersionCompositions,
            fixture.TableVersions,
            fixture.Reader.CutoffReleaseId);

    [DataFact]
    public void CurrentTableVersions_Are929_With126Abstract()
    {
        Assert.Equal(929, fixture.TableVersions.Count);
        Assert.Equal(126, fixture.TableVersions.Count(tv => tv.IsAbstract));
    }

    /// <summary>Emitted = non-abstract, without DORA -> 788, and 788 DISTINCT codes.</summary>
    [DataFact]
    public void Derive_EmitsExactly788DistinctTableCodes()
    {
        var derivation = Derive();

        var emittedTableVIds = derivation.TableVIdsByTaxonomyId.Values
            .SelectMany(v => v)
            .Distinct()
            .ToList();

        Assert.Equal(788, emittedTableVIds.Count);

        var tableVersionByVId = fixture.TableVersions.ToDictionary(tv => tv.TableVId);
        var distinctCodes = emittedTableVIds.Select(id => tableVersionByVId[id].Code).Distinct().ToList();
        Assert.Equal(788, distinctCodes.Count);
    }

    [DataFact]
    public void Derive_Produces847TableTaxonomyPairs()
    {
        var derivation = Derive();

        var totalPairs = derivation.TableCounts.Sum(c => c.TableCount);

        // 847, not 846: the abstract-closure rule adds the pair (if 4.2, C_34.02.b) that the closure
        // without resolving abstract tables used to lose.
        Assert.Equal(847, totalPairs);
    }

    [DataFact]
    public void Derive_Produces32Taxonomies_18DistinctFrameworks()
    {
        var derivation = Derive();

        // 32, NOT 31: deliberate -- all 31 of the reference, plus "pay 4.1".
        Assert.Equal(32, derivation.Taxonomies.Count);

        var distinctFrameworks = derivation.Taxonomies.Select(t => t.FrameworkId).Distinct().Count();
        Assert.Equal(18, distinctFrameworks);
    }

    /// <summary>Exact breakdown, one by one, against <see cref="ExpectedTableCountByTaxonomyCode"/>.</summary>
    [DataFact]
    public void Derive_TableCountPerTaxonomy_MatchesTheMeasuredBreakdown()
    {
        var derivation = Derive();

        var countByCode = derivation.Taxonomies
            .Join(derivation.TableCounts, t => t.TaxonomyId, c => c.TaxonomyId, (t, c) => (t.TaxonomyCode, c.TableCount))
            .ToDictionary(x => x.TaxonomyCode, x => x.TableCount, StringComparer.Ordinal);

        Assert.Equal(ExpectedTableCountByTaxonomyCode.Count, countByCode.Count);

        foreach (var (code, expectedCount) in ExpectedTableCountByTaxonomyCode)
        {
            Assert.True(countByCode.TryGetValue(code, out var actualCount), $"Derived taxonomy '{code}' is missing.");
            Assert.True(
                expectedCount == actualCount,
                $"'{code}': expected {expectedCount} tables, derived {actualCount}.");
        }

        // 847, not 846: see the comment on ExpectedTableCountByTaxonomyCode.
        Assert.Equal(847, countByCode.Values.Sum());
    }

    /// <summary>
    /// Comparison by CONTAINMENT, never by equality. The 31 <c>TaxonomyCode</c> values of
    /// <c>mTaxonomy</c> (EBA_4.2_Hotfix.db) must ALL be among the 32 derived ones, and ZERO may be
    /// left only in the reference. A test demanding equal censuses (31 == 32) would be wrongly written.
    /// </summary>
    [DataFact]
    public void Derive_Contains_AllReferenceTaxonomyCodes_ByContainment()
    {
        RepoPaths.EnsureReferenceDatabaseExists();

        var derivation = Derive();
        var derivedCodes = derivation.Taxonomies.Select(t => t.TaxonomyCode).ToHashSet(StringComparer.Ordinal);

        var referenceCodes = ReadReferenceTaxonomyCodes();

        var onlyInReference = referenceCodes.Except(derivedCodes).ToList();

        Assert.Equal(31, referenceCodes.Count);
        Assert.Empty(onlyInReference);
        Assert.True(referenceCodes.IsSubsetOf(derivedCodes));
    }

    private static HashSet<string> ReadReferenceTaxonomyCodes()
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = RepoPaths.ReferenceDatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString();

        using var connection = new SqliteConnection(connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT TaxonomyCode FROM mTaxonomy";

        var codes = new HashSet<string>(StringComparer.Ordinal);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            codes.Add(reader.GetString(0));
        }

        return codes;
    }

    /// <summary>
    /// The INTERMEDIATE release does not generate a pair. Witness case <c>D_04.00</c> (ESG
    /// framework): current since 4.0, still alive today (4.2). It must produce <c>esg 4.0</c> and
    /// <c>esg 4.2</c>, and NOT <c>esg 4.1</c> -- skipping the intermediate release is the rule, not
    /// an accident.
    /// </summary>
    [DataFact]
    public void Derive_D_04_00_SkipsTheIntermediateRelease()
    {
        var tableVersion = fixture.TableVersions.SingleOrDefault(tv => tv.Code == "D_04.00");
        Assert.True(tableVersion is not null, "The current version of D_04.00 was not found in the source (has the measured data changed?).");

        var derivation = Derive();

        var taxonomyCodesForTable = derivation.TableVIdsByTaxonomyId
            .Where(kvp => kvp.Value.Contains(tableVersion!.TableVId))
            .Select(kvp => derivation.Taxonomies.Single(t => t.TaxonomyId == kvp.Key).TaxonomyCode)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("esg 4.0", taxonomyCodesForTable);
        Assert.Contains("esg 4.2", taxonomyCodesForTable);
        Assert.DoesNotContain("esg 4.1", taxonomyCodesForTable);
    }

    /// <summary>Synthetic, deterministic <c>TaxonomyId</c>: two derivations over the SAME input give the SAME ids.</summary>
    [DataFact]
    public void Derive_TaxonomyIds_AreDeterministic_AcrossTwoRuns()
    {
        var first = Derive();
        var second = Derive();

        var firstIdsByCode = first.Taxonomies.ToDictionary(t => t.TaxonomyCode, t => t.TaxonomyId, StringComparer.Ordinal);
        var secondIdsByCode = second.Taxonomies.ToDictionary(t => t.TaxonomyCode, t => t.TaxonomyId, StringComparer.Ordinal);

        Assert.Equal(firstIdsByCode, secondIdsByCode);
    }

    /// <summary>
    /// Unlike DPM 1.0 (functional relationship), in DPM 2.0 a table can fall into MORE THAN ONE
    /// taxonomy (of a different framework). It is checked that at least one such case exists.
    /// </summary>
    [DataFact]
    public void Derive_AtLeastOneTable_FallsInMoreThanOneFramework()
    {
        var derivation = Derive();

        var frameworkCodeByTaxonomyId = derivation.Taxonomies.ToDictionary(t => t.TaxonomyId, t => t.TechnicalStandard);

        var frameworksByTableVId = new Dictionary<int, HashSet<string?>>();
        foreach (var (taxonomyId, tableVIds) in derivation.TableVIdsByTaxonomyId)
        {
            var frameworkCode = frameworkCodeByTaxonomyId[taxonomyId];
            foreach (var tableVId in tableVIds)
            {
                if (!frameworksByTableVId.TryGetValue(tableVId, out var set))
                {
                    set = [];
                    frameworksByTableVId[tableVId] = set;
                }

                set.Add(frameworkCode);
            }
        }

        var tablesInMoreThanOneFramework = frameworksByTableVId.Count(kvp => kvp.Value.Count > 1);

        Assert.True(tablesInMoreThanOneFramework > 0, "At least one table in more than one framework was expected (non-functional relationship in DPM 2.0).");
    }
}
