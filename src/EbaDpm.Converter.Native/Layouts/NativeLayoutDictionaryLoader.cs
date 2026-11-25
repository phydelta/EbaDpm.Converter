using System.Text.RegularExpressions;
using EbaDpm.Converter.Core.Layouts;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Native.Layouts;

/// <summary>
/// Fills a <see cref="LayoutDictionary"/> from a NATIVE DPM 2.0 output (see
/// docs/target-schema-native.md), the counterpart of the 45-table loader for
/// <c>--extract-layouts --dictionary</c>. The dictionary is a dictionary of SHAPE ("does this code
/// exist?"), so it covers EVERY release of the file (the union of the history, as the dictionary of
/// the latest 45-table release does). It reproduces only what the extractor needs, from the native
/// tables, with the rules of the 45-table mapping (docs/mapping-dpm2.md):
/// <list type="bullet">
/// <item>MET members: the codes of the properties that are metrics (property of a <c>fact</c>
/// variable, or of a non-key header, or whose own code follows the naming law
/// <c>^[A-Za-z]{2}[0-9]+$</c>); every <c>ItemCategory</c> row of a <c>_PR</c> item is a candidate.</item>
/// <item>Dimensions: properties used in <c>ContextComposition</c> or as the property of a
/// <c>key</c> variable, with their <c>ItemCategory</c> codes; the domain is the category of the
/// <c>PropertyCategory</c> row whose window overlaps the one of the code (the first one when none
/// does); <c>_NA</c> is <c>INT_NA</c> for data type <c>i</c> and <c>STR_NA</c> for <c>s</c>.</item>
/// <item>The metric dimension <c>MET</c> (domain <c>MET</c>), synthesised as in the 45-table mapping.</item>
/// <item>(domain, member) pairs: every <c>ItemCategory</c> row outside <c>SE</c>, <c>_NA</c>,
/// <c>_PR</c>, <c>_TE</c>, plus (<c>MET</c>, metric code).</item>
/// <item>Module to framework: <c>ModuleVersion.Code</c> -&gt; <c>Module.FrameworkID</c> -&gt;
/// <c>Framework.Code</c>, without document modules.</item>
/// </list>
/// </summary>
public static class NativeLayoutDictionaryLoader
{
    private static readonly Regex MetricNamePattern = new(
        "^[A-Za-z]{2}[0-9]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> StructuralCategories = new(StringComparer.Ordinal) { "SE", "_NA", "_PR", "_TE" };

    private sealed record ItemRow(long ItemId, long CategoryId, string Code, long Start, long? End);

    private sealed record PropertyWindow(long CategoryId, long Start, long? End);

    public static LayoutDictionary Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var dictionary = new LayoutDictionary(path, () => LoadFrameworksByModuleCode(path));

        using var connection = Open(path);

        var categoryCodeById = new Dictionary<long, string>();
        Query(connection, """SELECT "CategoryID", "Code" FROM "Category" WHERE "Code" IS NOT NULL""", r =>
            categoryCodeById[r.GetInt64(0)] = r.GetString(1));

        var items = new List<ItemRow>();
        Query(connection,
            """
            SELECT "ItemID", "CategoryID", "Code", "StartReleaseID", "EndReleaseID"
            FROM "ItemCategory" WHERE "Code" IS NOT NULL
            ORDER BY "ItemID", "StartReleaseID"
            """,
            r => items.Add(new ItemRow(
                r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetInt64(3), r.IsDBNull(4) ? null : r.GetInt64(4))));

        // Roles of a property, from the variables and headers that use it (all versions: the
        // dictionary covers every release).
        var factProperties = LongSet(connection,
            """
            SELECT DISTINCT vv."PropertyID"
            FROM "VariableVersion" vv JOIN "Variable" v ON v."VariableID" = vv."VariableID"
            WHERE v."Type" = 'fact' AND vv."PropertyID" IS NOT NULL
            """);
        var nonKeyHeaderProperties = LongSet(connection,
            """
            SELECT DISTINCT hv."PropertyID"
            FROM "HeaderVersion" hv JOIN "Header" h ON h."HeaderID" = hv."HeaderID"
            WHERE h."IsKey" = 0 AND hv."PropertyID" IS NOT NULL
            """);
        var dimensionProperties = LongSet(connection,
            """
            SELECT DISTINCT "PropertyID" FROM "ContextComposition" WHERE "PropertyID" IS NOT NULL
            UNION
            SELECT DISTINCT vv."PropertyID"
            FROM "VariableVersion" vv JOIN "Variable" v ON v."VariableID" = vv."VariableID"
            WHERE v."Type" = 'key' AND vv."PropertyID" IS NOT NULL
            """);

        var windowsByProperty = new Dictionary<long, List<PropertyWindow>>();
        Query(connection,
            """
            SELECT "PropertyID", "CategoryID", "StartReleaseID", "EndReleaseID"
            FROM "PropertyCategory" ORDER BY "PropertyID", "StartReleaseID", "CategoryID"
            """,
            r =>
            {
                var propertyId = r.GetInt64(0);
                if (!windowsByProperty.TryGetValue(propertyId, out var list))
                {
                    list = [];
                    windowsByProperty[propertyId] = list;
                }

                list.Add(new PropertyWindow(r.GetInt64(1), r.GetInt64(2), r.IsDBNull(3) ? null : r.GetInt64(3)));
            });

        var dataTypeCodeByProperty = new Dictionary<long, string>();
        Query(connection,
            """
            SELECT p."PropertyID", d."Code"
            FROM "Property" p JOIN "DataType" d ON d."DataTypeID" = p."DataTypeID"
            WHERE d."Code" IS NOT NULL
            """,
            r => dataTypeCodeByProperty[r.GetInt64(0)] = r.GetString(1));

        // The metric dimension: the 45-table mapping synthesises it (DimensionCode MET, domain MET) because
        // DPM 2.0 has no source row for it.
        dictionary.AddDimension("MET", "MET");

        foreach (var item in items)
        {
            if (!categoryCodeById.TryGetValue(item.CategoryId, out var categoryCode))
            {
                continue;
            }

            if (string.Equals(categoryCode, "_PR", StringComparison.Ordinal))
            {
                // Metric identification: role of the property, or the naming law of this very code.
                if (factProperties.Contains(item.ItemId)
                    || nonKeyHeaderProperties.Contains(item.ItemId)
                    || MetricNamePattern.IsMatch(item.Code))
                {
                    dictionary.AddMetMember(item.Code);
                    dictionary.AddDomainMember("MET", item.Code);
                }

                if (dimensionProperties.Contains(item.ItemId))
                {
                    var domainCode = DimensionDomainCode(item, windowsByProperty, categoryCodeById, dataTypeCodeByProperty);

                    // A property whose values live in a structural category (templates as items, in
                    // 4.4: TEM) has no domain, and the 45-table mapping does not emit it as a
                    // dimension either: a single-term "(TEM)" stays a rhetorical parenthesis.
                    if (domainCode is null || !StructuralCategories.Contains(domainCode))
                    {
                        dictionary.AddDimension(item.Code, domainCode);
                    }
                }

                continue;
            }

            if (!StructuralCategories.Contains(categoryCode))
            {
                dictionary.AddDomainMember(categoryCode, item.Code);
            }
        }

        return dictionary;
    }

    /// <summary>The domain code of a dimension code: the category of the PropertyCategory window that
    /// overlaps the window of the code; the first window when none does. <c>_NA</c> is typed by the
    /// property data type (<c>i</c> -&gt; <c>INT_NA</c>, <c>s</c> -&gt; <c>STR_NA</c>); another type has
    /// no domain.</summary>
    private static string? DimensionDomainCode(
        ItemRow item,
        Dictionary<long, List<PropertyWindow>> windowsByProperty,
        Dictionary<long, string> categoryCodeById,
        Dictionary<long, string> dataTypeCodeByProperty)
    {
        if (!windowsByProperty.TryGetValue(item.ItemId, out var windows) || windows.Count == 0)
        {
            return null;
        }

        var window = windows.FirstOrDefault(w =>
            (w.End is null || item.Start < w.End) && (item.End is null || w.Start < item.End)) ?? windows[0];

        if (!categoryCodeById.TryGetValue(window.CategoryId, out var code))
        {
            return null;
        }

        if (!string.Equals(code, "_NA", StringComparison.Ordinal))
        {
            return code;
        }

        return dataTypeCodeByProperty.GetValueOrDefault(item.ItemId) switch
        {
            "i" => "INT_NA",
            "s" => "STR_NA",
            _ => null,
        };
    }

    private static Dictionary<string, HashSet<string>> LoadFrameworksByModuleCode(string path)
    {
        var map = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        using var connection = Open(path);
        Query(connection,
            """
            SELECT DISTINCT mv."Code", f."Code"
            FROM "ModuleVersion" mv
            JOIN "Module" m ON m."ModuleID" = mv."ModuleID"
            JOIN "Framework" f ON f."FrameworkID" = m."FrameworkID"
            WHERE mv."Code" IS NOT NULL AND f."Code" IS NOT NULL AND m."isDocumentModule" = 0
            """,
            r =>
            {
                var moduleCode = r.GetString(0);
                if (!map.TryGetValue(moduleCode, out var frameworks))
                {
                    frameworks = new HashSet<string>(StringComparer.Ordinal);
                    map[moduleCode] = frameworks;
                }

                frameworks.Add(r.GetString(1));
            });
        return map;
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static HashSet<long> LongSet(SqliteConnection connection, string sql)
    {
        var set = new HashSet<long>();
        Query(connection, sql, r => set.Add(r.GetInt64(0)));
        return set;
    }

    private static void Query(SqliteConnection connection, string sql, Action<SqliteDataReader> onRow)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            onRow(reader);
        }
    }
}
