using Microsoft.Data.Sqlite;
using Xunit.Sdk;

namespace EbaDpm.Converter.Tests.Dictionary;

/// <summary>
/// Positive controls for <see cref="QueryHelpers"/> on an in-memory SQLite database: the orphan
/// checks used by the data tests must find a planted orphan, and report 0 when there is none.
/// </summary>
public sealed class QueryHelpersSyntheticTests : IDisposable
{
    private readonly SqliteConnection _connection;

    public QueryHelpersSyntheticTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        Exec("CREATE TABLE Parent (Id INTEGER PRIMARY KEY)");
        Exec("CREATE TABLE Child (Id INTEGER PRIMARY KEY, ParentId INTEGER NULL)");
        Exec("INSERT INTO Parent (Id) VALUES (1), (2)");
    }

    public void Dispose() => _connection.Dispose();

    private void Exec(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    [Fact]
    public void FindOrphans_NoOrphan_ReturnsZero_AndNullKeysAreNotOrphans()
    {
        Exec("INSERT INTO Child (Id, ParentId) VALUES (10, 1), (11, 2), (12, NULL)");

        var (count, samples) = QueryHelpers.FindOrphans(_connection, "Child", "ParentId", "Parent", "Id");

        Assert.Equal(0, count);
        Assert.Empty(samples);
        QueryHelpers.AssertNoOrphans(_connection, "Child", "ParentId", "Parent", "Id");
    }

    [Fact]
    public void FindOrphans_PlantedOrphan_IsDetected_WithItsKeyAsSample()
    {
        Exec("INSERT INTO Child (Id, ParentId) VALUES (10, 1), (11, 99), (12, 99)");

        var (count, samples) = QueryHelpers.FindOrphans(_connection, "Child", "ParentId", "Parent", "Id");

        Assert.Equal(2, count);
        Assert.Equal(["99"], samples);
    }

    [Fact]
    public void AssertNoOrphans_PlantedOrphan_Fails()
    {
        Exec("INSERT INTO Child (Id, ParentId) VALUES (10, 99)");

        var ex = Assert.ThrowsAny<XunitException>(
            () => QueryHelpers.AssertNoOrphans(_connection, "Child", "ParentId", "Parent", "Id"));
        Assert.Contains("1 orphan rows", ex.Message, StringComparison.Ordinal);
        Assert.Contains("99", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Scalar_ReturnsTheValue_AndFailsOnNullOrNoRow()
    {
        Assert.Equal(2, QueryHelpers.Scalar(_connection, "SELECT COUNT(*) FROM Parent"));

        // SUM over an empty set is NULL, and a query with no row returns no scalar: neither is a 0.
        Assert.ThrowsAny<XunitException>(() => QueryHelpers.Scalar(_connection, "SELECT SUM(Id) FROM Child"));
        Assert.ThrowsAny<XunitException>(() => QueryHelpers.Scalar(_connection, "SELECT Id FROM Parent WHERE Id = 99"));
    }
}
