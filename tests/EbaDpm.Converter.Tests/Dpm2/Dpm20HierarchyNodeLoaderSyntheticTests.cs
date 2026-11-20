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

    // -----------------------------------------------------------------------------------------
    // Tie-break by consistency (docs/mapping-dpm2.md, "Tie-break by consistency").
    // Common scenario: release R = 5 (the cutoff), domains D1 (1, the hierarchy's), D2 (2), D3 (3);
    // categories 10/20/30 carry those codes. Each test builds its own ItemCategory history.
    // -----------------------------------------------------------------------------------------

    private const int R = 5;

    private static InMemoryHierarchyNodeSource NewSource(int hierarchies = 1)
    {
        var source = new InMemoryHierarchyNodeSource { CutoffReleaseId = R };
        source.Categories.AddRange([new(10, "D1", "one", null, true), new(20, "D2", "two", null, true), new(30, "D3", "three", null, true)]);
        for (var h = 1; h <= hierarchies; h++)
        {
            // SubCategoryVID 10 + h, SubCategoryID h, open since release 1: current at R.
            source.SubCategoryVersions.Add(new(10 + h, h, 1, null));
        }

        return source;
    }

    private SqliteConnection NewDestination(
        int hierarchies, bool united, params (int MemberId, int DomainId, string Code, string Label)[] members)
    {
        var path = Path.Join(_tempDirectory, $"tb-{Guid.NewGuid():N}.db");
        SchemaCreator.Create(path, overwrite: false);
        var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        Execute(connection, "INSERT INTO mDomain (DomainID, DomainCode) VALUES (1, 'D1'), (2, 'D2'), (3, 'D3')");
        foreach (var m in members)
        {
            Execute(connection, $"INSERT INTO mMember (MemberID, DomainID, MemberCode, MemberLabel) VALUES ({m.MemberId}, {m.DomainId}, '{m.Code}', '{m.Label}')");
        }

        for (var h = 1; h <= hierarchies; h++)
        {
            Execute(connection, $"INSERT INTO mHierarchy (HierarchyID, HierarchyCode, DomainID) VALUES ({h}, 'H{h}', 1)");
        }

        if (united)
        {
            Execute(connection, "INSERT INTO mDomainUnion (UnionDomainID, UnitedDomainID) VALUES (1, 2)");
        }

        return connection;
    }

    private static Dictionary<int, (int HierarchyId, string HierarchyCode, int DomainId)> Hierarchies(int count)
        => Enumerable.Range(1, count).ToDictionary(h => h, h => (h, $"H{h}", 1));

    private static List<(string Hierarchy, string Domain, string Code)> NodeMembers(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT h.HierarchyCode, d.DomainCode, m.MemberCode
            FROM mHierarchyNode n
            JOIN mHierarchy h ON h.HierarchyID = n.HierarchyID
            JOIN mMember m ON m.MemberID = n.MemberID
            JOIN mDomain d ON d.DomainID = m.DomainID
            ORDER BY h.HierarchyCode, m.MemberCode
            """;
        using var reader = command.ExecuteReader();
        var rows = new List<(string, string, string)>();
        while (reader.Read())
        {
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        return rows;
    }

    /// <summary>
    /// (a) The item's current row at R is in D2 (ended-at-R pattern of GA4/MC150): its older row in D1 ends
    /// exactly at R. The node is re-anchored to D1 'a'; the child's ParentMemberID/Path follow the
    /// re-anchored member; one line is reported.
    /// </summary>
    [Fact]
    public void TieBreak_ConflictingNode_IsReanchoredToTheOlderRowInTheHierarchyDomain()
    {
        var source = NewSource();
        source.Items.AddRange([new(1001, "Parent"), new(1002, "Child")]);
        source.ItemCategories.AddRange(
        [
            new(1001, 10, "a", false, 1, R),      // D1:a, ended exactly at R
            new(1001, 20, "a2", false, R, null),  // D2:a2, current at R (different code: no GA30 twin)
            new(1002, 10, "c", false, 1, null),   // consistent child
        ]);
        source.SubCategoryItems.AddRange([new(1001, 11, 1, null, null, null, null), new(1002, 11, 2, null, 1001, null, null)]);

        using var connection = NewDestination(1, united: false,
            (100, 1, "a", "Parent"), (101, 2, "a2", "Parent"), (102, 1, "c", "Child"));

        var result = Dpm20HierarchyNodeLoader.Load(source, connection, Hierarchies(1));

        Assert.Equal([("H1", "D1", "a"), ("H1", "D1", "c")], NodeMembers(connection));
        var line = Assert.Single(result.ReanchoredMembers);
        Assert.Contains("HierarchyID=1", line, StringComparison.Ordinal);
        Assert.Contains("ItemID=1001", line, StringComparison.Ordinal);
        Assert.Empty(result.AnchoredMembers);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT ParentMemberID, Path FROM mHierarchyNode WHERE MemberID = 102";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(100, reader.GetInt32(0));
        Assert.Equal("100.102.", reader.GetString(1));
    }

    /// <summary>
    /// (a, control) Same node WITHOUT the older D1 row's history being usable (it starts AFTER R) - the rule
    /// is bounded by StartReleaseID &lt;= R, so it must not be used: the conversion stops (see (d)). Shows that
    /// the re-anchoring in (a) really comes from the row ended at R and not from any D1 row.
    /// </summary>
    [Fact]
    public void TieBreak_ARowStartingAfterR_IsNotAnAllowedCandidate()
    {
        var source = NewSource();
        source.Items.Add(new(1001, "Parent"));
        source.ItemCategories.AddRange(
        [
            new(1001, 20, "a2", false, 1, null),
            new(1001, 10, "a", false, R + 1, null), // starts after R
        ]);
        source.SubCategoryItems.Add(new(1001, 11, 1, null, null, null, null));

        using var connection = NewDestination(1, united: false, (100, 1, "a", "Parent"), (101, 2, "a2", "Parent"));

        var ex = Assert.Throws<InvalidOperationException>(() => Dpm20HierarchyNodeLoader.Load(source, connection, Hierarchies(1)));
        Assert.Contains("ItemID=1001", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// (b) GUARD, positive control. The current member is already in the hierarchy's domain by the GA30 twin
    /// anchoring (current D2:x, twin D1:x with the same label); an OLDER D1 row with another code ('x_old')
    /// also exists. An unguarded rule would pick 'x_old' (highest StartReleaseID &lt;= R in an allowed domain
    /// once D2 is excluded). The node must stay on the anchored D1:x and no re-anchoring is reported.
    /// </summary>
    [Fact]
    public void TieBreak_Guard_ANodeAlreadyInTheHierarchyDomainIsNotChanged()
    {
        var source = NewSource();
        source.Items.Add(new(1001, "Same"));
        source.ItemCategories.AddRange(
        [
            new(1001, 10, "x_old", false, 1, R),
            new(1001, 20, "x", false, R, null),
        ]);
        source.SubCategoryItems.Add(new(1001, 11, 1, null, null, null, null));

        using var connection = NewDestination(1, united: false,
            (100, 1, "x_old", "Same"), (101, 2, "x", "Same"), (102, 1, "x", "Same"));

        var result = Dpm20HierarchyNodeLoader.Load(source, connection, Hierarchies(1));

        Assert.Equal([("H1", "D1", "x")], NodeMembers(connection));
        Assert.Empty(result.ReanchoredMembers);
        Assert.Single(result.AnchoredMembers);
    }

    /// <summary>
    /// (b2) GUARD, second form. The current row is in the hierarchy's domain ('b', open since 3); a
    /// row of the same domain with a HIGHER StartReleaseID that is not current (start = end = R) and a
    /// different code exists. The unguarded rule would choose it; the guard keeps 'b'.
    /// </summary>
    [Fact]
    public void TieBreak_Guard_ACurrentRowInTheHierarchyDomainWinsOverALaterNonCurrentRow()
    {
        var source = NewSource();
        source.Items.Add(new(1001, "B"));
        source.ItemCategories.AddRange(
        [
            new(1001, 10, "b", false, 3, null),
            new(1001, 10, "b_later", false, R, R),
        ]);
        source.SubCategoryItems.Add(new(1001, 11, 1, null, null, null, null));

        using var connection = NewDestination(1, united: false, (100, 1, "b", "B"), (101, 1, "b_later", "B"));

        var result = Dpm20HierarchyNodeLoader.Load(source, connection, Hierarchies(1));

        Assert.Equal([("H1", "D1", "b")], NodeMembers(connection));
        Assert.Empty(result.ReanchoredMembers);
    }

    /// <summary>
    /// (c) A member in a domain UNITED to the hierarchy's (mDomainUnion 1 -> 2) is deliberate and is not
    /// changed even though an older D1 row exists. Positive control: the IDENTICAL data without the
    /// mDomainUnion row IS re-anchored to D1 (the same item/hierarchy), so the union is what protects it.
    /// </summary>
    [Theory]
    [InlineData(true, "D2", "u", 0)]
    [InlineData(false, "D1", "u_old", 1)]
    public void TieBreak_AMemberInAUnitedDomain_IsNotChanged_AndWithoutTheUnionItIs(bool united, string expectedDomain, string expectedCode, int expectedReanchored)
    {
        var source = NewSource();
        source.Items.Add(new(1001, "U"));
        source.ItemCategories.AddRange(
        [
            new(1001, 10, "u_old", false, 1, R),
            new(1001, 20, "u", false, R, null),
        ]);
        source.SubCategoryItems.Add(new(1001, 11, 1, null, null, null, null));

        using var connection = NewDestination(1, united, (100, 1, "u_old", "Old"), (101, 2, "u", "New"));

        var result = Dpm20HierarchyNodeLoader.Load(source, connection, Hierarchies(1));

        Assert.Equal([("H1", expectedDomain, expectedCode)], NodeMembers(connection));
        Assert.Equal(expectedReanchored, result.ReanchoredMembers.Count);
    }

    /// <summary>
    /// (d) No allowed row: the item only ever lived in D2 (and D3 is not united): the conversion stops with
    /// an exception that names the hierarchy, the item and R.
    /// </summary>
    [Fact]
    public void TieBreak_NoAllowedRow_Throws_NamingHierarchyItemAndRelease()
    {
        var source = NewSource();
        source.Items.Add(new(1001, "Orphan"));
        source.ItemCategories.AddRange([new(1001, 20, "o", false, 1, null), new(1001, 30, "o3", false, 2, 4)]);
        source.SubCategoryItems.Add(new(1001, 11, 1, null, null, null, null));

        using var connection = NewDestination(1, united: false, (101, 2, "o", "Orphan"), (102, 3, "o3", "Orphan"));

        var ex = Assert.Throws<InvalidOperationException>(() => Dpm20HierarchyNodeLoader.Load(source, connection, Hierarchies(1)));
        Assert.Contains("HierarchyID=1", ex.Message, StringComparison.Ordinal);
        Assert.Contains("ItemID=1001", ex.Message, StringComparison.Ordinal);
        Assert.Contains($"R={R}", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// (d2) An allowed row exists but its member is NOT in mMember: it is not a candidate, so the conversion
    /// stops the same way (and does not silently pick another row).
    /// </summary>
    [Fact]
    public void TieBreak_AnAllowedRowWithoutAMemberInMMember_IsNotACandidate()
    {
        var source = NewSource();
        source.Items.Add(new(1001, "NoMember"));
        source.ItemCategories.AddRange([new(1001, 10, "ghost", false, 1, R), new(1001, 20, "n", false, R, null)]);
        source.SubCategoryItems.Add(new(1001, 11, 1, null, null, null, null));

        using var connection = NewDestination(1, united: false, (101, 2, "n", "NoMember"));

        var ex = Assert.Throws<InvalidOperationException>(() => Dpm20HierarchyNodeLoader.Load(source, connection, Hierarchies(1)));
        Assert.Contains("ItemID=1001", ex.Message, StringComparison.Ordinal);
        Assert.Contains($"R={R}", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// (e) ReanchoredMembers counts one line PER NODE: two hierarchies of the same domain share the item
    /// (the resolution is cached per item, release and domain, so the second hierarchy hits the cache), plus a
    /// second, unshared item in one of them. 2 nodes for the shared item + 1 = 3 lines, each naming its own
    /// hierarchy; the two H1/H2 shared nodes both land on D1 'a'. Control: the consistent item adds none.
    /// </summary>
    [Fact]
    public void TieBreak_ReanchoredMembers_CountsOnePerNode_EvenWhenTwoHierarchiesShareTheItem()
    {
        var source = NewSource(hierarchies: 2);
        source.Items.AddRange([new(1001, "Shared"), new(1002, "OnlyH1"), new(1003, "Fine")]);
        source.ItemCategories.AddRange(
        [
            new(1001, 10, "a", false, 1, R), new(1001, 20, "a2", false, R, null),
            new(1002, 10, "b", false, 1, R), new(1002, 20, "b2", false, R, null),
            new(1003, 10, "f", false, 1, null),
        ]);
        source.SubCategoryItems.AddRange(
        [
            new(1001, 11, 1, null, null, null, null), new(1002, 11, 2, null, null, null, null), new(1003, 11, 3, null, null, null, null),
            new(1001, 12, 1, null, null, null, null), new(1003, 12, 2, null, null, null, null),
        ]);

        using var connection = NewDestination(2, united: false,
            (100, 1, "a", "Shared"), (101, 2, "a2", "Shared"), (102, 1, "b", "OnlyH1"), (103, 2, "b2", "OnlyH1"), (104, 1, "f", "Fine"));

        var result = Dpm20HierarchyNodeLoader.Load(source, connection, Hierarchies(2));

        Assert.Equal(5, result.HierarchyNodeRows);
        Assert.Equal(3, result.ReanchoredMembers.Count);
        Assert.Equal(1, result.ReanchoredMembers.Count(l => l.StartsWith("HierarchyID=2:", StringComparison.Ordinal)));
        Assert.Equal(2, result.ReanchoredMembers.Count(l => l.StartsWith("HierarchyID=1:", StringComparison.Ordinal)));
        Assert.Equal(2, result.ReanchoredMembers.Count(l => l.Contains("ItemID=1001", StringComparison.Ordinal)));
        Assert.Equal(
            [("H1", "D1", "a"), ("H1", "D1", "b"), ("H1", "D1", "f"), ("H2", "D1", "a"), ("H2", "D1", "f")],
            NodeMembers(connection));
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
