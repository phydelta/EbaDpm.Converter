using EbaDpm.Converter.Cli;
using EbaDpm.Converter.Native.Tests.Support;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Native.Tests.Layouts;

/// <summary>
/// Layout repositories extracted from the real EBA Annotated Table Layouts through the command line,
/// built once per process and shared by the data tests of this project (an extraction takes a few
/// seconds). Nothing is done unless a data test asks for one, so on a machine without the data
/// directory nothing is built.
/// </summary>
internal static class RealLayoutRepositories
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, string> Built = new(StringComparer.Ordinal);
    private static string? _directory;

    /// <summary>The repository of the layouts in <paramref name="layoutsDirectory"/> extracted with the dictionary of
    /// <paramref name="dictionaryPath"/>; <paramref name="key"/> names it for the cache.</summary>
    public static string Extract(string key, string layoutsDirectory, string dictionaryPath)
    {
        lock (Gate)
        {
            if (Built.TryGetValue(key, out var existing))
            {
                return existing;
            }

            if (_directory is null)
            {
                _directory = Path.Join(Path.GetTempPath(), $"native-plc-real_{Environment.ProcessId}_{Guid.NewGuid():N}");
                Directory.CreateDirectory(_directory);
                AppDomain.CurrentDomain.ProcessExit += (_, _) => Cleanup();
            }

            var output = Path.Join(_directory, key + ".db");
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            var exit = CliRunner.Run(
                ["--extract-layouts", layoutsDirectory, "--out", output, "--dictionary", dictionaryPath, "--plain"], stdout, stderr);
            Assert.True(exit == 0, $"--extract-layouts ({key}) exited {exit}: {stderr}\n{stdout}");
            Built[key] = output;
            return output;
        }
    }

    /// <summary>The 45-table 4.4 distribution output (<c>--release 4.4</c>), used as the other dictionary.</summary>
    public static string Distribution44(string sourcePath)
    {
        lock (Gate)
        {
            const string Key = "distribution-4.4-dictionary";
            if (Built.TryGetValue(Key, out var existing))
            {
                return existing;
            }

            if (_directory is null)
            {
                _directory = Path.Join(Path.GetTempPath(), $"native-plc-real_{Environment.ProcessId}_{Guid.NewGuid():N}");
                Directory.CreateDirectory(_directory);
                AppDomain.CurrentDomain.ProcessExit += (_, _) => Cleanup();
            }

            var output = Path.Join(_directory, Key + ".db");
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            var exit = CliRunner.Run(["--source", sourcePath, "--output", output, "--release", "4.4", "--plain"], stdout, stderr);
            SqliteConnection.ClearAllPools(); // the writer's pooled connection would keep the file locked.
            Assert.True(exit == 0, $"45-table 4.4 conversion exited {exit}: {stderr}\n{stdout}");
            Built[Key] = output;
            return output;
        }
    }

    private static void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (_directory is not null && Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Left for the OS temp cleanup.
        }
    }
}

/// <summary>
/// Content comparison of two layout repositories: every <c>Layout*</c> table except
/// <c>LayoutExtraction</c> (the provenance of the run: dictionary path, hash, timestamp), row by row,
/// all columns. The surrogate ids are part of the rows: both repositories come from the same extractor
/// over the same files, which assigns them in extraction order, so they coincide when the two
/// dictionaries classify the same codes (a different classification changes the content of the rows
/// anyway). Returns one line per difference, empty when the repositories agree.
/// </summary>
internal static class LayoutRepositoryComparer
{
    public static IReadOnlyList<string> Differences(string pathA, string pathB, int samplesPerTable = 3)
    {
        var differences = new List<string>();
        using var a = Open(pathA);
        using var b = Open(pathB);
        var tablesA = Tables(a);
        var tablesB = Tables(b);
        if (!tablesA.SequenceEqual(tablesB, StringComparer.Ordinal))
        {
            differences.Add($"tables differ: [{string.Join(", ", tablesA)}] against [{string.Join(", ", tablesB)}]");
            return differences;
        }

        foreach (var table in tablesA)
        {
            var columns = new List<string>();
            Read(a, $"PRAGMA table_info(\"{table}\")", r => columns.Add(r.GetString(1)));
            var order = string.Join(", ", columns.Select(c => $"\"{c}\""));
            var sql = $"SELECT {order} FROM \"{table}\" ORDER BY {order}";
            var rowsA = Rows(a, sql, columns.Count);
            var rowsB = Rows(b, sql, columns.Count);
            if (rowsA.Count != rowsB.Count)
            {
                differences.Add($"{table}: {rowsA.Count} rows against {rowsB.Count}");
            }

            var setB = new HashSet<string>(rowsB, StringComparer.Ordinal);
            var setA = new HashSet<string>(rowsA, StringComparer.Ordinal);
            var onlyA = rowsA.Where(r => !setB.Contains(r)).ToList();
            var onlyB = rowsB.Where(r => !setA.Contains(r)).ToList();
            if (onlyA.Count > 0 || onlyB.Count > 0)
            {
                differences.Add(
                    $"{table}: {onlyA.Count} row(s) only in the first, {onlyB.Count} only in the second; " +
                    $"e.g. first: {string.Join(" ; ", onlyA.Take(samplesPerTable))} | second: {string.Join(" ; ", onlyB.Take(samplesPerTable))}");
            }
        }

        return differences;
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private static List<string> Tables(SqliteConnection connection)
    {
        var tables = new List<string>();
        Read(
            connection,
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name LIKE 'Layout%' AND name <> 'LayoutExtraction' ORDER BY name",
            r => tables.Add(r.GetString(0)));
        return tables;
    }

    private static List<string> Rows(SqliteConnection connection, string sql, int columns)
    {
        var rows = new List<string>();
        Read(connection, sql, r =>
        {
            var values = new string[columns];
            for (var i = 0; i < columns; i++)
            {
                values[i] = r.IsDBNull(i) ? "<null>" : Convert.ToString(r.GetValue(i), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
            }

            rows.Add(string.Join("|", values));
        });
        return rows;
    }

    private static void Read(SqliteConnection connection, string sql, Action<SqliteDataReader> onRow)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            onRow(reader);
        }
    }
}
