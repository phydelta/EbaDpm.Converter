using System.Data;
using System.Data.OleDb;
using System.Diagnostics;
using EbaDpm.Converter.Core.Access;
using EbaDpm.Converter.Core.Sqlite;
using EbaDpm.Converter.Native.Catalog;
using EbaDpm.Converter.Native.Schema;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Native.Conversion;

/// <summary>
/// Converts a DPM 2.0 Access database into the native SQLite output: every user table (but the
/// closed exclusion list), 1:1, with the DDL built from the catalogue of the source
/// (docs/target-schema-native.md). Nothing is derived, renumbered or arbitrated.
///
/// Steps: detect the model (DPM 1.0 is rejected) and check the 4.4 gate (a <c>Release</c> row with
/// the baseline code), then read the catalogue, run the DDL of the tables with foreign-key
/// enforcement off, copy each table in primary-key order (one at a time, a single thread, streaming
/// with <see cref="OleDbDataReader"/> into a <see cref="SqliteBatchWriter"/>; ACE OLEDB is not safe
/// for concurrent use), create the indexes, and <c>VACUUM</c>. The file is built beside the output
/// (<c>.partial</c>) and moved into place at the end, so a failed conversion leaves no half-written
/// output, and an existing output is replaced only by a complete one.
/// </summary>
public static class NativeConverter
{
    public static NativeConversionSummary Convert(
        string accdbPath, string outputPath, bool overwrite, Action<NativeConversionEvent>? progress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accdbPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var total = Stopwatch.StartNew();
        var baseline = NativeBaseline.Load();

        if (SourceModelDetector.Detect(accdbPath) == DpmSourceModel.Dpm10)
        {
            throw new NativeSourceNotDpm20Exception(accdbPath);
        }

        if (File.Exists(outputPath) && !overwrite)
        {
            throw new IOException($"The output file already exists: {outputPath}. Use --overwrite to overwrite it.");
        }

        using var access = new OleDbConnection(AceProvider.ConnectionString(accdbPath));
        access.Open();

        var stageElapsed = new Dictionary<NativeConversionStage, TimeSpan>();
        T Stage<T>(NativeConversionStage stage, Func<T> work, int tableCount = 0)
        {
            progress?.Invoke(new(NativeConversionEventKind.StageStarted, stage, TableCount: tableCount));
            var stopwatch = Stopwatch.StartNew();
            var result = work();
            stageElapsed[stage] = stopwatch.Elapsed;
            progress?.Invoke(new(NativeConversionEventKind.StageFinished, stage, Elapsed: stopwatch.Elapsed));
            return result;
        }

        var (catalog, ddl, currentRelease) = Stage(NativeConversionStage.ReadCatalogue, () =>
        {
            var current = CheckBaselineGate(access, accdbPath, baseline.ReleaseCode);
            var read = NativeCatalogReader.Read(access);
            return (read, NativeSchemaBuilder.Build(read), current);
        });

        var partialPath = outputPath + ".partial";
        NativeOutputFile.Prepare(partialPath, overwrite: true);

        try
        {
            IReadOnlyList<NativeTableResult> results;
            using (var sqlite = NativeOutputFile.Open(partialPath, create: true))
            {
                Stage(NativeConversionStage.CreateSchema, () =>
                {
                    NativeOutputFile.Execute(sqlite, ddl.TableStatements);
                    return 0;
                });

                results = Stage(
                    NativeConversionStage.CopyTables,
                    () => CopyTables(access, sqlite, ddl.Tables, progress),
                    ddl.Tables.Count);

                Stage(NativeConversionStage.CreateIndexes, () =>
                {
                    NativeOutputFile.Execute(sqlite, ddl.IndexStatements);
                    return 0;
                });

                // VACUUM cannot run inside a transaction: each writer has committed and closed its own.
                Stage(NativeConversionStage.Vacuum, () =>
                {
                    using var vacuum = sqlite.CreateCommand();
                    vacuum.CommandText = "VACUUM;";
                    vacuum.ExecuteNonQuery();
                    return 0;
                });
            }

            NativeOutputFile.Discard(outputPath);
            File.Move(partialPath, outputPath);

            var present = catalog.Tables.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var excluded = NativeExclusions.ExcludedTables.Where(present.Contains).ToList();
            return new NativeConversionSummary(
                outputPath, currentRelease ?? string.Empty, results, excluded, catalog, ddl, stageElapsed,
                total.Elapsed, new FileInfo(outputPath).Length);
        }
        catch
        {
            NativeOutputFile.Discard(partialPath);
            throw;
        }
    }

    /// <summary>
    /// The 4.4 gate: the source must have a release with the baseline code. Returns the code of its
    /// current release (<c>IsCurrent</c>), or null.
    /// </summary>
    private static string? CheckBaselineGate(OleDbConnection access, string accdbPath, string baselineReleaseCode)
    {
        try
        {
            using var count = new OleDbCommand("SELECT COUNT(1) FROM [Release] WHERE [Code] = ?", access);
            count.Parameters.Add(new OleDbParameter { Value = baselineReleaseCode });
            var hasBaseline = System.Convert.ToInt64(count.ExecuteScalar()) > 0;

            string? current = null;
            using (var currentCommand = new OleDbCommand("SELECT [Code] FROM [Release] WHERE [IsCurrent] = True", access))
            using (var reader = currentCommand.ExecuteReader())
            {
                if (reader.Read() && !reader.IsDBNull(0))
                {
                    current = reader.GetString(0);
                }
            }

            if (!hasBaseline)
            {
                throw new NativeSourceBelowBaselineException(accdbPath, baselineReleaseCode, current);
            }

            return current;
        }
        catch (OleDbException ex)
        {
            throw new InvalidOperationException(
                $"Could not read the Release table of '{accdbPath}' to check the native baseline release: {ex.Message}", ex);
        }
    }

    private static List<NativeTableResult> CopyTables(
        OleDbConnection access, SqliteConnection sqlite, IReadOnlyList<NativeTable> tables, Action<NativeConversionEvent>? progress)
    {
        var results = new List<NativeTableResult>(tables.Count);
        for (var i = 0; i < tables.Count; i++)
        {
            var stopwatch = Stopwatch.StartNew();
            var table = tables[i];
            var rows = CopyTable(access, sqlite, table);
            var result = new NativeTableResult(table.Name, rows, table.Columns.Count, stopwatch.Elapsed);
            results.Add(result);
            progress?.Invoke(new(
                NativeConversionEventKind.TableCopied, NativeConversionStage.CopyTables, table.Name, i + 1, tables.Count, rows, result.Elapsed));
        }

        return results;
    }

    /// <summary>
    /// Streams one table, ordered by its primary key, into SQLite and checks the rows written against
    /// the <c>COUNT(1)</c> of Access.
    /// </summary>
    private static long CopyTable(OleDbConnection access, SqliteConnection sqlite, NativeTable table)
    {
        var forms = table.Columns
            .Select(c => NativeTypeMapping.Resolve(table.Name, c.Name, c.DataType).Form)
            .ToArray();

        var select = "SELECT " + string.Join(", ", table.Columns.Select(c => AccessName(c.Name)))
            + " FROM " + AccessName(table.Name)
            + (table.PrimaryKey.Count > 0 ? " ORDER BY " + string.Join(", ", table.PrimaryKey.Select(AccessName)) : string.Empty);

        long rows;
        using (var command = new OleDbCommand(select, access) { CommandTimeout = 0 })
        using (var reader = command.ExecuteReader(CommandBehavior.SequentialAccess))
        using (var writer = new SqliteBatchWriter(
            sqlite, NativeSchemaBuilder.Quote(table.Name), table.Columns.Select(c => c.Name).ToArray()))
        {
            var values = new object?[forms.Length];
            while (reader.Read())
            {
                for (var i = 0; i < forms.Length; i++)
                {
                    values[i] = NativeTypeMapping.ToSqliteValue(forms[i], reader.GetValue(i));
                }

                writer.AddRow(values);
            }

            writer.Complete();
            rows = writer.RowsWritten;
        }

        using var countCommand = new OleDbCommand($"SELECT COUNT(1) FROM {AccessName(table.Name)}", access);
        var expected = System.Convert.ToInt64(countCommand.ExecuteScalar());
        if (expected != rows)
        {
            throw new InvalidOperationException(
                $"Table '{table.Name}': {rows} rows written but Access reports {expected}.");
        }

        return rows;
    }

    /// <summary>An Access SQL identifier in square brackets (a name that contains brackets cannot be quoted).</summary>
    private static string AccessName(string name)
    {
        if (name.Contains('[') || name.Contains(']'))
        {
            throw new NotSupportedException($"The Access name '{name}' contains a square bracket and cannot be quoted.");
        }

        return "[" + name + "]";
    }
}
