using EbaDpm.Converter.Core.Sqlite;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Tests.Schema;

/// <summary>
/// Behaviour of <c>--overwrite</c> (SchemaCreator.Create): it overwrites when requested and fails
/// cleanly when it is not requested and the output file already exists. Each test uses its own
/// temporary directory, independent of the others.
/// </summary>
public sealed class SchemaCreatorOverwriteTests : IDisposable
{
    private readonly string _tempDirectory;

    public SchemaCreatorOverwriteTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"EbaDpm.OverwriteTests_{Environment.ProcessId}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        try
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort.
        }
    }

    [Fact]
    public void Create_WithoutOverwrite_WhenFileDoesNotExist_Succeeds()
    {
        var outputPath = Path.Combine(_tempDirectory, "output.db");

        var statementCount = SchemaCreator.Create(outputPath, overwrite: false);

        Assert.True(File.Exists(outputPath), $"Expected '{outputPath}' to exist after Create.");
        Assert.True(statementCount > 0, "Expected at least one CREATE TABLE statement to be executed.");
    }

    [Fact]
    public void Create_WithoutOverwrite_WhenFileAlreadyExists_ThrowsIOException()
    {
        var outputPath = Path.Combine(_tempDirectory, "output.db");
        File.WriteAllText(outputPath, "pre-existing content, not a valid SQLite database");

        var exception = Assert.Throws<IOException>(() => SchemaCreator.Create(outputPath, overwrite: false));

        Assert.Contains(outputPath, exception.Message);

        var contentAfter = File.ReadAllText(outputPath);
        Assert.Equal(
            "pre-existing content, not a valid SQLite database",
            contentAfter);
    }

    [Fact]
    public void Create_WithOverwrite_WhenFileAlreadyExists_ReplacesItCleanly()
    {
        var outputPath = Path.Combine(_tempDirectory, "output.db");
        File.WriteAllText(outputPath, "pre-existing content, not a valid SQLite database");

        var statementCount = SchemaCreator.Create(outputPath, overwrite: true);

        Assert.True(statementCount > 0, "Expected at least one CREATE TABLE statement to be executed.");

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = outputPath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString();

        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        var tableNames = SchemaInspector.GetTableNames(connection);

        Assert.True(
            tableNames.Count == statementCount,
            $"Expected {statementCount} tables after the overwrite (one per executed CREATE TABLE), " +
            $"but the resulting database contains {tableNames.Count}.");
    }

    [Fact]
    public void Create_WithOverwrite_WhenFileDoesNotExist_SucceedsAsIfOverwriteWereFalse()
    {
        var outputPath = Path.Combine(_tempDirectory, "output.db");

        var statementCount = SchemaCreator.Create(outputPath, overwrite: true);

        Assert.True(File.Exists(outputPath));
        Assert.True(statementCount > 0);
    }

    [Fact]
    public void Create_CalledTwiceWithOverwrite_ProducesIdenticalTableSetBothTimes()
    {
        var outputPath = Path.Combine(_tempDirectory, "output.db");

        SchemaCreator.Create(outputPath, overwrite: false);

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = outputPath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString();

        List<string> FirstRunTables()
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            return SchemaInspector.GetTableNames(connection);
        }

        var firstRunTables = FirstRunTables();

        // Microsoft.Data.Sqlite pools connections per connection string; even though the previous
        // connection is disposed, the pool may keep the file handle within the same process.
        // SchemaCreator.Create needs to delete the file in order to overwrite it, so the pool is
        // cleared before the second call (this is a test-isolation detail within a single
        // process; a second real run of the CLI is a new process without this problem).
        SqliteConnection.ClearAllPools();

        SchemaCreator.Create(outputPath, overwrite: true);

        List<string> secondRunTables;
        using (var connection = new SqliteConnection(connectionString))
        {
            connection.Open();
            secondRunTables = SchemaInspector.GetTableNames(connection);
        }

        Assert.True(
            firstRunTables.SequenceEqual(secondRunTables, StringComparer.Ordinal),
            "Reproducibility: the same input must produce the same output. " +
            $"1st run: {string.Join(", ", firstRunTables)}\n" +
            $"2nd run: {string.Join(", ", secondRunTables)}");
    }
}
