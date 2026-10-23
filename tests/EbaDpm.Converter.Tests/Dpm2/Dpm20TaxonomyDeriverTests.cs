using EbaDpm.Converter.Core.Mapping.Dpm20;
using EbaDpm.Converter.Tests.Infrastructure;
using EbaDpm.Converter.Tests.Schema;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Tests.Dpm2;

/// <summary>
/// <c>Dpm20TaxonomyDeriver</c>: the taxonomy derivation. The populations are scoped pins of
/// <see cref="ReleasePins"/> (scope <see cref="ReleasePins.Dpm20Cutoff42"/>), exact except where
/// containment (not equality) is the right comparison.
/// </summary>
[Collection("Dpm2")]
public sealed class Dpm20TaxonomyDeriverTests(Dpm20AccessReaderFixture fixture)
{
    /// <summary>
    /// EXACT breakdown per taxonomy, a scoped census (<see cref="ReleasePins.Dpm20Cutoff42"/>) that
    /// sums to <see cref="ReleasePins.Dpm20Cutoff42MTableRows"/> (one more than before the
    /// abstract-table closure rule): a module composition declared <c>C_34.02.b</c> as an ABSTRACT table in <c>if 4.2</c>,
    /// and the abstract-closure rule adds its current concrete children -- the pair
    /// <c>(if 4.2, C_34.02.b)</c> goes from 0 to 1, and only <c>if 4.2</c> changes: no other
    /// taxonomy moves.
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
    public void CurrentTableVersions_AndTheirAbstracts_MatchTheAccessSource()
    {
        // Derived with independent OLEDB queries (AccessCensus), by Release.Code, not measured literals.
        var accdb = RepoPaths.AccessDpm20DatabasePath;
        var expectedCurrent = AccessCensus.CountInForce(accdb, "TableVersion", RepoPaths.Cutoff42ReleaseCode);
        var expectedAbstract = AccessCensus.CountAbstractTableVersionsInForce(accdb, RepoPaths.Cutoff42ReleaseCode);

        Assert.True(
            expectedCurrent == fixture.TableVersions.Count,
            $"TableVersion in force at {RepoPaths.Cutoff42ReleaseCode}: the source counts {expectedCurrent}, the reader returned {fixture.TableVersions.Count}.");
        var abstractCount = fixture.TableVersions.Count(tv => tv.IsAbstract);
        Assert.True(
            expectedAbstract == abstractCount,
            $"Abstract TableVersion in force at {RepoPaths.Cutoff42ReleaseCode}: the source counts {expectedAbstract}, the reader returned {abstractCount}.");
    }

    /// <summary>Emitted = non-abstract, without DORA: the pinned number of table versions, and as many DISTINCT codes.</summary>
    [DataFact]
    public void Derive_EmitsExactlyThePinnedDistinctTableCodes()
    {
        var derivation = Derive();

        var emittedTableVIds = derivation.TableVIdsByTaxonomyId.Values
            .SelectMany(v => v)
            .Distinct()
            .ToList();

        ReleasePins.AssertPinned(
            ReleasePins.Dpm20Cutoff42, "distinct emitted table versions", ReleasePins.Dpm20Cutoff42EmittedTableCodes, emittedTableVIds.Count);

        var tableVersionByVId = fixture.TableVersions.ToDictionary(tv => tv.TableVId);
        var distinctCodes = emittedTableVIds.Select(id => tableVersionByVId[id].Code).Distinct().ToList();
        ReleasePins.AssertPinned(
            ReleasePins.Dpm20Cutoff42, "distinct emitted table codes", ReleasePins.Dpm20Cutoff42EmittedTableCodes, distinctCodes.Count);
    }

    [DataFact]
    public void Derive_ProducesThePinnedTableTaxonomyPairs()
    {
        var derivation = Derive();

        var totalPairs = derivation.TableCounts.Sum(c => c.TableCount);

        // The abstract-closure rule adds the pair (if 4.2, C_34.02.b) that the closure without
        // resolving abstract tables used to lose.
        ReleasePins.AssertPinned(
            ReleasePins.Dpm20Cutoff42, "derived (taxonomy, table) pairs", ReleasePins.Dpm20Cutoff42MTableRows, totalPairs);
    }

    [DataFact]
    public void Derive_ProducesThePinnedTaxonomies_AndThePinnedNumberOfDistinctFrameworks()
    {
        var derivation = Derive();

        // One more than the reference, deliberate: all of the reference's, plus "pay 4.1".
        ReleasePins.AssertPinned(
            ReleasePins.Dpm20Cutoff42, "derived taxonomies", ReleasePins.Dpm20Cutoff42MTaxonomyRows, derivation.Taxonomies.Count);

        var distinctFrameworks = derivation.Taxonomies.Select(t => t.FrameworkId).Distinct().Count();
        Assert.True(
            ReleasePins.Dpm20Cutoff42FrameworksWithTaxonomies == distinctFrameworks,
            ReleasePins.Message(ReleasePins.Dpm20Cutoff42, "distinct frameworks carrying a derived taxonomy", ReleasePins.Dpm20Cutoff42FrameworksWithTaxonomies, distinctFrameworks));
    }

    /// <summary>Exact breakdown, one by one, against <see cref="ExpectedTableCountByTaxonomyCode"/>.</summary>
    [DataFact]
    public void Derive_TableCountPerTaxonomy_MatchesTheMeasuredBreakdown()
    {
        var derivation = Derive();

        var countByCode = derivation.Taxonomies
            .Join(derivation.TableCounts, t => t.TaxonomyId, c => c.TaxonomyId, (t, c) => (t.TaxonomyCode, c.TableCount))
            .ToDictionary(x => x.TaxonomyCode, x => x.TableCount, StringComparer.Ordinal);

        var pin = ReleasePins.Dpm20Cutoff42;
        ReleasePins.AssertPinned(pin, "derived taxonomies with a table count", ExpectedTableCountByTaxonomyCode.Count, countByCode.Count);

        foreach (var (code, expectedCount) in ExpectedTableCountByTaxonomyCode)
        {
            Assert.True(countByCode.TryGetValue(code, out var actualCount), $"Derived taxonomy '{code}' is missing ({pin}).");
            ReleasePins.AssertPinned(pin, $"tables derived for taxonomy '{code}'", expectedCount, actualCount);
        }

        // Identity: the breakdown sums to the pinned mTable population.
        ReleasePins.AssertPinned(
            pin, "sum of the tables derived per taxonomy", ReleasePins.Dpm20Cutoff42MTableRows, countByCode.Values.Sum());
    }

    /// <summary>
    /// Comparison by CONTAINMENT, never by equality. The 31 <c>TaxonomyCode</c> values of
    /// <c>mTaxonomy</c> (EBA_4.2_Hotfix.db) must ALL be among the derived ones, and NONE may be
    /// left only in the reference. A test demanding equal censuses would be wrongly written.
    /// </summary>
    [DataFact]
    public void Derive_Contains_AllReferenceTaxonomyCodes_ByContainment()
    {
        RepoPaths.EnsureReferenceDatabaseExists();

        var derivation = Derive();
        var derivedCodes = derivation.Taxonomies.Select(t => t.TaxonomyCode).ToHashSet(StringComparer.Ordinal);

        var referenceCodes = ReadReferenceTaxonomyCodes();

        var onlyInReference = referenceCodes.Except(derivedCodes).ToList();

        ReleasePins.AssertPinned(
            ReleasePins.Reference42, "reference mTaxonomy codes", ReleasePins.Reference42MTaxonomyRows, referenceCodes.Count);
        Assert.True(
            onlyInReference.Count == 0,
            $"Reference taxonomy codes not derived: {string.Join(", ", onlyInReference)}.");
        Assert.True(referenceCodes.IsSubsetOf(derivedCodes), "The reference taxonomy codes are not a subset of the derived ones.");
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
