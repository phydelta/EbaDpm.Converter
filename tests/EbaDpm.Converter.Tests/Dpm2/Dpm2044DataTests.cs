using System.Globalization;
using EbaDpm.Converter.Cli;
using EbaDpm.Converter.Core.Access.Dpm20;
using EbaDpm.Converter.Core.Mapping.Dpm20;
using EbaDpm.Converter.Tests.Infrastructure;
using EbaDpm.Converter.Tests.Schema;
using EbaDpm.Converter.Tests.Support;
using Xunit.Abstractions;

namespace EbaDpm.Converter.Tests.Dpm2;

/// <summary>
/// The 4.4 publication (#67) over <c>Data/DPM2 Database_v 4_4_20261006.accdb</c>: the release
/// catalogue and the module versions that start in 4.4, each expectation queried independently from
/// Access with OLEDB (<see cref="AccessCensus"/>), never pinned. The figures of the issue (13 module
/// versions in 6 taxonomies, 4.3.0.1 empty) are NOT asserted as literals: the tests derive them.
/// </summary>
[Trait("Tier", "RealData")]
public sealed class Dpm2044ReleaseCatalogRealDataTests
{
    private static string Accdb => RepoPaths.AccessDpm20Release44DatabasePath;

    /// <summary>(release code, ReleaseID, non-document module versions starting in it), in ReleaseID order.</summary>
    private static List<(string Code, int Id, long ExportableModuleVersions)> ReleasesFromAccess()
    {
        RepoPaths.EnsureAccessDpm20Release44DatabaseExists();
        var releases = AccessCensus.IntByCode(Accdb, "SELECT [Code], [ReleaseID] FROM [Release]")
            .OrderBy(kv => kv.Value)
            .Select(kv => (kv.Key, kv.Value, AccessCensus.Scalar(
                Accdb,
                "SELECT COUNT(1) FROM [ModuleVersion] mv INNER JOIN [Module] m ON m.[ModuleID] = mv.[ModuleID] "
                + $"WHERE mv.[StartReleaseID] = {kv.Value} AND m.[isDocumentModule] = 0")))
            .ToList();
        Assert.NotEmpty(releases);
        return releases;
    }

    [DataFact]
    public void ReleaseCatalog_EqualsTheIndependentCountOfModuleVersionsStartingInEachRelease()
    {
        var expected = ReleasesFromAccess();

        using var reader = new Dpm20AccessReader(Accdb);
        reader.Open();
        var catalog = Dpm20ReleaseCatalog.Build(reader);

        Assert.Equal(
            expected.Select(e => (e.Id, e.Code, (int)e.ExportableModuleVersions)),
            catalog.Select(c => (c.ReleaseId, c.Code, c.ExportableModuleVersions)));

        // Positive control: the independent count discriminates (some releases publish module versions,
        // some do not), so equality is not two empty lists agreeing.
        Assert.Contains(expected, e => e.ExportableModuleVersions > 0);
        Assert.Contains(expected, e => e.ExportableModuleVersions == 0);
        Assert.Equal(expected.Select(e => e.ExportableModuleVersions == 0), catalog.Select(c => c.IsEmpty));
    }

    [DataFact]
    public void Release44_IsTheLastReleaseThatPublishesModuleVersions_And4301IsSkipped_MeasuredOn_2026_11_20()
    {
        var expected = ReleasesFromAccess();

        // Scoped facts of THIS file (measured 2026-11-20 on 'DPM2 Database_v 4_4_20261006.accdb'), stated by
        // the issue and not derivable without restating the rule: a later publication re-measures them.
        var last = expected.Last();
        Assert.Equal(RepoPaths.Release44Code, last.Code);
        Assert.True(last.ExportableModuleVersions > 0, "Release 4.4 publishes no module version.");
        var withoutModules = expected.Where(e => e.ExportableModuleVersions == 0).Select(e => e.Code).ToList();
        Assert.Contains("4.3.0.1", withoutModules);

        using var reader = new Dpm20AccessReader(Accdb);
        reader.Open();
        Assert.Equal(RepoPaths.Release44Code, reader.CutoffReleaseCode);
        Assert.False(reader.CutoffReleaseRequested);
        Assert.Equal(
            RepoPaths.Release44Code,
            Dpm20ReleaseCatalog.Build(reader).Last(r => !r.IsEmpty).Code);
    }

    [DataFact]
    public void ModuleVersionsStartingIn44_EqualTheTaxonomyModulesDerivedByTheOtherIndependentQuery()
    {
        var release = ReleasesFromAccess().Single(r => r.Code == RepoPaths.Release44Code);

        // Query 1: the module versions that START in 4.4 (every one is in force at 4.4).
        var starting = AccessCensus.Rows(
                Accdb,
                "SELECT mv.[Code] FROM [ModuleVersion] mv INNER JOIN [Module] m ON m.[ModuleID] = mv.[ModuleID] "
                + $"WHERE mv.[StartReleaseID] = {release.Id} AND m.[isDocumentModule] = 0")
            .Select(r => Convert.ToString(r[0], CultureInfo.InvariantCulture)!.ToUpperInvariant())
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToList();

        // Query 2: the taxonomies of the 4.4 file (framework + start release), grouped differently.
        var byTaxonomy = AccessCensus.OfRelease(
            AccessCensus.EmittedModuleCodesByTaxonomy(Accdb, RepoPaths.Release44Code), RepoPaths.Release44Code);
        var fromTaxonomies = byTaxonomy.Values.SelectMany(v => v).OrderBy(c => c, StringComparer.Ordinal).ToList();

        Assert.NotEmpty(starting);
        Assert.Equal(starting, fromTaxonomies);
        Assert.Equal(release.ExportableModuleVersions, starting.Count);
        Assert.All(byTaxonomy.Keys, k => Assert.EndsWith(" " + RepoPaths.Release44Code, k, StringComparison.Ordinal));
    }

    [DataFact]
    public void ListTaxonomies_ForRelease44_PrintsTheIndependentlyDerivedTaxonomies()
    {
        RepoPaths.EnsureAccessDpm20Release44DatabaseExists();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = CliRunner.Run(
            ["--list-taxonomies", "--source", Accdb, "--release", RepoPaths.Release44Code], stdout, stderr);

        Assert.Equal(CliRunner.ExitOk, exitCode);
        Assert.Empty(stderr.ToString());
        var expected = AccessCensus.OfRelease(
            AccessCensus.EmittedModuleCodesByTaxonomy(Accdb, RepoPaths.Release44Code), RepoPaths.Release44Code);
        Assert.NotEmpty(expected);
        Assert.Contains($"Total: {expected.Count} taxonomies.", stdout.ToString(), StringComparison.Ordinal);
        foreach (var code in expected.Keys)
        {
            Assert.Contains(code, stdout.ToString(), StringComparison.Ordinal);
        }
    }
}

/// <summary>Conversion of the 4.4 database with <c>--release 4.4</c> and plane A (#67).</summary>
[Collection("Dpm2044ValidateSuite")]
[Trait("Tier", "RealData")]
public sealed class Dpm2044ConversionTests(Dpm2044ValidateFixture fixture, ITestOutputHelper output)
{
    [DataFact]
    public void Release44_ExitsOk_AndTheLogShowsTheRelease()
    {
        Assert.True(fixture.ExitCode == 0, $"exit={fixture.ExitCode}; stderr: {fixture.Stderr}");
        Assert.Contains("Release: 4.4.", fixture.Stdout, StringComparison.Ordinal);
    }

    [DataFact]
    public void Release44_PlaneA_FinishesCleanAndNoCriticalFails()
    {
        Assert.Equal(0, fixture.ExitCode);
        var result = ValidatorRunCache.Run(fixture.GeneratedDatabasePath, referencePath: null);

        var critical = result.Report.Checks.Where(c => c.Layer == "critical").ToList();
        output.WriteLine(
            $"plane A: critical checks={critical.Count}, failing={critical.Count(c => c.Status == "fail")}, " +
            $"StaleExceptions={result.Report.Summary.StaleExceptions}, exit={result.ExitCode}");
        InvariantChecksAssertions.AssertNoCriticalFailures(result.Report, "DPM 2.0 4.4");
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(0, result.Report.Summary.Critical.Failed);
        Assert.Equal(0, result.Report.Summary.StaleExceptions);
        Assert.True(InvariantChecksAssertions.NewInvariantChecks(result.Report).Count > 0, "DPM 2.0 4.4: no 'I-*' check appeared in the report.");
    }

    [DataFact]
    public void Release44_TheOutputIsOneRelease_AndItsCurrentReleaseIs44()
    {
        using var c = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = fixture.GeneratedDatabasePath, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT ReleaseCode FROM mRelease WHERE IsCurrent = 1";
        using var r = cmd.ExecuteReader();
        var current = new List<string>();
        while (r.Read())
        {
            current.Add(r.GetString(0));
        }

        Assert.Equal([RepoPaths.Release44Code], current);
    }
}

/// <summary>#66 / #73 taxonomy derivation per start release, on the 4.4 file (<c>--release 4.4</c>).</summary>
[Collection("Dpm2044ValidateSuite")]
[Trait("Tier", "RealData")]
public sealed class Dpm2044TaxonomyPerStartReleaseTests(Dpm2044ValidateFixture fixture) : TaxonomyPerStartReleaseDataTestsBase
{
    protected override string Accdb => RepoPaths.AccessDpm20Release44DatabasePath;

    protected override string CutoffReleaseCode => RepoPaths.Release44Code;

    protected override string GeneratedDatabasePath => fixture.GeneratedDatabasePath;
}
