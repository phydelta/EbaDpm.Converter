using EbaDpm.Converter.Core.Access;
using EbaDpm.Converter.Core.Mapping;
using EbaDpm.Converter.Core.Sqlite;
using EbaDpm.Converter.Tests.Schema;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Tests.All;

/// <summary>
/// Collection fixture (xUnit <see cref="ICollectionFixture{TFixture}"/>): runs a single COMPLETE
/// <c>--all</c> conversion (all 128 taxonomies of the Access database) once for the whole "All"
/// collection. It closes a blind spot: mapping defects that stayed hidden for months because the
/// rest of the suite only converted 1 or 8 of the 128 taxonomies.
///
/// Loading chain over the SAME connection, in the order the real CLI requires
/// (<see cref="EbaDpm.Converter.Cli.CliRunner"/>.<c>RunConvert</c>):
/// <see cref="SkeletonLoader"/> -> <see cref="DictionaryLoader"/> -> <see cref="TemplateOrTableLoader"/>
/// -> <see cref="AxisAndCellLoader"/> -> <see cref="ModuleLoader"/>.
///
/// Four technical traps, solved here:
/// 1. **Without auxiliary indexes, the invariant battery over <c>--all</c> does NOT FINISH**
///    (measured: still running after 10 minutes). Eight indexes are created after loading
///    (~1 s), on a throw-away test database in <c>%TEMP%</c> -- never on the product output.
/// 2. **Disk**: 143-377 MB per run. Same pattern as the other fixtures:
///    <c>Path.Combine(Path.GetTempPath(), "..._" + Guid.NewGuid())</c> + <c>Directory.Delete</c>
///    in <see cref="Dispose"/>.
/// 3. **Memory**: measured peak of about 1 GB. This fixture lives in its OWN xUnit collection
///    ("All"), which xUnit does not parallelise with the others
///    (<c>parallelizeTestCollections=false</c> in <c>xunit.runner.json</c>), so its peak never
///    overlaps with that of another fixture.
/// 4. The Access reader is disposed (<see cref="Dpm10AccessReader.Dispose"/>) as soon as it has
///    finished serving the load and the origin anomaly censuses of <c>OriginAnomalyTests</c>,
///    which also need it: it is not reopened a second time.
///
/// This fixture ALSO PRODUCES <see cref="ValidatedDatabasePath"/> -- a copy of the same DPM 1.0
/// <c>--all</c> output, taken right after the loaders finish and BEFORE reopening
/// <see cref="GeneratedConnection"/> for the auxiliary test indexes. It therefore has NO live
/// SQLite connection on top of it (a requirement of <c>Validator.Run</c>, which does
/// <c>File.Copy</c> + an exclusive <c>File.OpenRead</c> for the SHA-256) and NO <c>ix_test_*</c>
/// indexes (which are not part of the product). Copying an already written file (with
/// <c>journal_mode=OFF</c> the file is consistent as soon as the connection is closed, see
/// <c>SchemaCreator.cs</c>) is far cheaper than repeating the whole conversion. Tests that need
/// a database without an open connection live in the "All" collection and read
/// <see cref="ValidatedDatabasePath"/> instead of owning a fixture of their own.
///
/// When the EBA data directory is not available the constructor does nothing (all members stay
/// default) because every test using this fixture is skipped.
/// </summary>
public sealed class AllFixture : IDisposable
{
    public string TempDirectory { get; }
    public string GeneratedDatabasePath { get; }

    /// <summary>
    /// Copy of the same <c>--all</c> output, WITHOUT an open connection and WITHOUT the auxiliary
    /// test indexes -- see the class comment.
    /// </summary>
    public string ValidatedDatabasePath { get; }

    public SqliteConnection GeneratedConnection { get; }

    public Dpm10AccessReader AccessReader { get; }

    public IReadOnlyList<AccessTaxonomyRow> AllTaxonomies { get; }
    public IReadOnlyList<AccessTaxonomyRow> SelectedTaxonomies { get; }

    public SkeletonLoader.Result Skeleton { get; }
    public DictionaryLoader.Result DictionaryResult { get; }
    public TemplateOrTableLoader.Result TemplateOrTableResult { get; }
    public AxisAndCellLoader.Result AxisAndCellResult { get; }
    public ModuleLoader.Result ModuleResult { get; }

    public AllFixture()
    {
        if (!RepoPaths.IsDataAvailable)
        {
            TempDirectory = string.Empty;
            GeneratedDatabasePath = string.Empty;
            ValidatedDatabasePath = string.Empty;
            GeneratedConnection = null!;
            AccessReader = null!;
            AllTaxonomies = null!;
            SelectedTaxonomies = null!;
            Skeleton = null!;
            DictionaryResult = null!;
            TemplateOrTableResult = null!;
            AxisAndCellResult = null!;
            ModuleResult = null!;
            return;
        }

        RepoPaths.EnsureAccessDatabaseExists();

        TempDirectory = Path.Combine(Path.GetTempPath(), $"EbaDpm.AllTests_{Environment.ProcessId}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(TempDirectory);
        GeneratedDatabasePath = Path.Combine(TempDirectory, "generated-all.db");

        SchemaCreator.Create(GeneratedDatabasePath, overwrite: false);

        AccessReader = new Dpm10AccessReader(RepoPaths.AccessDatabasePath);
        AccessReader.Open();

        AllTaxonomies = AccessReader.ReadTaxonomies().ToList();
        var request = new TaxonomySelectionRequest(TaxonomyCodes: null, TaxonomyKeys: null, ReleaseCodes: null, All: true);
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
        ModuleResult = ModuleLoader.Load(AccessReader, GeneratedConnection, SelectedTaxonomies);

        // The "clean" copy is taken HERE: loaders finished, no auxiliary indexes yet, and BEFORE
        // reopening a connection that Validator.Run would not tolerate. The write connection is
        // closed, the file is copied (journal_mode=OFF: already consistent on disk, see
        // SchemaCreator.cs) and the connection is reopened for the rest of this fixture: one
        // conversion, two files.
        GeneratedConnection.Dispose();
        SqliteConnection.ClearAllPools();

        ValidatedDatabasePath = Path.Combine(TempDirectory, "generated-all-validated.db");
        File.Copy(GeneratedDatabasePath, ValidatedDatabasePath, overwrite: true);

        GeneratedConnection = new SqliteConnection(connectionString);
        GeneratedConnection.Open();

        CreateAuxiliaryIndexes(GeneratedConnection);
    }

    /// <summary>
    /// The 8 auxiliary indexes: the output only carries the 12 <c>sqlite_autoindex_*</c> of the
    /// composite PKs, and WITHOUT indexes on the join columns used by the invariant battery it
    /// does NOT FINISH over <c>--all</c> (measured: still running after 10 minutes). This is a
    /// throw-away test database in <c>%TEMP%</c>, never the product output, so it does not touch
    /// the destination schema contract.
    /// </summary>
    private static void CreateAuxiliaryIndexes(SqliteConnection connection)
    {
        string[] statements =
        [
            "CREATE INDEX ix_test_cellposition_ordinateid ON \"mCellPosition\" (\"OrdinateID\")",
            "CREATE INDEX ix_test_tablecell_tableid ON \"mTableCell\" (\"TableID\")",
            "CREATE INDEX ix_test_axisordinate_axisid ON \"mAxisOrdinate\" (\"AxisID\")",
            "CREATE INDEX ix_test_axisordinate_parentordinateid ON \"mAxisOrdinate\" (\"ParentOrdinateID\")",
            "CREATE INDEX ix_test_ordinatecategorisation_ordinateid ON \"mOrdinateCategorisation\" (\"OrdinateID\")",
            "CREATE INDEX ix_test_tableaxis_tableid ON \"mTableAxis\" (\"TableID\")",
            "CREATE INDEX ix_test_hierarchynode_hierarchyid ON \"mHierarchyNode\" (\"HierarchyID\")",
            "CREATE INDEX ix_test_member_domainid ON \"mMember\" (\"DomainID\")",
        ];

        using var command = connection.CreateCommand();
        foreach (var statement in statements)
        {
            command.CommandText = statement;
            command.ExecuteNonQuery();
        }
    }

    public void Dispose()
    {
        if (!RepoPaths.IsDataAvailable)
        {
            return;
        }

        AccessReader.Dispose();
        GeneratedConnection.Dispose();
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
/// xUnit collection definition: groups all tests over the <c>--all</c> conversion so that they
/// share a single run of <see cref="AllFixture"/>. It is its own collection so that its memory
/// peak does not overlap with that of the other fixtures.
/// </summary>
[CollectionDefinition("All")]
public sealed class AllCollection : ICollectionFixture<AllFixture>
{
}
