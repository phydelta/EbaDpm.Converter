using System.Data.OleDb;
using EbaDpm.Converter.Distribution.Access.Dpm20;
using EbaDpm.Converter.Distribution.Mapping.Dpm20;
using EbaDpm.Converter.Core.Sqlite;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Distribution.Tests.Dpm2;

/// <summary>
/// #73: the dictionary is read AS OF the cutoff release R. A row that starts after R does not
/// contribute; a row that started at or before R contributes even when it ended before R (there is
/// no end condition, so renamed codes stay side by side). Every test pairs the filtered read with
/// the very same fixture read at a LATER cutoff, which includes the row: that is the positive
/// control that the absence comes from the filter and not from a broken fixture.
///
/// The world (<see cref="Populate"/>), releases 1 = "1.0", 2 = "2.0", 3 = "3.0": every "early" row
/// starts in 1, every "late" row starts in 3, and R is 2 or 3. Synthetic <c>.accdb</c> over
/// ADOX/ACE (<see cref="AceFactAttribute"/>): no EBA data file.
/// </summary>
[Trait("Tier", "Synthetic")]
public sealed class Dpm20AsOfReleaseReaderTests
{
    private static void Populate(OleDbConnection c)
    {
        static void X(OleDbConnection c, string sql) => Dpm2SyntheticSource.Exec(c, sql);

        X(c, "INSERT INTO [Release] ([ReleaseID],[Code],[IsCurrent]) VALUES (1,'1.0',0)");
        X(c, "INSERT INTO [Release] ([ReleaseID],[Code],[IsCurrent]) VALUES (2,'2.0',0)");
        X(c, "INSERT INTO [Release] ([ReleaseID],[Code],[IsCurrent]) VALUES (3,'3.0',-1)");

        // Category: CreatedRelease.
        X(c, "INSERT INTO [Category] ([CategoryID],[Code],[Name],[IsEnumerated],[CreatedRelease]) VALUES (1,'EARLYCAT','e',-1,1)");
        X(c, "INSERT INTO [Category] ([CategoryID],[Code],[Name],[IsEnumerated],[CreatedRelease]) VALUES (2,'LATECAT','l',-1,3)");

        // ItemCategory: 'old-b' started in 1 and ended in 2 (history), 'd' starts exactly in 2, 'c' starts in 3.
        X(c, "INSERT INTO [ItemCategory] VALUES (1,1,'a',0,1,NULL)");
        X(c, "INSERT INTO [ItemCategory] VALUES (2,1,'old-b',0,1,2)");
        X(c, "INSERT INTO [ItemCategory] VALUES (3,1,'c',0,3,NULL)");
        X(c, "INSERT INTO [ItemCategory] VALUES (4,1,'d',0,2,NULL)");

        // SuperCategoryComposition.
        X(c, "INSERT INTO [SuperCategoryComposition] VALUES (9,1,1)");
        X(c, "INSERT INTO [SuperCategoryComposition] VALUES (9,2,3)");

        // SubCategory / SubCategoryVersion: S1 early; S2 only has a late version; S3 has an early
        // version that ended in 2 and a late one.
        X(c, "INSERT INTO [SubCategory] VALUES (1,1,'S1','s1')");
        X(c, "INSERT INTO [SubCategory] VALUES (2,1,'S2','s2')");
        X(c, "INSERT INTO [SubCategory] VALUES (3,1,'S3','s3')");
        X(c, "INSERT INTO [SubCategoryVersion] VALUES (1000,1,1,NULL)");
        X(c, "INSERT INTO [SubCategoryVersion] VALUES (1001,2,3,NULL)");
        X(c, "INSERT INTO [SubCategoryVersion] VALUES (1002,3,1,2)");
        X(c, "INSERT INTO [SubCategoryVersion] VALUES (1003,3,3,NULL)");

        // PropertyCategory: property 2 starts late; property 3 ended in 2 (history).
        X(c, "INSERT INTO [PropertyCategory] VALUES (1,1,1,NULL)");
        X(c, "INSERT INTO [PropertyCategory] VALUES (2,1,3,NULL)");
        X(c, "INSERT INTO [PropertyCategory] VALUES (3,1,1,2)");

        // Fact / key variable versions and non-key / key header versions.
        X(c, "INSERT INTO [Variable] VALUES (1,'fact')");
        X(c, "INSERT INTO [Variable] VALUES (2,'key')");
        X(c, "INSERT INTO [VariableVersion] ([VariableVID],[VariableID],[PropertyID],[StartReleaseID]) VALUES (1,1,100,1)");
        X(c, "INSERT INTO [VariableVersion] ([VariableVID],[VariableID],[PropertyID],[StartReleaseID]) VALUES (2,1,101,3)");
        X(c, "INSERT INTO [VariableVersion] ([VariableVID],[VariableID],[PropertyID],[StartReleaseID]) VALUES (3,2,200,1)");
        X(c, "INSERT INTO [VariableVersion] ([VariableVID],[VariableID],[PropertyID],[StartReleaseID]) VALUES (4,2,201,3)");
        X(c, "INSERT INTO [Header] VALUES (1,0)");
        X(c, "INSERT INTO [Header] VALUES (2,-1)");
        X(c, "INSERT INTO [HeaderVersion] ([HeaderVID],[HeaderID],[PropertyID],[StartReleaseID]) VALUES (10,1,300,1)");
        X(c, "INSERT INTO [HeaderVersion] ([HeaderVID],[HeaderID],[PropertyID],[StartReleaseID]) VALUES (11,1,301,3)");
        X(c, "INSERT INTO [HeaderVersion] ([HeaderVID],[HeaderID],[PropertyID],[StartReleaseID]) VALUES (12,2,400,1)");

        // Contexts. ContextComposition has no release column: a context contributes through the
        // rows that reference it (HeaderVersion, VariableVersion, TableVersion, CompoundItemContext).
        //   ctx 1: referenced by a header version starting in 1        -> properties 500, 501
        //   ctx 2: referenced only by a variable version starting in 3 -> 502
        //   ctx 3: referenced by a table version [1,2)                 -> 503 (ended before R, still history)
        //   ctx 4: referenced by a compound item context starting in 1 -> 504
        //   ctx 5: referenced only by a compound item context from 3   -> 505
        //   ctx 6: referenced by nothing                               -> 506 (never contributes)
        X(c, "INSERT INTO [HeaderVersion] ([HeaderVID],[HeaderID],[PropertyID],[StartReleaseID],[ContextID]) VALUES (13,1,NULL,1,1)");
        X(c, "INSERT INTO [VariableVersion] ([VariableVID],[VariableID],[PropertyID],[StartReleaseID],[ContextID]) VALUES (5,1,NULL,3,2)");
        X(c, "INSERT INTO [TableVersion] VALUES (1,1,2,3)");
        X(c, "INSERT INTO [CompoundItemContext] VALUES (4,1)");
        X(c, "INSERT INTO [CompoundItemContext] VALUES (5,3)");
        foreach (var (context, property) in new[] { (1, 500), (1, 501), (2, 502), (3, 503), (4, 504), (5, 505), (6, 506) })
        {
            X(c, $"INSERT INTO [ContextComposition] VALUES ({context},{property})");
        }
    }

    /// <summary>Opens the fixture at the release with the given code and applies <paramref name="read"/>.</summary>
    private static T At<T>(string releaseCode, Func<Dpm20AccessReader, T> read)
    {
        var accdb = Dpm2SyntheticSource.Create(Populate);
        try
        {
            using var reader = new Dpm20AccessReader(accdb, cutoffReleaseCode: releaseCode);
            reader.Open();
            return read(reader);
        }
        finally
        {
            Dpm2SyntheticSource.Delete(accdb);
        }
    }

    private static List<int> Sorted(IEnumerable<int> values) => values.OrderBy(v => v).ToList();

    private static List<string> SortedText(IEnumerable<string> values) => values.OrderBy(v => v, StringComparer.Ordinal).ToList();

    [AceFact]
    public void ItemCategory_RowStartingAfterR_IsAbsent_RowEndedBeforeR_IsHistory_AndALaterRIncludesTheLateRow()
    {
        List<string> Codes(string r) => At(r, x => SortedText(x.ReadItemCategories().Select(i => i.Code)));

        // R = 2: 'c' (starts in 3) is absent; 'old-b' (ended in 2) and 'd' (starts in 2) are present.
        Assert.Equal(["a", "d", "old-b"], Codes("2.0"));
        // Positive control: at R = 3 the very same fixture also yields 'c'.
        Assert.Equal(["a", "c", "d", "old-b"], Codes("3.0"));
    }

    [AceFact]
    public void PropertyCategory_RowStartingAfterR_IsAbsent_AndTheEndedOneIsKept()
    {
        List<int> Props(string r) => At(r, x => Sorted(x.ReadPropertyCategories().Select(p => p.PropertyId)));

        Assert.Equal([1, 3], Props("2.0"));
        Assert.Equal([1, 2, 3], Props("3.0"));
    }

    [AceFact]
    public void SuperCategoryComposition_RowStartingAfterR_IsAbsent()
    {
        List<int> Members(string r) => At(r, x => Sorted(x.ReadSuperCategoryCompositions().Select(s => s.CategoryId)));

        Assert.Equal([1], Members("2.0"));
        Assert.Equal([1, 2], Members("3.0"));
    }

    [AceFact]
    public void Category_CreatedAfterR_IsAbsent()
    {
        List<string> Codes(string r) => At(r, x => SortedText(x.ReadCategories().Select(c => c.Code)));

        Assert.Equal(["EARLYCAT"], Codes("2.0"));
        Assert.Equal(["EARLYCAT", "LATECAT"], Codes("3.0"));
    }

    [AceFact]
    public void SubCategory_NeedsAVersionStartingByR_AndEndedVersionsStayInTheAllVersionsRead()
    {
        List<string> SubCategories(string r) => At(r, x => SortedText(x.ReadSubCategories().Select(s => s.Code)));
        List<int> Versions(string r) => At(r, x => Sorted(x.ReadAllSubCategoryVersions().Select(v => v.SubCategoryVId)));

        // S2 has only a version starting in 3: absent at R = 2. S3 has an early version: present.
        Assert.Equal(["S1", "S3"], SubCategories("2.0"));
        Assert.Equal(["S1", "S2", "S3"], SubCategories("3.0"));

        // 1002 ended in 2 (before R = 3) and is still read; 1001 and 1003 start in 3.
        Assert.Equal([1000, 1002], Versions("2.0"));
        Assert.Equal([1000, 1001, 1002, 1003], Versions("3.0"));
    }

    [AceFact]
    public void FactKeyAndNonKeyPropertyIds_ComeOnlyFromVersionsStartingByR()
    {
        Assert.Equal([100], At("2.0", x => Sorted(x.ReadFactVariablePropertyIds())));
        Assert.Equal([100, 101], At("3.0", x => Sorted(x.ReadFactVariablePropertyIds())));

        Assert.Equal([200], At("2.0", x => Sorted(x.ReadKeyVariablePropertyIds())));
        Assert.Equal([200, 201], At("3.0", x => Sorted(x.ReadKeyVariablePropertyIds())));

        // The KEY header (property 400) never appears among the non-key header properties.
        Assert.Equal([300], At("2.0", x => Sorted(x.ReadNonKeyHeaderPropertyIds())));
        Assert.Equal([300, 301], At("3.0", x => Sorted(x.ReadNonKeyHeaderPropertyIds())));
    }

    [AceFact]
    public void ContextCompositionProperties_ComeOnlyFromContextsReferencedByRowsStartingByR()
    {
        // R = 2: contexts 1 (header version from 1), 3 (table version, ended in 2) and 4 (compound
        // item context from 1). Context 2 (variable version from 3), 5 (compound item context from 3)
        // and 6 (referenced by nothing) do not contribute.
        Assert.Equal([500, 501, 503, 504], At("2.0", x => Sorted(x.ReadContextCompositionPropertyIds())));

        // Positive control: at R = 3 the contexts that only late rows reference now contribute, so the
        // absence above comes from the filter. The unreferenced context 6 never does.
        Assert.Equal([500, 501, 502, 503, 504, 505], At("3.0", x => Sorted(x.ReadContextCompositionPropertyIds())));
    }
}

/// <summary><c>mRelease</c> holds only the rows with <c>ReleaseID &lt;= R</c> (#73).</summary>
[Trait("Tier", "Synthetic")]
public sealed class Dpm20SkeletonReleaseRowsTests : IDisposable
{
    private static readonly Dpm20ReleaseRow[] Releases =
    [
        new(3, "4.2", "2025-01-03", "released", null, false),
        new(1, "4.0", "2025-01-01", "released", null, false),
        new(4, "4.3", "2025-01-04", "validation", null, true),
        new(2, "4.1", "2025-01-02", "released", null, false),
    ];

    private readonly string _directory = Path.Join(Path.GetTempPath(), $"EbaDpm.MRelease_{Environment.ProcessId}_{Guid.NewGuid():N}");

    public Dpm20SkeletonReleaseRowsTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort.
        }
    }

    private List<(string Code, long IsCurrent)> MRelease(string cutoffCode, int cutoffId)
    {
        var path = Path.Join(_directory, $"skeleton-{cutoffCode}.db");
        SchemaCreator.Create(path, overwrite: false);
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();

        var result = Dpm20SkeletonLoader.Load(Releases, [], [], cutoffCode, cutoffId, connection);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT ReleaseCode, IsCurrent FROM mRelease ORDER BY ReleaseID";
        using var rows = command.ExecuteReader();
        var list = new List<(string, long)>();
        while (rows.Read())
        {
            list.Add((rows.GetString(0), rows.GetInt64(1)));
        }

        Assert.Equal(list.Count, result.ReleaseRows);
        return list;
    }

    [Fact]
    public void MRelease_HasOnlyTheReleasesUpToR_AndOnlyRIsCurrent()
    {
        Assert.Equal([("4.0", 0L), ("4.1", 0L), ("4.2", 1L)], MRelease("4.2", 3));
    }

    [Fact]
    public void MRelease_PositiveControl_ALaterRIncludesTheLaterReleases()
    {
        // The source holds 4.3 as well: it is absent above only because of the cutoff.
        Assert.Equal([("4.0", 0L), ("4.1", 0L), ("4.2", 0L), ("4.3", 1L)], MRelease("4.3", 4));
    }
}
