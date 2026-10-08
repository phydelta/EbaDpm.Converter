using System.Reflection;
using System.Text;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Core.Sqlite;

/// <summary>
/// Creates the target SQLite schema (the 45 tables of
/// <c>src/EbaDpm.Converter.Core/Resources/dpm-distribution-schema.sql</c>, the exact output
/// contract) from the DDL file itself, embedded as a resource in this assembly. The
/// <c>CREATE TABLE</c> statements are not rewritten by hand.
/// </summary>
public static class SchemaCreator
{
    private const string EmbeddedResourceName = "EbaDpm.Converter.Core.Resources.dpm-distribution-schema.sql";

    /// <summary>
    /// Creates the SQLite database at <paramref name="outputPath"/> with the 45 tables of the
    /// target schema, empty.
    /// </summary>
    /// <param name="outputPath">Path of the .db file to create.</param>
    /// <param name="overwrite">If <c>false</c> and the file already exists, throws <see cref="IOException"/>.</param>
    /// <returns>Number of <c>CREATE TABLE</c> statements executed.</returns>
    public static int Create(string outputPath, bool overwrite)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            throw new ArgumentException("The output path cannot be empty.", nameof(outputPath));
        }

        if (File.Exists(outputPath))
        {
            if (!overwrite)
            {
                throw new IOException(
                    $"The output file already exists: {outputPath}. Use --overwrite to overwrite it.");
            }

            File.Delete(outputPath);
            DeleteIfExists(outputPath + "-shm");
            DeleteIfExists(outputPath + "-wal");
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var createTableStatements = ReadCreateTableStatements();

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = outputPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();

        using var connection = new SqliteConnection(connectionString);
        connection.Open();

        // Bulk-load PRAGMAs. foreign_keys stays disabled while the schema is created;
        // it is enabled in later phases, after loading.
        using (var pragmaCommand = connection.CreateCommand())
        {
            pragmaCommand.CommandText =
                """
                PRAGMA journal_mode = OFF;
                PRAGMA synchronous = OFF;
                PRAGMA foreign_keys = OFF;
                """;
            pragmaCommand.ExecuteNonQuery();
        }

        using var transaction = connection.BeginTransaction();
        foreach (var statement in createTableStatements)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = statement;
            command.ExecuteNonQuery();
        }

        transaction.Commit();

        return createTableStatements.Count;
    }

    /// <summary>
    /// Reads the embedded DDL and extracts only the <c>CREATE TABLE</c> statements, discarding
    /// <c>--</c> comments and blank statements.
    /// </summary>
    private static List<string> ReadCreateTableStatements()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(EmbeddedResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{EmbeddedResourceName}' not found in the assembly. " +
                "Check the EmbeddedResource item in the .csproj.");

        using var reader = new StreamReader(stream, Encoding.UTF8);
        var rawSql = reader.ReadToEnd();

        var withoutComments = StripLineComments(rawSql);

        var statements = withoutComments
            .Split(';')
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .Where(s => s.StartsWith("CREATE", StringComparison.OrdinalIgnoreCase))
            .ToList();

        return statements;
    }

    /// <summary>
    /// Removes, line by line, everything after a <c>--</c> (full-line comment or trailing
    /// comment after code). The source DDL contains no string literals with <c>--</c>, so the
    /// simple per-line cut is safe.
    /// </summary>
    private static string StripLineComments(string sql)
    {
        var builder = new StringBuilder(sql.Length);
        foreach (var line in sql.Split('\n'))
        {
            var commentIndex = line.IndexOf("--", StringComparison.Ordinal);
            var effectiveLine = commentIndex >= 0 ? line[..commentIndex] : line;
            builder.Append(effectiveLine).Append('\n');
        }

        return builder.ToString();
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
