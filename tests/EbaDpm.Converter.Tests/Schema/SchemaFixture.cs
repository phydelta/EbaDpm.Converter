using EbaDpm.Converter.Core.Sqlite;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Tests.Schema;

/// <summary>
/// Class fixture (xUnit <see cref="IClassFixture{TFixture}"/>): generates the target database
/// ONCE with <see cref="SchemaCreator.Create"/> in a temporary directory outside the repo, and
/// opens read-only connections both to that generated database and to the reference database
/// <c>EBA_4.2_Hotfix.db</c>. All later operations are reads (PRAGMAs and SELECT), so sharing the
/// connections between tests of the same class introduces no dependencies between them: each
/// test can run in any order.
/// When the data directory is not available the fixture does nothing (the tests are skipped).
/// </summary>
public sealed class SchemaFixture : IDisposable
{
    public string TempDirectory { get; } = null!;
    public string GeneratedDatabasePath { get; } = null!;

    /// <summary>Number of CREATE TABLE statements executed by <see cref="SchemaCreator.Create"/>.</summary>
    public int CreatedTableStatementCount { get; }

    internal SqliteConnection GeneratedConnection { get; } = null!;
    internal SqliteConnection ReferenceConnection { get; } = null!;

    public SchemaFixture()
    {
        if (!RepoPaths.IsDataAvailable)
        {
            return;
        }

        RepoPaths.EnsureReferenceDatabaseExists();

        TempDirectory = Path.Combine(Path.GetTempPath(), $"EbaDpm.SchemaTests_{Environment.ProcessId}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(TempDirectory);
        GeneratedDatabasePath = Path.Combine(TempDirectory, "generated.db");

        CreatedTableStatementCount = SchemaCreator.Create(GeneratedDatabasePath, overwrite: false);

        GeneratedConnection = OpenReadOnly(GeneratedDatabasePath);
        ReferenceConnection = OpenReadOnly(RepoPaths.ReferenceDatabasePath);
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
        GeneratedConnection?.Dispose();
        ReferenceConnection?.Dispose();
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
