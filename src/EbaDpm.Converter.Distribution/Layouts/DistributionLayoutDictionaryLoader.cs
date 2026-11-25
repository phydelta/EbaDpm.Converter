using EbaDpm.Converter.Core.Layouts;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Distribution.Layouts;

/// <summary>
/// Fills a <see cref="LayoutDictionary"/> from ANY SQLite database with the 45-table distribution
/// schema: a reference database, or the converter's own generated output — the extractor does not
/// impose which one, because this is a MECHANICAL query of which codes exist, not an arbitration
/// of content (disputes about the VALUE of a cell are a different matter from which table to use as
/// the list of valid codes to disambiguate a syntactic shape).
///
/// With a null <c>path</c> it returns an absent dictionary (see <see cref="LayoutDictionary"/>).
/// </summary>
public static class DistributionLayoutDictionaryLoader
{
    public static LayoutDictionary Load(string? path)
    {
        if (path is null)
        {
            return new LayoutDictionary(null);
        }

        var dictionary = new LayoutDictionary(path, () => LoadFrameworksByModuleCode(path));

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString();

        using var connection = new SqliteConnection(connectionString);
        connection.Open();

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText =
                """
                SELECT m."MemberCode"
                FROM "mMember" m
                JOIN "mDomain" d ON d."DomainID" = m."DomainID"
                WHERE d."DomainCode" = 'MET' AND m."MemberCode" IS NOT NULL
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                dictionary.AddMetMember(reader.GetString(0));
            }
        }

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText =
                """
                SELECT dim."DimensionCode", dom."DomainCode"
                FROM "mDimension" dim
                LEFT JOIN "mDomain" dom ON dom."DomainID" = dim."DomainID"
                WHERE dim."DimensionCode" IS NOT NULL
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var code = reader.GetString(0);
                var domainCode = reader.IsDBNull(1) ? null : reader.GetString(1);
                // The same DimensionCode may be repeated (several versions of the common base):
                // the dictionary keeps the first.
                dictionary.AddDimension(code, domainCode);
            }
        }

        using (var cmd = connection.CreateCommand())
        {
            // Real (domain, member) pairs — to validate the "(domain:member)" form: a two-code
            // value paired POSITIONALLY with a declaration is not, by that alone, a real DPM
            // value. "ZZFAKE:ZZFAKE2" has the right shape and neither code exists.
            cmd.CommandText =
                """
                SELECT dom."DomainCode", m."MemberCode"
                FROM "mMember" m
                JOIN "mDomain" dom ON dom."DomainID" = m."DomainID"
                WHERE dom."DomainCode" IS NOT NULL AND m."MemberCode" IS NOT NULL
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                dictionary.AddDomainMember(reader.GetString(0), reader.GetString(1));
            }
        }

        return dictionary;
    }

    private static Dictionary<string, HashSet<string>> LoadFrameworksByModuleCode(string path)
    {
        var map = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString();

        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT m."ModuleCode", f."FrameworkCode"
            FROM "mModule" m
            JOIN "mTaxonomy" t ON t."TaxonomyID" = m."TaxonomyID"
            JOIN "mReportingFramework" f ON f."FrameworkID" = t."FrameworkID"
            WHERE m."ModuleCode" IS NOT NULL AND f."FrameworkCode" IS NOT NULL
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var moduleCode = reader.GetString(0);
            if (!map.TryGetValue(moduleCode, out var frameworks))
            {
                frameworks = new HashSet<string>(StringComparer.Ordinal);
                map[moduleCode] = frameworks;
            }

            frameworks.Add(reader.GetString(1));
        }

        return map;
    }
}
