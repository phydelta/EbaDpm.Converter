using EbaDpm.Converter.Core.Validation.PlaneC;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Native.PlaneC;

/// <summary>One metric of the layouts annotated with a hierarchy on an ordinate:
/// <c>(qAYV) label[qCO:new_CO5]</c>. <see cref="BusinessKey"/> is the shape of a <c>hierarchy</c> row of
/// the census.</summary>
internal sealed record NativeHierarchyUnit(
    string ReleaseLabel, string TableCode, string Axis, string OrdinateCode, string MetricCode,
    string LayoutDomainCode, string LayoutHierarchyCode)
{
    public string BusinessKey => $"{TableCode}\t{ReleaseLabel}|{Axis}|{OrdinateCode}|{MetricCode}";

    public string LayoutHierarchy => $"{LayoutDomainCode}:{LayoutHierarchyCode}";
}

internal sealed record NativeHierarchyViolation(NativeHierarchyUnit Unit, string NativeHierarchy);

internal sealed record NativeHierarchyResult(
    string? SkipReason,
    long UnitsTotal,
    IReadOnlyDictionary<string, long> ComparedByRelease,
    IReadOnlyList<NativeHierarchyUnit> MatchedWithoutHierarchy,
    long NotMatched,
    IReadOnlyDictionary<string, long> NotMatchedByReason,
    IReadOnlyList<string> NotMatchedSamples,
    long SheetLevelNotExamined,
    IReadOnlyList<NativeHierarchyViolation> Violations);

/// <summary>
/// The hierarchy of an enumerated metric, per use. The layouts publish it on the metric ordinate
/// (<c>[domain:hierarchy]</c>); the native model carries it on the non-key header:
/// <c>HeaderVersion.SubCategoryVID</c> -&gt; <c>SubCategoryVersion</c> -&gt; <c>SubCategory.Code</c> (the
/// hierarchy) and <c>SubCategory.CategoryID</c> -&gt; <c>Category.Code</c> (the domain).
///
/// A unit is one <c>LayoutValue</c> with <c>DomainCode = 'MET'</c>, a hierarchy and an ordinate. It is
/// resolved at the release of its sheet: the table version with that code valid at the release, and in
/// it the non-key headers with the ordinate's code and direction whose <c>PropertyID</c> has the layout
/// metric code at that release. A unit with no such header is not matched (reason named); one whose header
/// has no sub-category is matched without hierarchy (not a violation); the others are compared.
/// </summary>
internal static class NativeHierarchyChecker
{
    public static NativeHierarchyResult Check(
        SqliteConnection native,
        SqliteConnection layouts,
        IReadOnlyDictionary<string, (long ReleaseId, NativeSignatureBuilder Builder)> releases,
        NativeTableVersions tableVersions,
        NativeStructure structure,
        NativeItemCodes itemCodes)
    {
        if (!NativeSql.HasColumn(layouts, "LayoutValue", "HierarchyCode")
            || !NativeSql.HasColumn(layouts, "LayoutValue", "HierarchyDomainCode"))
        {
            return Skipped(
                "the layouts repository was built before LayoutValue stored the metric bracket (columns " +
                "HierarchyDomainCode/HierarchyCode absent): re-extract the layouts to evaluate it");
        }

        var (units, sheetLevel) = LoadUnits(layouts);
        var hierarchyByVid = LoadHierarchies(native);

        long notMatched = 0;
        var compared = new Dictionary<string, long>(StringComparer.Ordinal);
        var withoutHierarchy = new List<NativeHierarchyUnit>();
        var violations = new List<NativeHierarchyViolation>();
        var notMatchedByReason = new Dictionary<string, long>(StringComparer.Ordinal);
        var samples = new List<string>();

        foreach (var unit in units)
        {
            var (reason, headers) = Resolve(unit, releases, tableVersions, structure, itemCodes);
            if (reason is not null)
            {
                notMatched++;
                notMatchedByReason[reason] = notMatchedByReason.GetValueOrDefault(reason) + 1;
                if (samples.Count < 50)
                {
                    samples.Add($"{unit.BusinessKey} [{reason}]");
                }

                continue;
            }

            var nativeHierarchies = new List<(string Domain, string Code)>();
            var unresolvedVersions = 0;
            foreach (var header in headers)
            {
                if (header.SubCategoryVid is { } vid && hierarchyByVid.TryGetValue(vid, out var hierarchy))
                {
                    nativeHierarchies.Add(hierarchy);
                }
                else if (header.SubCategoryVid is not null)
                {
                    unresolvedVersions++;
                }
            }

            if (nativeHierarchies.Count == 0 && unresolvedVersions > 0)
            {
                notMatched++;
                const string reasonText = "sub-category version of the header not found";
                notMatchedByReason[reasonText] = notMatchedByReason.GetValueOrDefault(reasonText) + 1;
                if (samples.Count < 50)
                {
                    samples.Add($"{unit.BusinessKey} [{reasonText}]");
                }

                continue;
            }

            if (nativeHierarchies.Count == 0)
            {
                withoutHierarchy.Add(unit);
                continue;
            }

            compared[unit.ReleaseLabel] = compared.GetValueOrDefault(unit.ReleaseLabel) + 1;
            var layoutDomain = PlaneCSignature.StripEbaPrefix(unit.LayoutDomainCode);
            var differing = nativeHierarchies.FindIndex(h =>
                !string.Equals(h.Code, unit.LayoutHierarchyCode, StringComparison.Ordinal)
                || !string.Equals(PlaneCSignature.StripEbaPrefix(h.Domain), layoutDomain, StringComparison.Ordinal));
            if (differing >= 0)
            {
                var h = nativeHierarchies[differing];
                violations.Add(new NativeHierarchyViolation(unit, $"{h.Domain}:{h.Code}"));
            }
        }

        return new NativeHierarchyResult(
            null, units.Count, compared, withoutHierarchy, notMatched, notMatchedByReason, samples, sheetLevel, violations);
    }

    private static NativeHierarchyResult Skipped(string reason) =>
        new(reason, 0, new Dictionary<string, long>(), [], 0, new Dictionary<string, long>(), [], 0, []);

    private static (string? Reason, List<NativeHeader> Headers) Resolve(
        NativeHierarchyUnit unit,
        IReadOnlyDictionary<string, (long ReleaseId, NativeSignatureBuilder Builder)> releases,
        NativeTableVersions tableVersions,
        NativeStructure structure,
        NativeItemCodes itemCodes)
    {
        if (!releases.TryGetValue(unit.ReleaseLabel, out var release))
        {
            return ("release not in the file", []);
        }

        switch (tableVersions.Find(unit.TableCode, release.ReleaseId, out var tableVid))
        {
            case TableVersionLookup.NotValidAtRelease:
                return ("no table version at the release", []);
            case TableVersionLookup.Several:
                return ("several table versions at the release", []);
        }

        if (!structure.Tables.TryGetValue(tableVid, out var table))
        {
            return ("no table version at the release", []);
        }

        var onAxis = table.Headers
            .Where(h => !h.IsKey
                        && string.Equals(h.Direction, unit.Axis, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(h.Code, unit.OrdinateCode, StringComparison.Ordinal))
            .ToList();
        if (onAxis.Count == 0)
        {
            return ("ordinate not found among the non-key headers of the axis", []);
        }

        var metricCode = PlaneCSignature.StripEbaPrefix(unit.MetricCode);
        var matched = onAxis.Where(h =>
            h.PropertyId is { } propertyId
            && itemCodes.TryGet(propertyId, release.ReleaseId, out var code, out _) == ItemCodeStatus.Found
            && string.Equals(PlaneCSignature.StripEbaPrefix(code), metricCode, StringComparison.Ordinal)).ToList();
        return matched.Count == 0 ? ("header without that metric", []) : (null, matched);
    }

    /// <summary>The distinct layout units and the number of sheet-level metric values (no ordinate) not examined.</summary>
    private static (List<NativeHierarchyUnit> Units, long SheetLevel) LoadUnits(SqliteConnection layouts)
    {
        long sheetLevel = 0;
        var seen = new HashSet<NativeHierarchyUnit>();
        var units = new List<NativeHierarchyUnit>();
        NativeSql.Query(
            layouts,
            """
            SELECT f."ReleaseLabel", s."TableCode", o."Axis", o."OrdinateCode",
                   v."MemberCode", v."HierarchyDomainCode", v."HierarchyCode", v."OrdinateId"
            FROM "LayoutValue" v
            JOIN "LayoutSheet" s ON s."SheetId" = v."SheetId"
            JOIN "LayoutFile" f ON f."FileId" = s."FileId"
            LEFT JOIN "LayoutOrdinate" o ON o."OrdinateId" = v."OrdinateId"
            WHERE v."DomainCode" = 'MET' AND v."HierarchyCode" IS NOT NULL AND v."MemberCode" IS NOT NULL
            ORDER BY s."TableCode", f."ReleaseLabel", o."Axis", o."OrdinateCode", v."MemberCode"
            """,
            r =>
            {
                if (r.IsDBNull(7) || r.IsDBNull(2) || r.IsDBNull(3))
                {
                    sheetLevel++;
                    return;
                }

                var unit = new NativeHierarchyUnit(
                    r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3).Trim(), r.GetString(4),
                    r.IsDBNull(5) ? string.Empty : r.GetString(5), r.GetString(6));
                if (seen.Add(unit))
                {
                    units.Add(unit);
                }
            });
        return (units, sheetLevel);
    }

    /// <summary><c>SubCategoryVID</c> -&gt; (<c>Category.Code</c>, <c>SubCategory.Code</c>).</summary>
    private static Dictionary<long, (string Domain, string Code)> LoadHierarchies(SqliteConnection native)
    {
        var result = new Dictionary<long, (string, string)>();
        NativeSql.Query(
            native,
            """
            SELECT scv."SubCategoryVID", c."Code", sc."Code"
            FROM "SubCategoryVersion" scv
            JOIN "SubCategory" sc ON sc."SubCategoryID" = scv."SubCategoryID"
            JOIN "Category" c ON c."CategoryID" = sc."CategoryID"
            WHERE c."Code" IS NOT NULL AND sc."Code" IS NOT NULL
            """,
            r => result[r.GetInt64(0)] = (r.GetString(1), r.GetString(2)));
        return result;
    }
}
