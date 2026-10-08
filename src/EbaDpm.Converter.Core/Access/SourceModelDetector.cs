using System.Data;
using System.Data.OleDb;

namespace EbaDpm.Converter.Core.Access;

/// <summary>
/// Data model of the source Access database. DPM 1.0 and DPM 2.0 are structurally different
/// and have separate reading and mapping pipelines, with no common interface.
/// </summary>
public enum DpmSourceModel
{
    /// <summary>The classic Access database (<c>DPM 1.0 Database_v4_1_20250709.accdb</c> and similar).</summary>
    Dpm10,

    /// <summary>The Access database of the new model (<c>DPM2 Database_v 4_2_20251125.accdb</c> and similar).</summary>
    Dpm20,
}

/// <summary>
/// Raised when the DPM model of the source Access database cannot be determined. Auto-detection
/// is the only branching point between the two pipelines: it is used by <c>CliRunner</c> and
/// nobody else.
/// </summary>
public sealed class SourceModelDetectionException : Exception
{
    public SourceModelDetectionException(string message)
        : base(message)
    {
    }

    public SourceModelDetectionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Detects the DPM model of the <c>.accdb</c> by querying the CATALOG
/// (<see cref="OleDbConnection.GetSchema(string)"/>, filtered to <c>TABLE_TYPE = "TABLE"</c>: the
/// <c>MSys*</c> system tables come as <c>ACCESS TABLE</c> / <c>SYSTEM TABLE</c> and are already
/// excluded), NEVER the data: not a single content row is read to decide.
///
/// Rule: <c>Category</c> present and <c>Domain</c> absent => DPM 2.0; <c>Domain</c> present =>
/// DPM 1.0 (taking precedence over <c>Category</c>, in case they ever coexisted); neither =>
/// explicit exception. Never a default assumption.
///
/// Both <c>Category</c> AND the absence of <c>Domain</c> are required, not just one of the two
/// conditions: the 2014 Access database (DPM Database 2.1.0.1.accdb) is DPM 1.0 with a different
/// schema, and a single-table test could silently misclassify it.
///
/// Table-name comparison is case-insensitive.
///
/// EVERY failure of <see cref="Detect"/> names the file, not only the "readable catalog without
/// any marker" case. If the file does not even open as Access (wrong path, a <c>.db</c> instead
/// of an <c>.accdb</c>, a truncated file), ACE OLEDB rejects the connection BEFORE reaching the
/// catalog, and its message does not interpolate the path. For that reason opening the
/// connection and reading the catalog are wrapped in <c>try/catch</c> and the failure is
/// re-raised as <see cref="SourceModelDetectionException"/> with the path, what was expected
/// (a DPM 1.0 or DPM 2.0 <c>.accdb</c>) and the original ACE message chained as
/// <c>InnerException</c>: this distinguishes "the file does not exist" from "it exists but is
/// not a database", and that information must not be lost.
/// </summary>
public static class SourceModelDetector
{
    public static DpmSourceModel Detect(string accdbPath)
    {
        if (string.IsNullOrWhiteSpace(accdbPath))
        {
            throw new ArgumentException("The .accdb file path cannot be empty.", nameof(accdbPath));
        }

        var connectionString =
            $"Provider=Microsoft.ACE.OLEDB.16.0;Data Source={accdbPath};Persist Security Info=False;";

        HashSet<string> tableNames;
        try
        {
            using var connection = new OleDbConnection(connectionString);
            connection.Open();
            tableNames = ReadUserTableNames(connection);
        }
        catch (Exception ex) when (ex is OleDbException or InvalidOperationException)
        {
            throw new SourceModelDetectionException(
                $"Could not open '{accdbPath}' as an Access database. Expected a DPM 1.0 "
                + ".accdb ('Domain' table) or a DPM 2.0 .accdb ('Category' table without "
                + $"'Domain'). Original provider message: {ex.Message}",
                ex);
        }

        var hasDomain = tableNames.Contains("Domain");
        var hasCategory = tableNames.Contains("Category");

        if (hasDomain)
        {
            return DpmSourceModel.Dpm10;
        }

        if (hasCategory)
        {
            return DpmSourceModel.Dpm20;
        }

        throw new SourceModelDetectionException(
            $"Could not determine the DPM model of source '{accdbPath}': it has no 'Domain' "
            + "table (DPM 1.0 marker) and no 'Category' table without 'Domain' (DPM 2.0 marker). "
            + "The file matches neither expected format; no default is assumed.");
    }

    private static HashSet<string> ReadUserTableNames(OleDbConnection connection)
    {
        // Catalog, not data: GetSchema("Tables") does not touch the content of any table.
        var schema = connection.GetSchema("Tables");
        var tableNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (DataRow row in schema.Rows)
        {
            var tableType = row["TABLE_TYPE"] as string;
            if (!string.Equals(tableType, "TABLE", StringComparison.Ordinal))
            {
                // Skip MSys* ("ACCESS TABLE" / "SYSTEM TABLE") and any view: only user tables
                // are relevant to the detection rule.
                continue;
            }

            if (row["TABLE_NAME"] is string name && !string.IsNullOrEmpty(name))
            {
                tableNames.Add(name);
            }
        }

        return tableNames;
    }
}
