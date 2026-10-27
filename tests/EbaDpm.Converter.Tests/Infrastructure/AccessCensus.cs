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

    /// <summary>Every column of every row of the query (values as read by OLEDB; NULL is <see cref="DBNull"/>).</summary>
    public static List<object[]> Rows(string accdb, string sql)
    {
        using var connection = Open(accdb);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 600;
        using var rows = command.ExecuteReader();
        var result = new List<object[]>();
        while (rows.Read())
        {
            var values = new object[rows.FieldCount];
            rows.GetValues(values);
            result.Add(values);
        }

        return result;
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

    /// <summary>
    /// The DPM 2.0 taxonomy rule (issue 66), derived straight from the source tables with OLEDB and
    /// without calling production code: the module versions in force at the cutoff release whose
    /// module is not a document module and whose framework is not DORA are the EMITTED module
    /// versions; the taxonomy of each one is (lower-case <c>Framework.Code</c>, <c>Release.Code</c>
    /// of its <c>StartReleaseID</c>). Returns, per taxonomy code (<c>"corep 4.2"</c>), the upper-case
    /// codes of its module versions.
    /// </summary>
    public static Dictionary<string, SortedSet<string>> EmittedModuleCodesByTaxonomy(string accdb, string cutoffReleaseCode)
    {
        var inForce = InForce(ReleaseId(accdb, cutoffReleaseCode), "mv");
        var rows = Rows(
            accdb,
            "SELECT f.[Code], r.[Code], mv.[Code] "
            + "FROM (([ModuleVersion] mv INNER JOIN [Module] m ON m.[ModuleID] = mv.[ModuleID]) "
            + "INNER JOIN [Framework] f ON f.[FrameworkID] = m.[FrameworkID]) "
            + "INNER JOIN [Release] r ON r.[ReleaseID] = mv.[StartReleaseID] "
            + $"WHERE {inForce} AND m.[isDocumentModule] = 0 AND f.[Code] <> 'DORA'");

        var result = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var code = Convert.ToString(row[0], CultureInfo.InvariantCulture)!.ToLowerInvariant()
                + " " + Convert.ToString(row[1], CultureInfo.InvariantCulture);
            if (!result.TryGetValue(code, out var modules))
            {
                modules = new SortedSet<string>(StringComparer.Ordinal);
                result[code] = modules;
            }

            modules.Add(Convert.ToString(row[2], CultureInfo.InvariantCulture)!.ToUpperInvariant());
        }

        return result;
    }

    /// <summary>
    /// Second, independent derivation of the tables of each taxonomy (same grouping as
    /// <see cref="EmittedModuleCodesByTaxonomy"/>): the non-abstract table versions in force that
    /// the emitted module versions compose, plus, for each ABSTRACT table version they compose, the
    /// non-abstract table versions in force whose <c>AbstractTableID</c> is that table. Returns the
    /// set of <c>TableVersion.Code</c> per taxonomy code (a table code is unique among the versions
    /// in force).
    /// </summary>
    public static Dictionary<string, SortedSet<string>> TableCodesByTaxonomy(string accdb, string cutoffReleaseCode)
    {
        var releaseId = ReleaseId(accdb, cutoffReleaseCode);
        var mvInForce = InForce(releaseId, "mv");
        var tvInForce = InForce(releaseId, "tv");

        var taxonomyByModuleVid = new Dictionary<int, string>();
        foreach (var row in Rows(
            accdb,
            "SELECT mv.[ModuleVID], f.[Code], r.[Code] "
            + "FROM (([ModuleVersion] mv INNER JOIN [Module] m ON m.[ModuleID] = mv.[ModuleID]) "
            + "INNER JOIN [Framework] f ON f.[FrameworkID] = m.[FrameworkID]) "
            + "INNER JOIN [Release] r ON r.[ReleaseID] = mv.[StartReleaseID] "
            + $"WHERE {mvInForce} AND m.[isDocumentModule] = 0 AND f.[Code] <> 'DORA'"))
        {
            taxonomyByModuleVid[Convert.ToInt32(row[0], CultureInfo.InvariantCulture)] =
                Convert.ToString(row[1], CultureInfo.InvariantCulture)!.ToLowerInvariant()
                + " " + Convert.ToString(row[2], CultureInfo.InvariantCulture);
        }

        // Table versions in force: (TableVID, Code, TableID, AbstractTableID, IsAbstract).
        var versions = Rows(
            accdb,
            "SELECT tv.[TableVID], tv.[Code], tv.[TableID], tv.[AbstractTableID], t.[IsAbstract] "
            + "FROM [TableVersion] tv INNER JOIN [Table] t ON t.[TableID] = tv.[TableID] "
            + $"WHERE {tvInForce}");
        var byVid = versions.ToDictionary(v => Convert.ToInt32(v[0], CultureInfo.InvariantCulture));
        bool IsAbstract(object[] v) => Convert.ToInt32(v[4], CultureInfo.InvariantCulture) != 0;

        var result = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var taxonomyCode in taxonomyByModuleVid.Values.Distinct())
        {
            result[taxonomyCode] = new SortedSet<string>(StringComparer.Ordinal);
        }

        foreach (var row in Rows(accdb, "SELECT [ModuleVID], [TableVID] FROM [ModuleVersionComposition]"))
        {
            if (!taxonomyByModuleVid.TryGetValue(Convert.ToInt32(row[0], CultureInfo.InvariantCulture), out var taxonomyCode)
                || !byVid.TryGetValue(Convert.ToInt32(row[1], CultureInfo.InvariantCulture), out var version))
            {
                continue;
            }

            if (!IsAbstract(version))
            {
                result[taxonomyCode].Add(Convert.ToString(version[1], CultureInfo.InvariantCulture)!);
                continue;
            }

            var abstractTableId = Convert.ToInt32(version[2], CultureInfo.InvariantCulture);
            foreach (var child in versions)
            {
                if (!IsAbstract(child)
                    && child[3] is not DBNull
                    && Convert.ToInt32(child[3], CultureInfo.InvariantCulture) == abstractTableId)
                {
                    result[taxonomyCode].Add(Convert.ToString(child[1], CultureInfo.InvariantCulture)!);
                }
            }
        }

        return result;
    }
}
