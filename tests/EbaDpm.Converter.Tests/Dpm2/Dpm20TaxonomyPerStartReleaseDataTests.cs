using System.Globalization;
using System.Text.RegularExpressions;
using EbaDpm.Converter.Tests.Infrastructure;
using EbaDpm.Converter.Tests.Schema;
using EbaDpm.Converter.Tests.Support;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Tests.Dpm2;

/// <summary>
/// DPM 2.0, issue 66, on the REAL natural-cutoff outputs of the 4.2.1 and 4.3 databases: a module
/// version hangs from the taxonomy (framework, <c>ModuleVersion.StartReleaseID</c>), the taxonomies
/// are exactly those of the emitted module versions, and every <c>mModule.XBRLSchemaRef</c> release
/// is the start release of its module version. The expectations are read from the Access database
/// with independent OLEDB queries (<see cref="AccessCensus"/>); no literal count is pinned.
/// </summary>
public abstract class TaxonomyPerStartReleaseDataTestsBase
{
    private static readonly Regex SchemaRefPattern = new(
        @"^http://www\.eba\.europa\.eu/eu/fr/xbrl/crr/fws/(?<fw>[^/]+)/(?<release>[^/]+)/mod/(?<code>[^/]+)\.xsd$",
        RegexOptions.Compiled);

    protected abstract string Accdb { get; }

    protected abstract string CutoffReleaseCode { get; }

    protected abstract string GeneratedDatabasePath { get; }

    protected Dictionary<string, SortedSet<string>> Expected() => AccessCensus.EmittedModuleCodesByTaxonomy(Accdb, CutoffReleaseCode);

    private SqliteConnection OpenOutput()
    {
        var c = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = GeneratedDatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        c.Open();
        return c;
    }

    protected List<string[]> Rows(string sql, int columns)
    {
        using var c = OpenOutput();
        using var command = c.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = new List<string[]>();
        while (reader.Read())
        {
            rows.Add(Enumerable.Range(0, columns).Select(i => Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture) ?? string.Empty).ToArray());
        }

        return rows;
    }

    [DataFact]
    public void TheQueryIsNotDegenerate_AtLeastOneTaxonomyIsNotOnTheCutoffRelease_OrEveryModuleStartsAtIt()
    {
        // Positive control of the expectation itself: the query reads the START release of each module
        // version. If it silently returned the cutoff release for every module (the previous rule),
        // every expected code would end with the cutoff release; the data must show otherwise.
        var expected = Expected();
        Assert.NotEmpty(expected);
        Assert.Contains(expected.Keys, k => !k.EndsWith(" " + CutoffReleaseCode, StringComparison.Ordinal));
    }

    [DataFact]
    public void MTaxonomy_Codes_AreTheDistinctFrameworkAndStartReleaseOfTheEmittedModuleVersions()
    {
        var expected = Expected();
        var actual = Rows("SELECT TaxonomyCode FROM mTaxonomy", 1).Select(r => r[0]).ToList();

        Assert.Equal(expected.Count, actual.Count);
        Assert.Equal(
            expected.Keys.OrderBy(k => k, StringComparer.Ordinal),
            actual.OrderBy(k => k, StringComparer.Ordinal));
    }

    [DataFact]
    public void EveryModule_HangsFromTheTaxonomyOfItsFrameworkAndStartRelease()
    {
        var expected = Expected();
        var actual = Rows(
                "SELECT t.TaxonomyCode, m.ModuleCode FROM mModule m JOIN mTaxonomy t ON t.TaxonomyID = m.TaxonomyID", 2)
            .GroupBy(r => r[0], r => r[1], StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderBy(c => c, StringComparer.Ordinal).ToList(), StringComparer.Ordinal);

        var moduleRows = int.Parse(Rows("SELECT COUNT(1) FROM mModule", 1)[0][0], CultureInfo.InvariantCulture);
        Assert.Equal(expected.Values.Sum(v => v.Count), moduleRows);
        foreach (var (taxonomyCode, modules) in expected)
        {
            Assert.True(actual.TryGetValue(taxonomyCode, out var actualModules), $"Taxonomy '{taxonomyCode}' has no module in the output.");
            Assert.True(
                modules.SequenceEqual(actualModules!),
                $"Taxonomy '{taxonomyCode}': modules in Access [{string.Join(",", modules)}], in the output [{string.Join(",", actualModules!)}].");
        }

        Assert.Equal(expected.Count, actual.Count);
    }

    [DataFact]
    public void EveryModule_XBRLSchemaRef_ReleaseIsTheStartReleaseOfItsModuleVersion()
    {
        var expectedReleaseByModule = Expected()
            .SelectMany(kvp => kvp.Value.Select(module => (Module: module, Release: kvp.Key[(kvp.Key.IndexOf(' ', StringComparison.Ordinal) + 1)..], Framework: kvp.Key[..kvp.Key.IndexOf(' ', StringComparison.Ordinal)])))
            .ToDictionary(x => x.Module, x => (x.Framework, x.Release), StringComparer.Ordinal);

        var refs = Rows("SELECT ModuleCode, XBRLSchemaRef FROM mModule", 2);
        Assert.Equal(expectedReleaseByModule.Count, refs.Count);

        var mismatches = new List<string>();
        foreach (var row in refs)
        {
            var match = SchemaRefPattern.Match(row[1]);
            Assert.True(match.Success, $"{row[0]}: XBRLSchemaRef '{row[1]}' does not match the expected pattern.");
            var (framework, release) = expectedReleaseByModule[row[0]];
            if (match.Groups["release"].Value != release || match.Groups["fw"].Value != framework
                || !string.Equals(match.Groups["code"].Value, row[0], StringComparison.OrdinalIgnoreCase))
            {
                mismatches.Add($"{row[0]}: expected {framework}/{release}, got {match.Groups["fw"].Value}/{match.Groups["release"].Value}");
            }
        }

        Assert.True(mismatches.Count == 0, $"{mismatches.Count} XBRLSchemaRef values differ from the start release of their module version: {string.Join(" | ", mismatches.Take(10))}");
    }

    [DataFact]
    public void TheTemplateTree_ExistsForEveryTaxonomy_AndNoBusinessTableIsAnOrphan()
    {
        var taxonomiesWithTables = Rows("SELECT DISTINCT t.TaxonomyCode FROM mTaxonomyTable tt JOIN mTaxonomy t ON t.TaxonomyID = tt.TaxonomyID", 1)
            .Select(r => r[0]).ToHashSet(StringComparer.Ordinal);
        var taxonomiesWithGroups = Rows(
                "SELECT DISTINCT t.TaxonomyCode FROM mTemplateOrTable x JOIN mTaxonomy t ON t.TaxonomyID = x.TaxonomyID WHERE x.TemplateOrTableType = 'TableGroup'", 1)
            .Select(r => r[0]).ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(taxonomiesWithTables);
        Assert.True(
            taxonomiesWithTables.IsSubsetOf(taxonomiesWithGroups),
            $"Taxonomies with tables but without TableGroup nodes: {string.Join(", ", taxonomiesWithTables.Except(taxonomiesWithGroups))}");

        var orphans = int.Parse(
            Rows("SELECT COUNT(1) FROM mTemplateOrTable WHERE TemplateOrTableType = 'BusinessTable' AND ParentTemplateOrTableID = 0", 1)[0][0],
            CultureInfo.InvariantCulture);
        Assert.True(orphans == 0, $"{orphans} BusinessTable rows are orphans (Parent = 0).");
    }

    [DataFact]
    public void PlaneA_AMod10_RunsAndPasses_OverARealUniverse()
    {
        var result = ValidatorRunCache.Run(GeneratedDatabasePath, referencePath: null);
        var aMod10 = Assert.Single(result.Report.Checks, c => c.Id == "A-MOD-10");

        Assert.Equal("critical", aMod10.Layer);
        Assert.Equal("pass", aMod10.Status);
        Assert.Equal(0, aMod10.Failed);
        // Universe = taxonomies + modules of the output (a zero over nothing proves nothing).
        var universe = Expected().Count + Expected().Values.Sum(v => v.Count);
        Assert.True(aMod10.Examined == universe, $"A-MOD-10 examined {aMod10.Examined}, expected {universe} (taxonomies + modules derived from Access).");
    }
}

[Collection("Dpm2042_1ValidateSuite")]
[Trait("Tier", "RealData")]
public sealed class Dpm2042_1TaxonomyPerStartReleaseTests(Dpm2042_1ValidateFixture fixture) : TaxonomyPerStartReleaseDataTestsBase
{
    protected override string Accdb => RepoPaths.AccessDpm20DatabasePath;

    protected override string CutoffReleaseCode => fixture.DerivedCutoffReleaseCode;

    protected override string GeneratedDatabasePath => fixture.GeneratedDatabasePath;

    /// <summary>The only current module version of this database that starts after 4.2 is the FINREP9DP one, read from Access.</summary>
    [DataFact]
    public void OnlyTheModuleVersionsThatStartInTheCutoffRelease_GiveACutoffTaxonomy()
    {
        var startingAtCutoff = AccessCensus.Rows(
            Accdb,
            "SELECT mv.[Code] FROM [ModuleVersion] mv INNER JOIN [Release] r ON r.[ReleaseID] = mv.[StartReleaseID] "
            + $"WHERE r.[Code] = '{CutoffReleaseCode}' AND mv.[EndReleaseID] IS NULL");
        Assert.NotEmpty(startingAtCutoff);

        var inOutput = Rows($"SELECT m.ModuleCode FROM mModule m JOIN mTaxonomy t ON t.TaxonomyID = m.TaxonomyID WHERE t.TaxonomyCode LIKE '% {CutoffReleaseCode}'", 1)
            .Select(r => r[0]).OrderBy(c => c, StringComparer.Ordinal).ToList();
        var expected = startingAtCutoff
            .Select(r => Convert.ToString(r[0], CultureInfo.InvariantCulture)!.ToUpperInvariant())
            .Where(code => Expected().Values.Any(v => v.Contains(code)))
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(expected, inOutput);
    }
}

[Collection("Dpm2043ValidateSuite")]
[Trait("Tier", "RealData")]
public sealed class Dpm2043TaxonomyPerStartReleaseTests(Dpm2043ValidateFixture fixture) : TaxonomyPerStartReleaseDataTestsBase
{
    protected override string Accdb => RepoPaths.AccessDpm20Release43DatabasePath;

    protected override string CutoffReleaseCode => ReleasePins.Dpm20Natural43.Release;

    protected override string GeneratedDatabasePath => fixture.GeneratedDatabasePath;
}
