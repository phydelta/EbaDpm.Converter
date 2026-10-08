using EbaDpm.Converter.Core.Access;
using EbaDpm.Converter.Core.Mapping;
using EbaDpm.Converter.Core.Sqlite;
using EbaDpm.Converter.Tests.Schema;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Tests.Dictionary;

/// <summary>
/// Collection fixture (xUnit <see cref="ICollectionFixture{TFixture}"/>): runs the reference
/// conversion ONCE for the whole "Dictionary" collection, extended with
/// <see cref="TemplateOrTableLoader"/>:
/// <c>--source "Data/DPM 1.0 Database_v4_1_20250709.accdb" --output &lt;db&gt; --taxonomies "COREP 3.2" --overwrite</c>.
///
/// It leaves open, read-only except for the generated database:
/// - <see cref="GeneratedConnection"/>: the output database (skeleton + dictionary +
///   template/table tree).
/// - <see cref="Reference32Connection"/>, <see cref="Reference40Connection"/>, <see cref="Reference42Connection"/>:
///   the three reference databases used for regression comparison.
/// - <see cref="AccessReader"/>: connection to the source Access database, for row-by-row
///   comparisons against what was emitted.
///
/// All tests only read; they share this single generation without introducing dependencies
/// between them.
///
/// When the EBA data directory is not available the constructor does nothing (all members stay
/// default) because every test using this fixture is skipped.
/// </summary>
public sealed class DictionaryFixture : IDisposable
{
    /// <summary>The taxonomy of the reference run.</summary>
    public const string TaxonomyCode = "COREP 3.2";

    public string TempDirectory { get; }
    public string GeneratedDatabasePath { get; }

    public SqliteConnection GeneratedConnection { get; }
    public SqliteConnection Reference32Connection { get; }
    public SqliteConnection Reference40Connection { get; }
    public SqliteConnection Reference42Connection { get; }

    public Dpm10AccessReader AccessReader { get; }

    public IReadOnlyList<AccessTaxonomyRow> AllTaxonomies { get; }
    public IReadOnlyList<AccessTaxonomyRow> SelectedTaxonomies { get; }

    public SkeletonLoader.Result Skeleton { get; }
    public DictionaryLoader.Result DictionaryResult { get; }
    public TemplateOrTableLoader.Result TemplateOrTableResult { get; }

    public DictionaryFixture()
    {
        if (!RepoPaths.IsDataAvailable)
        {
            TempDirectory = string.Empty;
            GeneratedDatabasePath = string.Empty;
            GeneratedConnection = null!;
            Reference32Connection = null!;
            Reference40Connection = null!;
            Reference42Connection = null!;
            AccessReader = null!;
            AllTaxonomies = null!;
            SelectedTaxonomies = null!;
            Skeleton = null!;
            DictionaryResult = null!;
            TemplateOrTableResult = null!;
            return;
        }

        RepoPaths.EnsureAccessDatabaseExists();
        RepoPaths.EnsureReference32DatabaseExists();
        RepoPaths.EnsureReference40DatabaseExists();
        RepoPaths.EnsureReferenceDatabaseExists();

        TempDirectory = Path.Combine(Path.GetTempPath(), $"EbaDpm.DictionaryTests_{Environment.ProcessId}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(TempDirectory);
        GeneratedDatabasePath = Path.Combine(TempDirectory, "generated.db");

        SchemaCreator.Create(GeneratedDatabasePath, overwrite: false);

        AccessReader = new Dpm10AccessReader(RepoPaths.AccessDatabasePath);
        AccessReader.Open();

        AllTaxonomies = AccessReader.ReadTaxonomies().ToList();
        var request = new TaxonomySelectionRequest(TaxonomyCodes: [TaxonomyCode], TaxonomyKeys: null, ReleaseCodes: null, All: false);
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

        Reference32Connection = OpenReadOnly(RepoPaths.Reference32Path);
        Reference40Connection = OpenReadOnly(RepoPaths.Reference40Path);
        Reference42Connection = OpenReadOnly(RepoPaths.ReferenceDatabasePath);
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
        if (!RepoPaths.IsDataAvailable)
        {
            return;
        }

        AccessReader.Dispose();
        GeneratedConnection.Dispose();
        Reference32Connection.Dispose();
        Reference40Connection.Dispose();
        Reference42Connection.Dispose();
        SqliteConnection.ClearAllPools();

        try
        {
            if (Directory.Exists(TempDirectory))
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
/// xUnit collection definition: groups all the dictionary tests so that they share a single run
/// of <see cref="DictionaryFixture"/> (converting a 740 MB Access database is not free).
/// </summary>
[CollectionDefinition("Dictionary")]
public sealed class DictionaryCollection : ICollectionFixture<DictionaryFixture>
{
}
