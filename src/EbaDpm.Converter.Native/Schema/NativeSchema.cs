using EbaDpm.Converter.Native.Catalog;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Native.Schema;

/// <summary>
/// Creates the empty native database from the baseline (4.4) catalogue: what
/// <c>--schema-only --target native</c> writes. No Access database is needed.
/// </summary>
public static class NativeSchema
{
    /// <summary>
    /// Creates the SQLite database at <paramref name="outputPath"/> with the tables and indexes of the
    /// baseline, empty.
    /// </summary>
    /// <param name="overwrite">If <c>false</c> and the file already exists, throws <see cref="IOException"/>.</param>
    /// <returns>The DDL that was run (its <see cref="NativeSchemaDdl.Tables"/> are the tables created).</returns>
    public static NativeSchemaDdl CreateEmpty(string outputPath, bool overwrite)
    {
        NativeOutputFile.Prepare(outputPath, overwrite);

        var ddl = NativeSchemaBuilder.Build(NativeBaseline.Load().Catalog);
        try
        {
            using (var connection = NativeOutputFile.Open(outputPath, create: true))
            {
                NativeOutputFile.Execute(connection, ddl.Statements);
            }
        }
        catch
        {
            NativeOutputFile.Discard(outputPath);
            throw;
        }

        return ddl;
    }
}

/// <summary>File and connection handling of the native SQLite output.</summary>
internal static class NativeOutputFile
{
    /// <summary>
    /// Checks the path and removes the file that <paramref name="overwrite"/> allows replacing (with
    /// its <c>-shm</c>/<c>-wal</c> companions), as the 45-table schema creation does, and creates the
    /// parent directory.
    /// </summary>
    public static void Prepare(string outputPath, bool overwrite)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            throw new ArgumentException("The output path cannot be empty.", nameof(outputPath));
        }

        if (File.Exists(outputPath))
        {
            if (!overwrite)
            {
                throw new IOException($"The output file already exists: {outputPath}. Use --overwrite to overwrite it.");
            }

            Discard(outputPath);
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    /// <summary>Deletes the file and its companions, after releasing the pooled connections that hold them.</summary>
    public static void Discard(string path)
    {
        SqliteConnection.ClearAllPools();
        foreach (var candidate in new[] { path, path + "-shm", path + "-wal", path + "-journal" }.Where(File.Exists))
        {
            File.Delete(candidate);
        }
    }

    /// <summary>
    /// Opens the database with the bulk-load PRAGMAs. <c>foreign_keys</c> stays off: the foreign keys
    /// are declared, and their integrity is checked afterwards, not enforced during the load.
    /// </summary>
    public static SqliteConnection Open(string path, bool create)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = create ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        connection.Open();

        using var pragma = connection.CreateCommand();
        pragma.CommandText =
            """
            PRAGMA journal_mode = OFF;
            PRAGMA synchronous = OFF;
            PRAGMA foreign_keys = OFF;
            """;
        pragma.ExecuteNonQuery();
        return connection;
    }

    /// <summary>Runs the statements in one transaction.</summary>
    public static void Execute(SqliteConnection connection, IEnumerable<string> statements)
    {
        using var transaction = connection.BeginTransaction();
        foreach (var statement in statements)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = statement;
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }
}
