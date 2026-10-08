using EbaDpm.Converter.Core.Access;
using EbaDpm.Converter.Core.Mapping;
using EbaDpm.Converter.Core.Sqlite;
using EbaDpm.Converter.Tests.Dictionary;
using EbaDpm.Converter.Tests.Schema;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Tests.Modules;

/// <summary>
/// Collection fixture (xUnit <see cref="ICollectionFixture{TFixture}"/>): runs ONCE, for the whole
/// "Modules" collection, the module conversion (<see cref="ModuleLoader"/>) over the FOUR
/// taxonomies of the primary 3.2 reference: <c>AE 3.2</c>, <c>COREP 3.2</c>, <c>GSII 3.2</c> and
/// <c>IF 3.2</c>, plus the four of the 4.0 reference. The module tests need the complete census of
/// modules and <c>mModuleBusinessTemplate</c> rows of the 3.2 working universe, which
/// <see cref="Dictionary.DictionaryFixture"/> does not cover because it only converts
/// <c>COREP 3.2</c>.
///
/// Loading chain on the SAME connection, in the order required by <see cref="ModuleLoader"/>
/// (it needs <c>mTemplateOrTable</c> already written in order to resolve the L1 node of each
/// <c>BusinessTemplateID</c>): <see cref="SkeletonLoader"/> then <see cref="DictionaryLoader"/>
/// then <see cref="TemplateOrTableLoader"/> then <see cref="ModuleLoader"/>.
///
/// Leaves open, read-only except for the generated one:
/// - <see cref="GeneratedConnection"/>: the output database.
/// - <see cref="Reference32Connection"/> (primary) and <see cref="Reference40Connection"/>
///   (secondary).
/// - <see cref="AccessReader"/>: connection to the source Access database.
///
/// When the data directory is not available the fixture does nothing (the tests are skipped).
/// </summary>
public sealed class ModulesFixture : IDisposable
{
    /// <summary>
    /// The four taxonomies of the primary 3.2 reference (13 modules / 35 rows of
    /// <c>mModuleBusinessTemplate</c>), PLUS the four of the secondary 4.0 reference (5 modules /
    /// 28 rows). All eight are converted in the same run so that both references can be
    /// validated without duplicating the Access conversion.
    /// </summary>
    public static readonly string[] TaxonomyCodes =
    [
        "AE 3.2", "COREP 3.2", "GSII 3.2", "IF 3.2",
        "COREP 4.0", "DORA 4.0", "IF 4.0", "MICA 4.0",
    ];

    public string TempDirectory { get; } = null!;
    public string GeneratedDatabasePath { get; } = null!;

    public SqliteConnection GeneratedConnection { get; } = null!;
    public SqliteConnection Reference32Connection { get; } = null!;
    public SqliteConnection Reference40Connection { get; } = null!;

    public Dpm10AccessReader AccessReader { get; } = null!;

    public IReadOnlyList<AccessTaxonomyRow> AllTaxonomies { get; } = null!;
    public IReadOnlyList<AccessTaxonomyRow> SelectedTaxonomies { get; } = null!;

    public SkeletonLoader.Result Skeleton { get; } = null!;
    public DictionaryLoader.Result DictionaryResult { get; } = null!;
    public TemplateOrTableLoader.Result TemplateOrTableResult { get; } = null!;
    public ModuleLoader.Result ModuleResult { get; } = null!;

    public ModulesFixture()
    {
        if (!RepoPaths.IsDataAvailable)
        {
            return;
        }

        RepoPaths.EnsureAccessDatabaseExists();
        RepoPaths.EnsureReference32DatabaseExists();
        RepoPaths.EnsureReference40DatabaseExists();

        TempDirectory = Path.Combine(Path.GetTempPath(), $"EbaDpm.ModulesTests_{Environment.ProcessId}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(TempDirectory);
        GeneratedDatabasePath = Path.Combine(TempDirectory, "generated.db");

        SchemaCreator.Create(GeneratedDatabasePath, overwrite: false);

        AccessReader = new Dpm10AccessReader(RepoPaths.AccessDatabasePath);
        AccessReader.Open();

        AllTaxonomies = AccessReader.ReadTaxonomies().ToList();
        var request = new TaxonomySelectionRequest(TaxonomyCodes: TaxonomyCodes, TaxonomyKeys: null, ReleaseCodes: null, All: false);
        SelectedTaxonomies = TaxonomySelector.Resolve(AllTaxonomies, request);

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = GeneratedDatabasePath,
            Mode = SqliteOpenMode.ReadWrite,
        }.ToString();

        GeneratedConnection = new SqliteConnection(connectionString);
        GeneratedConnection.Open();

        Skeleton = SkeletonLoader.Load(AccessReader, GeneratedConnection, AllTaxonomies, SelectedTaxonomies);
        DictionaryResult = DictionaryLoader.Load(AccessReader, GeneratedConnection, AllTaxonomies, SelectedTaxonomies);
        TemplateOrTableResult = TemplateOrTableLoader.Load(AccessReader, GeneratedConnection, SelectedTaxonomies);
        ModuleResult = ModuleLoader.Load(AccessReader, GeneratedConnection, SelectedTaxonomies);

        Reference32Connection = OpenReadOnly(RepoPaths.Reference32Path);
        Reference40Connection = OpenReadOnly(RepoPaths.Reference40Path);
    }

    private static SqliteConnection OpenReadOnly(string path)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString();

        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }

    public void Dispose()
    {
        AccessReader?.Dispose();
        GeneratedConnection?.Dispose();
        Reference32Connection?.Dispose();
        Reference40Connection?.Dispose();
        SqliteConnection.ClearAllPools();

        try
        {
            if (TempDirectory is not null && Directory.Exists(TempDirectory))
            {
                Directory.Delete(TempDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort: a late SQLite handle must not bring down the test suite.
        }
    }
}

/// <summary>
/// xUnit collection definition: groups all the module tests so that they share a single run of
/// <see cref="ModulesFixture"/> (converting a 740 MB Access database is not free, and here four
/// or more taxonomies are converted instead of one).
/// </summary>
[CollectionDefinition("Modules")]
public sealed class ModulesCollection : ICollectionFixture<ModulesFixture>
{
}
