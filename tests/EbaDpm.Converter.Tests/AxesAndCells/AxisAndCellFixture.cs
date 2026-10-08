using EbaDpm.Converter.Core.Access;
using EbaDpm.Converter.Core.Mapping;
using EbaDpm.Converter.Core.Sqlite;
using EbaDpm.Converter.Tests.Schema;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Tests.AxesAndCells;

/// <summary>
/// Collection fixture (xUnit <see cref="ICollectionFixture{TFixture}"/>): runs ONCE, for the whole
/// "AxesAndCells" collection, the axis and cell conversion (<see cref="AxisAndCellLoader"/>) over
/// the SAME EIGHT taxonomies as <c>ModulesFixture</c>: the four of the primary 3.2 reference
/// (<c>AE 3.2</c>, <c>COREP 3.2</c>, <c>GSII 3.2</c>, <c>IF 3.2</c>) plus the four comparable ones
/// of the subsidiary 4.0 reference (<c>COREP 4.0</c>, <c>DORA 4.0</c>, <c>IF 4.0</c>,
/// <c>MICA 4.0</c>).
///
/// This universe reproduces EXACTLY the measured figures: 217/217 tables, 522 axes and 7,615
/// ordinates against 3.2, and 150/151 tables, 368 axes and 4,220 ordinates against 4.0 (the
/// missing 4.0 table is <c>corep 3.4</c>, absent from the Access v4.1; that is not a failure).
///
/// Loading chain on the SAME connection, in the order required by <see cref="AxisAndCellLoader"/>
/// (it needs <c>TableIdByTableVId</c> from <see cref="TemplateOrTableLoader"/>):
/// <see cref="SkeletonLoader"/> -> <see cref="DictionaryLoader"/> -> <see cref="TemplateOrTableLoader"/>
/// -> <see cref="AxisAndCellLoader"/>. <see cref="ModuleLoader"/> is NOT run here: the axis and cell
/// loader does not need it and skipping it shortens the run.
///
/// Leaves open (read-only except the generated one):
/// - <see cref="GeneratedConnection"/>: the output database.
/// - <see cref="Reference32Connection"/> (primary) and <see cref="Reference40Connection"/>
///   (subsidiary, by containment).
/// - <see cref="AccessReader"/>: connection to the source Access database, for the internal
///   invariants and the per-table censuses that need no reference.
///
/// When the EBA data files are not available the constructor does nothing and every member keeps
/// its default value; the data-dependent tests are skipped in that case.
/// </summary>
public sealed class AxisAndCellFixture : IDisposable
{
    /// <summary>Same universe as <c>ModulesFixture.TaxonomyCodes</c>, deliberately reused here.</summary>
    public static readonly string[] TaxonomyCodes =
    [
        "AE 3.2", "COREP 3.2", "GSII 3.2", "IF 3.2",
        "COREP 4.0", "DORA 4.0", "IF 4.0", "MICA 4.0",
    ];

    public string TempDirectory { get; }
    public string GeneratedDatabasePath { get; }

    public SqliteConnection GeneratedConnection { get; }
    public SqliteConnection Reference32Connection { get; }
    public SqliteConnection Reference40Connection { get; }

    /// <summary>
    /// 4.2 reference (schema contract), also opened here because the three-part bracket census
    /// of the data point signatures needs it in addition to the 3.2 and 4.0 references.
    /// </summary>
    public SqliteConnection Reference42Connection { get; }

    public Dpm10AccessReader AccessReader { get; }

    public IReadOnlyList<AccessTaxonomyRow> AllTaxonomies { get; }
    public IReadOnlyList<AccessTaxonomyRow> SelectedTaxonomies { get; }

    public SkeletonLoader.Result Skeleton { get; }
    public DictionaryLoader.Result DictionaryResult { get; }
    public TemplateOrTableLoader.Result TemplateOrTableResult { get; }
    public AxisAndCellLoader.Result AxisAndCellResult { get; }

    public AxisAndCellFixture()
    {
        // Without the EBA data files every test of the collection is skipped; leave all members
        // at their default values.
        if (!RepoPaths.IsDataAvailable)
        {
            TempDirectory = null!;
            GeneratedDatabasePath = null!;
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
            AxisAndCellResult = null!;
            return;
        }

        RepoPaths.EnsureAccessDatabaseExists();
        RepoPaths.EnsureReference32DatabaseExists();
        RepoPaths.EnsureReference40DatabaseExists();
        RepoPaths.EnsureReferenceDatabaseExists();

        TempDirectory = Path.Combine(Path.GetTempPath(), $"EbaDpm.AxesAndCellsTests_{Environment.ProcessId}_{Guid.NewGuid():N}");
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
        AxisAndCellResult = AxisAndCellLoader.Load(AccessReader, GeneratedConnection, TemplateOrTableResult.TableIdByTableVId);

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
        AccessReader?.Dispose();
        GeneratedConnection?.Dispose();
        Reference32Connection?.Dispose();
        Reference40Connection?.Dispose();
        Reference42Connection?.Dispose();
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
/// xUnit collection definition: groups all the axis and cell tests so that they share a single
/// run of <see cref="AxisAndCellFixture"/>.
/// </summary>
[CollectionDefinition("AxesAndCells")]
public sealed class AxisAndCellCollection : ICollectionFixture<AxisAndCellFixture>
{
}
