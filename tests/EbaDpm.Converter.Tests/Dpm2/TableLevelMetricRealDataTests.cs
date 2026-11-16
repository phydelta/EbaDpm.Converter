using System.Globalization;
using EbaDpm.Converter.Tests.Infrastructure;
using EbaDpm.Converter.Tests.Schema;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Tests.Dpm2;

/// <summary>
/// The table-level metric (<c>TableVersion.PropertyID</c>) over the per-release files of the 4.2.1
/// database (same single conversion as <see cref="CliPerReleaseDirectoryTests"/>). The table
/// <c>C_96.05.2</c> (qCFB) declares its metric only at table level: its cells' variables carry that
/// same property and no non-key header contributes a metric. Every expectation is derived from the
/// Access database: the property of the table version in force at each release, the code of its
/// <c>_PR</c> item as of that release (resolvable only when its <c>ItemCategory</c> row has started)
/// and the number of live cells. A resolvable release must carry <c>MET(&lt;metric&gt;)|</c> on every
/// non-shaded cell; an unresolvable one must carry nothing invented.
/// </summary>
[Collection("Dpm2PerReleaseDirectory")]
[Trait("Tier", "RealData")]
public sealed class TableLevelMetricRealDataTests(Dpm20PerReleaseDirectoryFixture fixture)
{
    private const string TableCode = "C_96.05.2";

    private static string Accdb => RepoPaths.AccessDpm20DatabasePath;

    private sealed record AccessFacts(int TableVId, int PropertyId, string? MetricCode, long LiveCellsOfTheMetric, long LiveCells);

    /// <summary>What Access declares for <c>C_96.05.2</c> as of release <paramref name="releaseId"/>; null when no table version is in force.</summary>
    private static AccessFacts? FactsAsOf(int releaseId)
    {
        var versions = AccessCensus.Rows(
            Accdb,
            $"SELECT tv.[TableVID], tv.[PropertyID] FROM [TableVersion] tv WHERE tv.[Code] = '{TableCode}' AND {AccessCensus.InForce(releaseId, "tv")}");
        if (versions.Count == 0)
        {
            return null;
        }

        var tableVId = Convert.ToInt32(versions.Single()[0], CultureInfo.InvariantCulture);
        if (versions.Single()[1] is DBNull)
        {
            return null;
        }

        var propertyId = Convert.ToInt32(versions.Single()[1], CultureInfo.InvariantCulture);

        // The _PR code of the property as of the release: the dictionary is read with StartReleaseID <= R.
        var codes = AccessCensus.Rows(
            Accdb,
            "SELECT ic.[Code] FROM [ItemCategory] ic INNER JOIN [Category] c ON c.[CategoryID] = ic.[CategoryID] " +
            $"WHERE ic.[ItemID] = {propertyId} AND c.[Code] = '_PR' AND ic.[StartReleaseID] <= {releaseId}");
        var metricCode = codes.Count == 0 ? null : Convert.ToString(codes[0][0], CultureInfo.InvariantCulture);

        var live = AccessCensus.Scalar(
            Accdb,
            $"SELECT COUNT(1) FROM [TableVersionCell] WHERE [TableVID] = {tableVId} AND [IsExcluded] = 0 AND [IsVoid] = 0");
        var liveOfMetric = AccessCensus.Scalar(
            Accdb,
            "SELECT COUNT(1) FROM [TableVersionCell] tvc INNER JOIN [VariableVersion] vv ON vv.[VariableVID] = tvc.[VariableVID] " +
            $"WHERE tvc.[TableVID] = {tableVId} AND tvc.[IsExcluded] = 0 AND tvc.[IsVoid] = 0 AND vv.[PropertyID] = {propertyId}");
        return new AccessFacts(tableVId, propertyId, metricCode, liveOfMetric, live);
    }

    private static List<string?> NonShadedDps(string path)
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT tc.\"DPS\" FROM \"mTableCell\" tc JOIN \"mTable\" t ON t.\"TableID\" = tc.\"TableID\" " +
            $"WHERE t.\"TableCode\" = '{TableCode}' AND tc.\"IsShaded\" = 0";
        using var reader = command.ExecuteReader();
        var list = new List<string?>();
        while (reader.Read())
        {
            list.Add(reader.IsDBNull(0) ? null : reader.GetString(0));
        }

        return list;
    }

    private static string? MetricXbrlCode(string path, string metricCode)
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT m.\"MemberXBRLCode\" FROM \"mMember\" m JOIN \"mDomain\" d ON d.\"DomainID\" = m.\"DomainID\" " +
            "WHERE d.\"DomainCode\" = 'MET' AND m.\"MemberCode\" = $code";
        command.Parameters.AddWithValue("$code", metricCode);
        return command.ExecuteScalar() as string;
    }

    [DataFact]
    public void C960502_EveryNonShadedCell_CarriesTheTableMetricWhenItResolvesAtTheRelease_AndNothingElseDoes()
    {
        Assert.True(fixture.ExitCode == 0, $"exit={fixture.ExitCode}; stderr: {fixture.Stderr}");

        var problems = new List<string>();
        var resolvedFilesChecked = new List<string>();
        var unresolvedFilesChecked = new List<string>();
        foreach (var (code, id, exportable) in Dpm20PerReleaseDirectoryFixture.ReleasesFromAccess().Where(r => r.Exportable > 0))
        {
            var facts = FactsAsOf(id);
            if (facts is null)
            {
                continue;
            }

            var path = fixture.FileOf(code);
            var dps = NonShadedDps(path);
            if (dps.Count == 0)
            {
                // Table version in force at R but its module version starts earlier: the file of R does not hold it.
                continue;
            }

            if (dps.Count != facts.LiveCells)
            {
                problems.Add($"dpm-{code}.db {TableCode}: {dps.Count} non-shaded cell(s), Access declares {facts.LiveCells} live cell(s).");
            }

            if (facts.MetricCode is null)
            {
                unresolvedFilesChecked.Add(code);
                var invented = dps.Count(d => d is not null && d.StartsWith("MET(", StringComparison.Ordinal));
                if (invented != 0)
                {
                    problems.Add($"dpm-{code}.db {TableCode}: metric property {facts.PropertyId} has no _PR item as of the release, yet {invented} cell(s) start with 'MET(' (invented).");
                }

                continue;
            }

            resolvedFilesChecked.Add(code);
            var xbrl = MetricXbrlCode(path, facts.MetricCode);
            if (xbrl is null)
            {
                problems.Add($"dpm-{code}.db: the metric '{facts.MetricCode}' of {TableCode} is not a member of the MET domain.");
                continue;
            }

            var prefix = $"MET({xbrl})";
            var wrong = dps.Where(d => d is null || !(d == prefix || d.StartsWith(prefix + "|", StringComparison.Ordinal))).ToList();
            if (wrong.Count != 0)
            {
                problems.Add($"dpm-{code}.db {TableCode}: {wrong.Count}/{dps.Count} non-shaded cell(s) do not start with '{prefix}' (first: {wrong[0] ?? "NULL"}).");
            }

            if (facts.LiveCellsOfTheMetric != facts.LiveCells)
            {
                problems.Add($"{TableCode} at {code}: only {facts.LiveCellsOfTheMetric}/{facts.LiveCells} live cells' variables carry the table property in Access; the table-level reading is not what Access declares for all of them.");
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        // Both sides of the rule must have been exercised, otherwise a zero proves nothing.
        Assert.True(resolvedFilesChecked.Count >= 1, "No file holds C_96.05.2 with a resolvable table-level metric.");
        Assert.True(unresolvedFilesChecked.Count >= 1, "No file holds C_96.05.2 with an unresolvable table-level metric (the 'nothing invented' side was not exercised).");
    }
}
