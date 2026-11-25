using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Native.Validation;

/// <summary>
/// Tells a native DPM 2.0 output from a 45-table distribution output. It looks at the catalogue of
/// the SQLite file (<c>sqlite_master</c>) and never at data: a native output has the tables
/// <c>Category</c> and <c>TableVersionCell</c> and has no <c>mModule</c>; the distribution schema is
/// the other way round.
/// </summary>
public static class NativeOutputDetector
{
    public static bool IsNativeOutput(string sqlitePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sqlitePath);

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = sqlitePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString();
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        return IsNativeOutput(connection);
    }

    public static bool IsNativeOutput(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name IN ('Category', 'TableVersionCell', 'mModule')";
        var names = new HashSet<string>(StringComparer.Ordinal);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        return names.Contains("Category") && names.Contains("TableVersionCell") && !names.Contains("mModule");
    }
}
