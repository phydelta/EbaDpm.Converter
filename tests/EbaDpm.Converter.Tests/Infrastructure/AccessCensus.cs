using System.Data.OleDb;
using System.Globalization;

namespace EbaDpm.Converter.Tests.Infrastructure;

/// <summary>
/// Independent census of an EBA Access database: plain OLEDB queries on the same <c>.accdb</c> the
/// production reader consumes, opened read-only, that never call production code. A test derives
/// its expected figure here instead of hard-coding a number measured on one release.
///
/// Conventions: use <c>COUNT(1)</c> (never <c>COUNT(*)</c>); look a release up by its
/// <c>Release.Code</c> (<see cref="ReleaseId"/>), never by a hard-coded ID; filter the rows in force
/// at a release with <see cref="InForce"/>. Needs the Access Database Engine (the tests that use it
/// are <c>[DataFact]</c>/<c>[DataTheory]</c>, which run only with the EBA data files).
/// </summary>
internal static class AccessCensus
{
    private static OleDbConnection Open(string accdb)
    {
        var connection = new OleDbConnection($"Provider=Microsoft.ACE.OLEDB.16.0;Data Source={accdb};Mode=Read;");
        connection.Open();
        return connection;
    }

    /// <summary>The single value of a scalar query (a <c>COUNT(1)</c>), as a <see cref="long"/>.</summary>
    public static long Scalar(string accdb, string sql)
    {
        using var connection = Open(accdb);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 600;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>The first column of every row of the query, as integers (streamed, in source order).</summary>
    public static IEnumerable<int> Ints(string accdb, string sql)
    {
        using var connection = Open(accdb);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 600;
        using var rows = command.ExecuteReader();
        while (rows.Read())
        {
            yield return Convert.ToInt32(rows.GetValue(0), CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Two columns (a text business key and an integer) of every row of the query.</summary>
    public static Dictionary<string, int> IntByCode(string accdb, string sql)
    {
        using var connection = Open(accdb);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var rows = command.ExecuteReader();
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        while (rows.Read())
        {
            result.Add(Convert.ToString(rows.GetValue(0), CultureInfo.InvariantCulture)!, Convert.ToInt32(rows.GetValue(1), CultureInfo.InvariantCulture));
        }

        return result;
    }

    /// <summary><c>Release.ReleaseID</c> of the release with the given <c>Release.Code</c>; fails when it does not exist.</summary>
    public static int ReleaseId(string accdb, string releaseCode)
    {
        var byCode = IntByCode(accdb, "SELECT [Code], [ReleaseID] FROM [Release]");
        return byCode.TryGetValue(releaseCode, out var id)
            ? id
            : throw new InvalidOperationException($"Release code '{releaseCode}' is not declared in [Release] of '{Path.GetFileName(accdb)}'.");
    }

    /// <summary>
    /// The "in force at release <paramref name="releaseId"/>" predicate of a versioned table:
    /// <c>StartReleaseID &lt;= R AND (EndReleaseID IS NULL OR EndReleaseID &gt; R)</c>. The optional
    /// <paramref name="alias"/> qualifies the columns (<c>tv</c> gives <c>tv.[StartReleaseID]</c>).
    /// </summary>
    public static string InForce(int releaseId, string? alias = null)
    {
        var q = alias is null ? string.Empty : alias + ".";
        var r = releaseId.ToString(CultureInfo.InvariantCulture);
        return $"{q}[StartReleaseID] <= {r} AND ({q}[EndReleaseID] IS NULL OR {q}[EndReleaseID] > {r})";
    }

    /// <summary>Rows of <c>[table]</c> in force at the release with the given <c>Release.Code</c>.</summary>
    public static long CountInForce(string accdb, string table, string releaseCode)
        => Scalar(accdb, $"SELECT COUNT(1) FROM [{table}] WHERE {InForce(ReleaseId(accdb, releaseCode))}");

    /// <summary>All the rows of <c>[table]</c>.</summary>
    public static long Count(string accdb, string table) => Scalar(accdb, $"SELECT COUNT(1) FROM [{table}]");

    /// <summary>
    /// <c>TableVersion</c> rows in force at the release whose <c>Table</c> is abstract
    /// (<c>Table.IsAbstract</c>), counted straight on the source tables.
    /// </summary>
    public static long CountAbstractTableVersionsInForce(string accdb, string releaseCode)
        => Scalar(
            accdb,
            "SELECT COUNT(1) FROM [TableVersion] tv INNER JOIN [Table] t ON t.[TableID] = tv.[TableID] "
            + $"WHERE t.[IsAbstract] <> 0 AND {InForce(ReleaseId(accdb, releaseCode), "tv")}");
}
