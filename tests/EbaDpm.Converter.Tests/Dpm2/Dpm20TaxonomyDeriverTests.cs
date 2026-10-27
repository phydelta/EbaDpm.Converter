using EbaDpm.Converter.Core.Mapping.Dpm20;
using EbaDpm.Converter.Tests.Infrastructure;
using EbaDpm.Converter.Tests.Schema;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Tests.Dpm2;

/// <summary>
/// <c>Dpm20TaxonomyDeriver</c> over the real 4.2.1 database with the explicit cutoff 4.2. Rule
/// (issue 66): a module version hangs from the taxonomy (framework, <c>StartRelease</c>); the
/// taxonomies are exactly those of the emitted module versions. Every expectation is derived
/// from the Access database with independent OLEDB queries (<see cref="AccessCensus"/>), never
/// through the deriver. The only pin left is the number of distinct table codes, which does not
/// depend on the taxonomy rule.
/// </summary>
[Collection("Dpm2")]
public sealed class Dpm20TaxonomyDeriverTests(Dpm20AccessReaderFixture fixture)
{
    private static string Accdb => RepoPaths.AccessDpm20DatabasePath;

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

    /// <summary>Emitted = non-abstract, without DORA: the pinned number of distinct table codes, one table version per code.</summary>
    [DataFact]
    public void Derive_EmitsExactlyThePinnedDistinctTableCodes()
    {
        var derivation = Derive();

        var emittedTableVIds = derivation.TableVIdsByTaxonomyId.Values
            .SelectMany(v => v)
            .Distinct()
            .ToList();

        var tableVersionByVId = fixture.TableVersions.ToDictionary(tv => tv.TableVId);
        var distinctCodes = emittedTableVIds.Select(id => tableVersionByVId[id].Code).Distinct().ToList();
        ReleasePins.AssertPinned(
            ReleasePins.Dpm20Cutoff42, "distinct emitted table codes", ReleasePins.Dpm20Cutoff42EmittedTableCodes, distinctCodes.Count);
        Assert.Equal(emittedTableVIds.Count, distinctCodes.Count);
    }

    /// <summary>
    /// The taxonomy codes are exactly the distinct (lower-case framework, StartRelease.Code) of the
    /// emitted module versions, read from Access: no taxonomy without a module, none missing. The
    /// distinct frameworks carrying a taxonomy are those of the same query.
    /// </summary>
    [DataFact]
    public void Derive_TaxonomyCodes_AreTheDistinctFrameworkAndStartReleaseOfTheEmittedModuleVersions()
    {
        var derivation = Derive();
        var expected = AccessCensus.EmittedModuleCodesByTaxonomy(Accdb, RepoPaths.Cutoff42ReleaseCode);

        Assert.True(expected.Count > 0, "The independent query returned no taxonomy: the comparison would be vacuous.");
        Assert.Equal(
            expected.Keys.OrderBy(k => k, StringComparer.Ordinal),
            derivation.Taxonomies.Select(t => t.TaxonomyCode).OrderBy(k => k, StringComparer.Ordinal));

        var expectedFrameworks = expected.Keys.Select(k => k[..k.IndexOf(' ', StringComparison.Ordinal)]).Distinct().Count();
        Assert.Equal(expectedFrameworks, derivation.Taxonomies.Select(t => t.FrameworkId).Distinct().Count());

        // DpmPackageCode is the release the module versions start in: the part of the code after the space.
        Assert.All(
            derivation.Taxonomies,
            t => Assert.Equal(t.TaxonomyCode[(t.TaxonomyCode.IndexOf(' ', StringComparison.Ordinal) + 1)..], t.DpmPackageCode));
    }

    /// <summary>The (taxonomy, table) pairs: as many as the independent derivation from Access.</summary>
    [DataFact]
    public void Derive_TableTaxonomyPairs_EqualTheIndependentDerivationFromAccess()
    {
        var derivation = Derive();
        var expectedPairs = AccessCensus.TableCodesByTaxonomy(Accdb, RepoPaths.Cutoff42ReleaseCode).Values.Sum(v => v.Count);
        var actualPairs = derivation.TableCounts.Sum(c => c.TableCount);

        Assert.True(expectedPairs > 0, "The independent query returned no pair: the comparison would be vacuous.");
        Assert.True(
            expectedPairs == actualPairs,
            $"derived (taxonomy, table) pairs: expected {expectedPairs} (derived from '{RepoPaths.AccessDpm20DatabaseFileName}'), actual {actualPairs}.");
    }

    /// <summary>Exact table CODES of every taxonomy, one by one, against the independent derivation.</summary>
    [DataFact]
    public void Derive_TablesOfEveryTaxonomy_EqualTheIndependentDerivationFromAccess()
    {
        var derivation = Derive();
        var tableCodeByVId = fixture.TableVersions.ToDictionary(tv => tv.TableVId, tv => tv.Code);
        var actual = derivation.Taxonomies.ToDictionary(
            t => t.TaxonomyCode,
            t => derivation.TableVIdsByTaxonomyId[t.TaxonomyId].Select(v => tableCodeByVId[v]).ToList(),
            StringComparer.Ordinal);
        var expected = AccessCensus.TableCodesByTaxonomy(Accdb, RepoPaths.Cutoff42ReleaseCode);

        Assert.Equal(expected.Keys.OrderBy(k => k, StringComparer.Ordinal), actual.Keys.OrderBy(k => k, StringComparer.Ordinal));
        foreach (var (code, expectedCodes) in expected)
        {
            var actualCodes = actual[code];
            Assert.True(
                actualCodes.Count == actualCodes.Distinct().Count(),
                $"Taxonomy '{code}' lists a table version twice.");
            Assert.True(
                expectedCodes.SetEquals(actualCodes),
                $"Taxonomy '{code}': tables only in Access [{string.Join(",", expectedCodes.Except(actualCodes))}]; only derived [{string.Join(",", actualCodes.Except(expectedCodes))}].");
        }
    }

    /// <summary>
    /// Plane B, information only. The reference export has more taxonomies than the rule derives (it
    /// keeps the ones of older releases): every derived taxonomy is among its codes (containment of
    /// the derived set in the reference), and the reference taxonomies we do not derive are exactly
    /// those that have no module in the reference.
    /// </summary>
    [DataFact]
    public void Derive_TaxonomyCodes_AreContainedInTheReference_AndTheOnesLeftOutHaveNoModuleThere()
    {
        RepoPaths.EnsureReferenceDatabaseExists();

        var derivation = Derive();
        var derivedCodes = derivation.Taxonomies.Select(t => t.TaxonomyCode).ToHashSet(StringComparer.Ordinal);

        var referenceCodes = ReadReferenceTaxonomyCodes("SELECT TaxonomyCode FROM mTaxonomy");
        ReleasePins.AssertPinned(
            ReleasePins.Reference42, "reference mTaxonomy codes", ReleasePins.Reference42MTaxonomyRows, referenceCodes.Count);

        var onlyDerived = derivedCodes.Except(referenceCodes).ToList();
        Assert.True(
            onlyDerived.Count == 0,
            $"Derived taxonomy codes absent from the reference: {string.Join(", ", onlyDerived)}.");

        var onlyInReference = referenceCodes.Except(derivedCodes).OrderBy(c => c, StringComparer.Ordinal).ToList();
        var withoutModuleInReference = ReadReferenceTaxonomyCodes(
                "SELECT t.TaxonomyCode FROM mTaxonomy t WHERE NOT EXISTS (SELECT 1 FROM mModule m WHERE m.TaxonomyID = t.TaxonomyID)")
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToList();
        Assert.True(
            onlyInReference.SequenceEqual(withoutModuleInReference),
            $"Reference taxonomies we do not derive: [{string.Join(", ", onlyInReference)}]; reference taxonomies without a module: [{string.Join(", ", withoutModuleInReference)}].");
    }

    private static HashSet<string> ReadReferenceTaxonomyCodes(string sql)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = RepoPaths.ReferenceDatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString();

        using var connection = new SqliteConnection(connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = sql;

        var codes = new HashSet<string>(StringComparer.Ordinal);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            codes.Add(reader.GetString(0));
        }

        return codes;
    }

    /// <summary>
    /// Witness case <c>D_04.00</c> (ESG framework): current since 4.0, still alive at the cutoff.
    /// Under the rule "a module version hangs from the taxonomy of its StartRelease" the table lands
    /// ONLY in the taxonomies of the ESG module versions that compose it (here <c>esg 4.2</c>): the
    /// old copies of the table in <c>esg 4.0</c> and <c>esg 4.1</c> do not exist, because no ESG
    /// module version starts in those releases. The expected taxonomies are read from Access.
    /// </summary>
    [DataFact]
    public void Derive_D_04_00_LandsOnlyInTheTaxonomiesOfItsModuleVersions()
    {
        var tableVersion = fixture.TableVersions.SingleOrDefault(tv => tv.Code == "D_04.00");
        Assert.True(tableVersion is not null, "The current version of D_04.00 was not found in the source (has the measured data changed?).");

        var derivation = Derive();

        var taxonomyCodesForTable = derivation.TableVIdsByTaxonomyId
            .Where(kvp => kvp.Value.Contains(tableVersion!.TableVId))
            .Select(kvp => derivation.Taxonomies.Single(t => t.TaxonomyId == kvp.Key).TaxonomyCode)
            .ToHashSet(StringComparer.Ordinal);

        var expected = AccessCensus.TableCodesByTaxonomy(Accdb, RepoPaths.Cutoff42ReleaseCode)
            .Where(kvp => kvp.Value.Contains("D_04.00"))
            .Select(kvp => kvp.Key)
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(expected);
        Assert.Contains("esg 4.2", taxonomyCodesForTable);
        Assert.Equal(expected.OrderBy(c => c, StringComparer.Ordinal), taxonomyCodesForTable.OrderBy(c => c, StringComparer.Ordinal));
        Assert.DoesNotContain("esg 4.0", taxonomyCodesForTable);
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
