using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Core.Validation.Checks;

/// <summary>
/// B-SCH-01: schema identical to the reference with the SCHEMA ROLE (only the 4.2 decides schema;
/// the 3.2/4.0 never do). Compares tables and columns (name and declared type) via
/// <c>pragma_table_info</c>, without opening <c>dpm-distribution-schema.sql</c> again: the 4.2
/// reference IS the contract.
/// </summary>
public static class SchemaCensusChecks
{
    public static CheckResult Run(SqliteConnection generated, SqliteConnection reference, string referenceRole)
    {
        if (!ReferenceRole.IsSchemaRole(referenceRole))
        {
            return CheckResult.Skip(
                "B-SCH-01", ValidationPlane.B, ValidationLayer.Critical, "(schema)",
                "Schema identical to the reference with the schema role",
                $"The given reference ('{referenceRole}') does not have the schema role (only {ReferenceRole.Reference42} decides schema; the 3.2/4.0 have their own, different schemas)");
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var generatedTables = TableNames(generated);
        var referenceTables = TableNames(reference);

        var failures = new List<CheckSample>();
        foreach (var table in referenceTables)
        {
            if (!generatedTables.Contains(table))
            {
                failures.Add(new CheckSample($"table '{table}' absent from the generated output"));
                continue;
            }

            var generatedColumns = ColumnSignature(generated, table);
            var referenceColumns = ColumnSignature(reference, table);
            if (!generatedColumns.SetEquals(referenceColumns))
            {
                var missing = referenceColumns.Except(generatedColumns).Select(c => $"-{c}");
                var extra = generatedColumns.Except(referenceColumns).Select(c => $"+{c}");
                failures.Add(new CheckSample($"{table}: {string.Join(" ", missing.Concat(extra))}"));
            }
        }
        sw.Stop();

        return CheckResult.FromViolationCount(
            "B-SCH-01", ValidationPlane.B, ValidationLayer.Critical, "(schema)",
            "Schema identical to the 4.2 reference: tables and columns (name and type)",
            referenceTables.Count, failures.Count, sw.ElapsedMilliseconds, failures);
    }

    private static HashSet<string> TableNames(SqliteConnection c) =>
        SqlHelpers.Rows(c, "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'", 1)
            .Select(r => r[0]!).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static HashSet<string> ColumnSignature(SqliteConnection c, string table) =>
        SqlHelpers.Rows(c, $"SELECT name, type FROM pragma_table_info('{table}')", 2)
            .Select(r => $"{r[0]}:{r[1]}".ToUpperInvariant())
            .ToHashSet(StringComparer.Ordinal);
}
