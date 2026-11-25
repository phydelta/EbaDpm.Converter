using System.Data.OleDb;
using System.Diagnostics;
using System.Text.RegularExpressions;
using EbaDpm.Converter.Core.Validation;
using EbaDpm.Converter.Native.Catalog;
using EbaDpm.Converter.Native.Schema;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Native.Validation;

/// <summary>
/// Plane A of the native output (docs/validation.md, "Native output"): the checks of the output
/// against its source and its own schema. Checks that need the source are skipped, never passed,
/// when there is none.
/// </summary>
internal static partial class NativePlaneAChecks
{
    public const string CompletenessId = "A-NAT-01";
    public const string RowCountId = "A-NAT-02";
    public const string ContentId = "A-NAT-03";
    public const string ForeignKeysId = "A-NAT-04";
    public const string SchemaId = "A-NAT-05";
    public const string DriftId = "A-DRF-01";

    private const string CompletenessStatement =
        "every user table of the source is exported or in the exclusion list; every exported table has exactly the columns of the source; the output has no other table";
    private const string RowCountStatement = "every exported table has as many rows as the source table (COUNT)";
    private const string ContentStatement = "every row of every exported table equals the source row with the same primary key; no row is missing or extra";
    private const string ForeignKeysStatement = "PRAGMA foreign_key_check returns no rows";
    private const string SchemaStatement = "the output schema equals the DDL built from the catalogue";
    private const string DriftStatement = "schema drift: the source catalogue against the 4.4 baseline (informative)";

    private const string AllTables = "(all exported tables)";

    public static CheckResult SkippedForNoSource(string id, ValidationLayer layer, string statement, string table = AllTables)
        => CheckResult.Skip(id, ValidationPlane.A, layer, table, statement, NativeCheckSupport.NeedsSource);

    public static CheckResult SkippedDrift()
        => SkippedForNoSource(DriftId, ValidationLayer.Informative, DriftStatement, "(catalogue)");

    public static IEnumerable<CheckResult> SkippedChecks()
    {
        yield return SkippedForNoSource(CompletenessId, ValidationLayer.Critical, CompletenessStatement);
        yield return SkippedForNoSource(RowCountId, ValidationLayer.Critical, RowCountStatement);
        yield return SkippedForNoSource(ContentId, ValidationLayer.Critical, ContentStatement);
    }

    /// <summary>A-NAT-01: tables and columns of the output against the source catalogue.</summary>
    public static CheckResult Completeness(SqliteConnection output, NativeCatalog source)
    {
        var stopwatch = Stopwatch.StartNew();
        var actual = NativeCheckSupport.ReadOutputTables(output);
        var expected = NativeCheckSupport.ExpectedTables(source).ToDictionary(t => t.Name, StringComparer.Ordinal);
        var samples = new List<CheckSample>();
        long failed = 0;

        foreach (var table in source.Tables.OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            if (!expected.ContainsKey(table.Name))
            {
                continue; // in the exclusion list: if the output has it, it is reported below as extra
            }

            if (!actual.TryGetValue(table.Name, out var columns))
            {
                failed++;
                samples.Add(new CheckSample(table.Name, "<table missing>", $"{table.Columns.Count} columns"));
            }
            else if (!columns.SequenceEqual(table.Columns.Select(c => c.Name), StringComparer.Ordinal))
            {
                failed++;
                samples.Add(new CheckSample(
                    table.Name, "columns: " + string.Join(", ", columns), "columns: " + string.Join(", ", table.Columns.Select(c => c.Name))));
            }
        }

        var extra = actual.Keys.Where(name => !expected.ContainsKey(name)).Order(StringComparer.Ordinal).ToList();
        foreach (var name in extra)
        {
            failed++;
            samples.Add(new CheckSample(
                name, "<table present>", NativeExclusions.IsExcluded(name) ? "<excluded table>" : "<not a table of the source>"));
        }

        return CheckResult.FromViolationCount(
            CompletenessId, ValidationPlane.A, ValidationLayer.Critical, AllTables, CompletenessStatement,
            source.Tables.Count + extra.Count, failed, stopwatch.ElapsedMilliseconds, samples);
    }

    /// <summary>A-NAT-02: row count of every exported table against Access <c>COUNT(1)</c>.</summary>
    public static CheckResult RowCounts(SqliteConnection output, OleDbConnection access, NativeCatalog source)
    {
        var stopwatch = Stopwatch.StartNew();
        var actual = NativeCheckSupport.ReadOutputTables(output);
        var tables = NativeCheckSupport.ExpectedTables(source);
        var samples = new List<CheckSample>();
        long failed = 0;

        foreach (var table in tables)
        {
            var expectedRows = NativeCheckSupport.AccessCount(access, table.Name);
            if (!actual.ContainsKey(table.Name))
            {
                failed++;
                samples.Add(new CheckSample(table.Name, "<table missing>", expectedRows.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                continue;
            }

            var rows = NativeCheckSupport.SqliteCount(output, table.Name);
            if (rows != expectedRows)
            {
                failed++;
                samples.Add(new CheckSample(
                    table.Name,
                    rows.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    expectedRows.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }
        }

        return CheckResult.FromViolationCount(
            RowCountId, ValidationPlane.A, ValidationLayer.Critical, AllTables, RowCountStatement,
            tables.Count, failed, stopwatch.ElapsedMilliseconds, samples);
    }

    /// <summary>A-NAT-03: content of every exported table against the source, row by row, one table at a time.</summary>
    public static CheckResult Content(SqliteConnection output, OleDbConnection access, NativeCatalog source)
    {
        var stopwatch = Stopwatch.StartNew();
        var actual = NativeCheckSupport.ReadOutputTables(output);
        var samples = new List<CheckSample>();
        long examined = 0;
        long failed = 0;

        foreach (var table in NativeCheckSupport.ExpectedTables(source))
        {
            if (!actual.TryGetValue(table.Name, out var columns))
            {
                examined++;
                failed++;
                AddSample(samples, new CheckSample(table.Name, "<table missing>", null));
                continue;
            }

            var missingColumns = table.Columns.Select(c => c.Name).Where(c => !columns.Contains(c, StringComparer.Ordinal)).ToList();
            if (missingColumns.Count > 0)
            {
                // The rows cannot be compared without the column; A-NAT-01 names it too.
                examined++;
                failed++;
                AddSample(samples, new CheckSample($"{table.Name}|columns", "<column missing: " + string.Join(", ", missingColumns) + ">", null));
                continue;
            }

            var comparison = NativeContentComparer.Compare(access, output, table, samples);
            examined += comparison.SourceRows + comparison.ExtraRows;
            failed += comparison.Failed;
        }

        return CheckResult.FromViolationCount(
            ContentId, ValidationPlane.A, ValidationLayer.Critical, AllTables, ContentStatement,
            examined, failed, stopwatch.ElapsedMilliseconds, samples);
    }

    /// <summary>A-NAT-04: <c>PRAGMA foreign_key_check</c> returns no rows. No source needed.</summary>
    public static CheckResult ForeignKeys(SqliteConnection output)
    {
        var stopwatch = Stopwatch.StartNew();
        var samples = new List<CheckSample>();
        long failed = 0;

        using (var command = output.CreateCommand())
        {
            command.CommandText = "PRAGMA foreign_key_check";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                failed++;
                if (samples.Count < CheckResult.MaxSamples)
                {
                    var table = reader.GetString(0);
                    var rowid = reader.IsDBNull(1) ? "?" : reader.GetInt64(1).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    var parent = reader.GetString(2);
                    samples.Add(new CheckSample($"{table}|rowid={rowid}", $"no row in {parent}", null));
                }
            }
        }

        // Examined: the (row, relationship) pairs the check looks at.
        long examined = 0;
        foreach (var table in NativeCheckSupport.ReadOutputTables(output).Keys.Order(StringComparer.Ordinal))
        {
            using var list = output.CreateCommand();
            list.CommandText = "SELECT COUNT(1) FROM pragma_foreign_key_list($table)";
            list.Parameters.AddWithValue("$table", table);
            var relationships = Convert.ToInt64(list.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
            if (relationships > 0)
            {
                examined += relationships * NativeCheckSupport.SqliteCount(output, table);
            }
        }

        return CheckResult.FromViolationCount(
            ForeignKeysId, ValidationPlane.A, ValidationLayer.Critical, AllTables, ForeignKeysStatement,
            Math.Max(examined, failed), failed, stopwatch.ElapsedMilliseconds, samples);
    }

    /// <summary>
    /// A-NAT-05: the schema of the output (<c>sqlite_master</c>) against the DDL <see cref="NativeSchemaBuilder"/> builds.
    /// With the source it is built from the source catalogue and the check is critical; without it, from
    /// the baseline, and the check is informative (a difference is then drift, not a defect).
    /// </summary>
    public static CheckResult Schema(SqliteConnection output, NativeCatalog catalog, bool fromSource)
    {
        var stopwatch = Stopwatch.StartNew();
        var layer = fromSource ? ValidationLayer.Critical : ValidationLayer.Informative;
        var statement = fromSource ? SchemaStatement : SchemaStatement + " of the 4.4 baseline (no source: informative)";

        var expected = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var sql in NativeSchemaBuilder.Build(catalog).Statements)
        {
            expected[ObjectKey(sql)] = Normalize(sql);
        }

        var actual = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var command = output.CreateCommand())
        {
            command.CommandText = "SELECT type, name, sql FROM sqlite_master WHERE sql IS NOT NULL AND substr(name, 1, 7) <> 'sqlite_'";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                actual[$"{reader.GetString(0)}:{reader.GetString(1)}"] = Normalize(reader.GetString(2));
            }
        }

        var samples = new List<CheckSample>();
        long failed = 0;
        foreach (var key in expected.Keys.Union(actual.Keys).Order(StringComparer.Ordinal))
        {
            var inExpected = expected.TryGetValue(key, out var wanted);
            var inActual = actual.TryGetValue(key, out var found);
            if (inExpected && inActual && string.Equals(wanted, found, StringComparison.Ordinal))
            {
                continue;
            }

            failed++;
            if (samples.Count < CheckResult.MaxSamples)
            {
                samples.Add(new CheckSample(
                    key,
                    inActual ? NativeCheckSupport.Shorten(found!, 300) : "<missing>",
                    inExpected ? NativeCheckSupport.Shorten(wanted!, 300) : "<not in the expected schema>"));
            }
        }

        return CheckResult.FromViolationCount(
            SchemaId, ValidationPlane.A, layer, "(schema)", statement,
            expected.Keys.Union(actual.Keys).Count(), failed, stopwatch.ElapsedMilliseconds, samples);
    }

    /// <summary>A-DRF-01: the drift of the source catalogue against the baseline. Informative; never fails.</summary>
    public static CheckResult Drift(IReadOnlyList<NativeCatalogDifference> drift)
    {
        var stopwatch = Stopwatch.StartNew();
        var baseline = NativeBaseline.Load().Catalog;
        var objects = baseline.Tables.Count + baseline.ForeignKeys.Count + baseline.Indexes.Count
            + drift.Count(d => d.Kind is NativeCatalogDifferenceKind.TableAdded or NativeCatalogDifferenceKind.ForeignKeyAdded
                or NativeCatalogDifferenceKind.IndexAdded);
        var samples = drift
            .Select(d => new CheckSample($"{d.Kind} {d.Subject}", d.Actual, d.Baseline))
            .ToList();

        return CheckResult.FromViolationCount(
            DriftId, ValidationPlane.A, ValidationLayer.Informative, "(catalogue)", DriftStatement,
            Math.Max(objects, drift.Count), drift.Count, stopwatch.ElapsedMilliseconds, samples);
    }

    private static void AddSample(List<CheckSample> samples, CheckSample sample)
    {
        if (samples.Count < CheckResult.MaxSamples)
        {
            samples.Add(sample);
        }
    }

    /// <summary><c>table:Name</c> or <c>index:Name</c> of a CREATE statement of the builder.</summary>
    private static string ObjectKey(string createStatement)
    {
        var match = CreateStatement().Match(createStatement);
        if (!match.Success)
        {
            throw new InvalidOperationException($"Unexpected DDL statement: {NativeCheckSupport.Shorten(createStatement)}");
        }

        return match.Groups[1].Value.ToLowerInvariant() + ":" + match.Groups[2].Value.Replace("\"\"", "\"", StringComparison.Ordinal);
    }

    private static string Normalize(string sql) => Whitespace().Replace(sql, " ").Trim();

    [GeneratedRegex(@"^CREATE (?:UNIQUE )?(TABLE|INDEX) ""((?:[^""]|"""")*)""")]
    private static partial Regex CreateStatement();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
