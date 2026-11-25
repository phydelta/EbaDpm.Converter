using System.Data.OleDb;
using System.Globalization;
using EbaDpm.Converter.Native.Catalog;
using EbaDpm.Converter.Native.Schema;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Native.Validation;

/// <summary>Small helpers shared by the native plane A checks.</summary>
internal static class NativeCheckSupport
{
    public const string NeedsSource = "needs --source <accdb>";

    /// <summary>The tables the output must hold: the source catalogue minus the closed exclusion list, ordinal by name.</summary>
    public static IReadOnlyList<NativeTable> ExpectedTables(NativeCatalog source)
        => source.Tables
            .Where(t => !NativeExclusions.IsExcluded(t.Name))
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToList();

    /// <summary>An Access SQL identifier in square brackets.</summary>
    public static string AccessName(string name)
    {
        if (name.Contains('[') || name.Contains(']'))
        {
            throw new NotSupportedException($"The Access name '{name}' contains a square bracket and cannot be quoted.");
        }

        return "[" + name + "]";
    }

    public static long AccessCount(OleDbConnection access, string table)
    {
        using var command = new OleDbCommand($"SELECT COUNT(1) FROM {AccessName(table)}", access) { CommandTimeout = 0 };
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public static long SqliteCount(SqliteConnection output, string table)
    {
        using var command = output.CreateCommand();
        command.CommandText = $"SELECT COUNT(1) FROM {NativeSchemaBuilder.Quote(table)}";
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>The user tables of the output (name, columns in declared order). <c>sqlite_*</c> objects are not user tables.</summary>
    public static Dictionary<string, IReadOnlyList<string>> ReadOutputTables(SqliteConnection output)
    {
        var names = new List<string>();
        using (var command = output.CreateCommand())
        {
            command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND substr(name, 1, 7) <> 'sqlite_'";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                names.Add(reader.GetString(0));
            }
        }

        var tables = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            using var command = output.CreateCommand();
            command.CommandText = "SELECT name FROM pragma_table_info($table) ORDER BY cid";
            command.Parameters.AddWithValue("$table", name);
            var columns = new List<string>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                columns.Add(reader.GetString(0));
            }

            tables[name] = columns;
        }

        return tables;
    }

    public static string Shorten(string text, int max = 120)
        => text.Length <= max ? text : text[..max] + "...";
}
