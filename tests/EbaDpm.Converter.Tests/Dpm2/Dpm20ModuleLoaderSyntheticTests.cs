using EbaDpm.Converter.Core.Access;
using EbaDpm.Converter.Core.Access.Dpm20;
using EbaDpm.Converter.Core.Mapping.Dpm20;
using EbaDpm.Converter.Core.Sqlite;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Tests.Dpm2;

/// <summary>
/// Synthetic tests of <see cref="Dpm20ModuleLoader.Load"/> through
/// <see cref="InMemoryModuleSource"/>: no Access, no data files. Rule under test:
/// docs/mapping-dpm2.md, section "Modules" (<c>mModule</c>: current modules excluding DORA and
/// document modules, <c>ModuleCode</c> in upper case; <c>mConceptualModule</c> mirrors it). The
/// <see cref="Dpm20StructureLoader.Result"/> is built by hand and empty: this test does not
/// exercise <c>mModuleBusinessTemplate</c>.
/// </summary>
public sealed class Dpm20ModuleLoaderSyntheticTests : IDisposable
{
    private readonly string _tempDirectory;

    public Dpm20ModuleLoaderSyntheticTests()
    {
        _tempDirectory = Path.Join(Path.GetTempPath(), $"EbaDpm.ModSynth_{Environment.ProcessId}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort.
        }
    }

    [Fact]
    public void Modules_ExcludeDoraAndDocumentModules_AndUpperCaseTheCode()
    {
        var source = new InMemoryModuleSource { CutoffReleaseCode = "R42" };

        Dpm20FrameworkRow[] frameworks = [new(1, "COREP", "Corep"), new(2, "DORA", "Dora")];
        Dpm20ModuleRow[] modules =
        [
            new(10, 1, false), // regular module
            new(11, 1, true), // document module (link to a PDF)
            new(12, 2, false), // DORA module
        ];
        Dpm20ModuleVersionRow[] moduleVersions =
        [
            new(100, 10, "corep_lr", "Leverage ratio", 1, null),
            new(101, 11, "p3_docs", "Documents", 1, null),
            new(102, 12, "dora_ict", "ICT register", 1, null),
        ];
        Dpm20ReleaseRow[] releases = [new(1, "R42", null, null, null, true)];
        AccessTaxonomyRow[] taxonomies =
        [
            new(1, 1, "COREP_R42", null, "corep", null, null, "R42", null),
            new(2, 2, "DORA_R42", null, "dora", null, null, "R42", null),
        ];
        var structure = new Dpm20StructureLoader.Result(
            0, 0, 0, 0, 0, 0, true, [],
            new Dictionary<int, int>(),
            new HashSet<int>(),
            new Dictionary<int, IReadOnlySet<int>>());

        var path = Path.Join(_tempDirectory, "out.db");
        SchemaCreator.Create(path, overwrite: false);

        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        // The real pipeline loads with foreign keys off (SchemaCreator), and the loader writes
        // mModule before mConceptualModule, so enforcing them during the load is impossible.
        // Integrity is checked after the load instead (PRAGMA foreign_key_check below).
        Execute(connection, "PRAGMA foreign_keys = OFF");
        Execute(connection, "INSERT INTO mTaxonomy (TaxonomyID, TaxonomyCode) VALUES (1, 'COREP_R42')");
        Execute(connection, "INSERT INTO mTaxonomy (TaxonomyID, TaxonomyCode) VALUES (2, 'DORA_R42')");

        var result = Dpm20ModuleLoader.Load(
            source, connection, modules, moduleVersions, [], [], frameworks, releases, taxonomies, structure);

        Assert.Empty(result.UnresolvedCases);
        Assert.Equal(1, result.ModuleRows);
        Assert.Equal(1, result.ConceptualModuleRows);

        Assert.Empty(Query(connection, "SELECT \"table\" FROM pragma_foreign_key_check"));
        Assert.Equal(["COREP_LR"], Query(connection, "SELECT ModuleCode FROM mModule ORDER BY ModuleCode"));
        Assert.Equal(["COREP_LR"], Query(
            connection, "SELECT ConceptualModuleCode FROM mConceptualModule ORDER BY ConceptualModuleCode"));
    }

    /// <summary>
    /// Issue 66: a module version hangs from the taxonomy (framework, StartRelease) and its
    /// XBRLSchemaRef carries the start release, NOT the cutoff release. Two modules of one framework
    /// with different start releases land in two taxonomies.
    /// </summary>
    [Fact]
    public void Modules_HangFromTheTaxonomyOfTheirStartRelease_AndTheSchemaRefCarriesIt()
    {
        var source = new InMemoryModuleSource { CutoffReleaseCode = "4.2" };

        Dpm20FrameworkRow[] frameworks = [new(1, "COREP", "Corep")];
        Dpm20ModuleRow[] modules = [new(10, 1, false), new(11, 1, false)];
        Dpm20ModuleVersionRow[] moduleVersions =
        [
            new(100, 10, "corep_lr", "Leverage ratio", 1, null), // starts in 4.0, still current at 4.2
            new(101, 11, "corep_of", "Own funds", 2, null), // starts in 4.2
        ];
        Dpm20ReleaseRow[] releases = [new(1, "4.0", null, null, null, false), new(2, "4.2", null, null, null, true)];
        AccessTaxonomyRow[] taxonomies =
        [
            new(1, 1, "corep 4.0", null, "corep", null, null, "4.0", null),
            new(2, 1, "corep 4.2", null, "corep", null, null, "4.2", null),
        ];
        var structure = new Dpm20StructureLoader.Result(
            0, 0, 0, 0, 0, 0, true, [],
            new Dictionary<int, int>(),
            new HashSet<int>(),
            new Dictionary<int, IReadOnlySet<int>>());

        var path = Path.Join(_tempDirectory, "start-release.db");
        SchemaCreator.Create(path, overwrite: false);

        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        Execute(connection, "PRAGMA foreign_keys = OFF");
        Execute(connection, "INSERT INTO mTaxonomy (TaxonomyID, TaxonomyCode) VALUES (1, 'corep 4.0'), (2, 'corep 4.2')");

        var result = Dpm20ModuleLoader.Load(
            source, connection, modules, moduleVersions, [], [], frameworks, releases, taxonomies, structure);

        Assert.Empty(result.UnresolvedCases);
        Assert.Equal(2, result.ModuleRows);
        Assert.Equal(
            ["COREP_LR|corep 4.0|http://www.eba.europa.eu/eu/fr/xbrl/crr/fws/corep/4.0/mod/corep_lr.xsd",
             "COREP_OF|corep 4.2|http://www.eba.europa.eu/eu/fr/xbrl/crr/fws/corep/4.2/mod/corep_of.xsd"],
            Query(
                connection,
                "SELECT m.ModuleCode || '|' || t.TaxonomyCode || '|' || m.XBRLSchemaRef FROM mModule m "
                + "JOIN mTaxonomy t ON t.TaxonomyID = m.TaxonomyID ORDER BY m.ModuleCode"));
    }

    /// <summary>A module whose start-release taxonomy is not in the current selection is not emitted, and says why.</summary>
    [Fact]
    public void AModule_WhoseStartReleaseTaxonomyIsNotSelected_IsNotEmitted_AndIsReported()
    {
        var source = new InMemoryModuleSource { CutoffReleaseCode = "4.2" };

        Dpm20FrameworkRow[] frameworks = [new(1, "COREP", "Corep")];
        Dpm20ModuleRow[] modules = [new(10, 1, false), new(11, 1, false)];
        Dpm20ModuleVersionRow[] moduleVersions =
        [
            new(100, 10, "corep_lr", "Leverage ratio", 1, null),
            new(101, 11, "corep_of", "Own funds", 2, null),
        ];
        Dpm20ReleaseRow[] releases = [new(1, "4.0", null, null, null, false), new(2, "4.2", null, null, null, true)];
        // Only the 4.2 taxonomy is selected (for instance --releases 4.2).
        AccessTaxonomyRow[] taxonomies = [new(2, 1, "corep 4.2", null, "corep", null, null, "4.2", null)];
        var structure = new Dpm20StructureLoader.Result(
            0, 0, 0, 0, 0, 0, true, [],
            new Dictionary<int, int>(),
            new HashSet<int>(),
            new Dictionary<int, IReadOnlySet<int>>());

        var path = Path.Join(_tempDirectory, "partial-selection.db");
        SchemaCreator.Create(path, overwrite: false);

        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        Execute(connection, "PRAGMA foreign_keys = OFF");
        Execute(connection, "INSERT INTO mTaxonomy (TaxonomyID, TaxonomyCode) VALUES (2, 'corep 4.2')");

        var result = Dpm20ModuleLoader.Load(
            source, connection, modules, moduleVersions, [], [], frameworks, releases, taxonomies, structure);

        Assert.Equal(1, result.ModuleRows);
        Assert.Equal(["COREP_OF"], Query(connection, "SELECT ModuleCode FROM mModule"));
        Assert.Contains(result.UnresolvedCases, c => c.Contains("corep_lr", StringComparison.Ordinal) && c.Contains("corep 4.0", StringComparison.Ordinal));
    }

    private static List<string> Query(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var values = new List<string>();
        while (reader.Read())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
