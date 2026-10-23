using EbaDpm.Converter.Core.Access.Dpm20;
using EbaDpm.Converter.Core.Mapping.Dpm20;
using EbaDpm.Converter.Core.Sqlite;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Tests.Dpm2;

/// <summary>
/// Synthetic tests of <see cref="Dpm20HierarchyNodeLoader.Load"/> through
/// <see cref="InMemoryHierarchyNodeSource"/>: no Access, no data files. Rule under test:
/// docs/mapping-dpm2.md, section "mHierarchyNode" (effective version: the one current at the
/// cutoff).
/// </summary>
public sealed class Dpm20HierarchyNodeLoaderSyntheticTests : IDisposable
{
    private readonly string _tempDirectory;

    public Dpm20HierarchyNodeLoaderSyntheticTests()
    {
        _tempDirectory = Path.Join(Path.GetTempPath(), $"EbaDpm.HierSynth_{Environment.ProcessId}_{Guid.NewGuid():N}");
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
    public void EffectiveVersion_IsTheOneCurrentAtTheCutoff_ALaterVersionDoesNotWin()
    {
        // One sub-category with two versions: V10 is current at the cutoff (release 3), V11 starts
        // at release 5, beyond the cutoff. Only the items of V10 may become nodes.
        var source = new InMemoryHierarchyNodeSource { CutoffReleaseId = 3 };
        source.SubCategoryVersions.AddRange([new(10, 1, 1, 5), new(11, 1, 5, null)]);
        source.Categories.Add(new(10, "D1", "Domain one", null, true));
        source.Items.AddRange([new(1001, "Alpha item"), new(1002, "Beta item")]);
        source.ItemCategories.AddRange(
        [
            new(1001, 10, "a", false, 1, null),
            new(1002, 10, "b", false, 1, null),
        ]);
        source.SubCategoryItems.AddRange(
        [
            new(1001, 10, 1, null, null, null, null), // in V10, the effective version
            new(1002, 11, 1, null, null, null, null), // in V11, after the cutoff
        ]);

        var path = Path.Join(_tempDirectory, "out.db");
        SchemaCreator.Create(path, overwrite: false);

        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        Execute(connection, "INSERT INTO mDomain (DomainID, DomainCode) VALUES (1, 'D1')");
        Execute(connection, "INSERT INTO mMember (MemberID, DomainID, MemberCode, MemberLabel) VALUES (100, 1, 'a', 'Alpha item')");
        Execute(connection, "INSERT INTO mMember (MemberID, DomainID, MemberCode, MemberLabel) VALUES (101, 1, 'b', 'Beta item')");

        Execute(connection, "INSERT INTO mHierarchy (HierarchyID, HierarchyCode, DomainID) VALUES (1, 'H1', 1)");

        var hierarchies =new Dictionary<int, (int HierarchyId, string HierarchyCode, int DomainId)>
        {
            [1] = (1, "H1", 1),
        };

        var result = Dpm20HierarchyNodeLoader.Load(source, connection, hierarchies);

        Assert.Equal(1, result.HierarchyNodeRows);
        Assert.Equal(0, result.HierarchiesWithoutNodes);

        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT m.MemberCode, n.HierarchyNodeLabel FROM mHierarchyNode n JOIN mMember m ON m.MemberID = n.MemberID ORDER BY m.MemberCode";
        using var reader = command.ExecuteReader();
        var nodes = new List<(string Code, string? Label)>();
        while (reader.Read())
        {
            nodes.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
        }

        Assert.Equal([("a", "Alpha item")], nodes);
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
