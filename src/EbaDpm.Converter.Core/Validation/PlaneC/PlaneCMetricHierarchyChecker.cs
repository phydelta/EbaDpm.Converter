using EbaDpm.Converter.Core.Layouts;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Core.Validation.PlaneC;

/// <summary>
/// The hierarchy restriction of an enumerated metric, per USE: the Annotated Table Layouts publish
/// it on the metric column/row annotation (<c>(qAYV) label[qCO:new_CO5]</c>), and the output carries
/// it in <c>mOrdinateMetricRestriction</c> (per ordinate) or, failing that, in
/// <c>mMetric.ReferencedHierarchyID</c>.
///
/// Unit = one <c>LayoutValue</c> row with <c>DomainCode = 'MET'</c>, a <c>HierarchyCode</c> and an
/// <c>OrdinateId</c>. Everything is compared BY BUSINESS KEY, never by surrogate ID:
/// <list type="bullet">
/// <item>layout sheet -> generated table(s): the table CODE and the SAME release pairing as the rest
/// of plane C (<see cref="PlaneCReleaseMatcher.SelectTableIds"/>, including the <c>REL.n -> REL</c>
/// tolerance and the precedence of exact pairings), applied per TableID like
/// <see cref="PlaneCEqualityChecker"/>;</item>
/// <item>layout ordinate (<c>Axis</c> Y/X, <c>OrdinateCode</c>) -> generated <c>mAxisOrdinate</c>:
/// <c>OrdinateCode</c> (trimmed, <see cref="Normalization.OrdinateCode"/>) on the axis of the same
/// <c>AxisOrientation</c> of that table, the same index as the equality checker;</item>
/// <item>the generated ordinate must carry a <c>MET</c> categorisation whose member code is the
/// layout metric code; otherwise the unit is NOT MATCHED.</item>
/// </list>
/// The generated hierarchy of a matched unit, in this order: <c>mOrdinateMetricRestriction</c> of
/// the ordinate, then <c>mMetric.ReferencedHierarchyID</c> of the metric, then none (a MET on a
/// key/open axis, for example: not a violation, listed in the census).
/// </summary>
public static class PlaneCMetricHierarchyChecker
{
    public const string RestrictionTable = "mOrdinateMetricRestriction";

    public const string SourceOrdinate = "ordinate";
    public const string SourceMetric = "metric";

    /// <summary><see cref="FrameworkCode"/> only drives the release pairing; it is not part of the
    /// business key (the release label and the table code are).</summary>
    public sealed record Unit(
        string FrameworkCode, string ReleaseLabel, string TableCode, string Axis, string OrdinateCode, string MetricCode,
        string LayoutHierarchyDomainCode, string LayoutHierarchyCode)
    {
        /// <summary>The business key of the unit: <c>{TableCode}\t{Release}|{Axis}|{OrdinateCode}|{MetricCode}</c>,
        /// the shape of a <c>hierarchy</c> row of the plane C divergences TSV.</summary>
        public string BusinessKey => $"{TableCode}\t{ReleaseLabel}|{Axis}|{OrdinateCode}|{MetricCode}";

        public string LayoutHierarchy => $"{LayoutHierarchyDomainCode}:{LayoutHierarchyCode}";
    }

    /// <param name="Source"><see cref="SourceOrdinate"/> or <see cref="SourceMetric"/>.</param>
    public sealed record Violation(Unit Unit, string GeneratedHierarchy, string Source);

    public sealed record Census(
        long UnitsTotal,
        long Compared,
        IReadOnlyList<Unit> MatchedWithoutHierarchy,
        long NotMatched,
        IReadOnlyDictionary<string, long> NotMatchedByReason,
        IReadOnlyList<string> NotMatchedSamples,
        long SheetLevelNotExamined,
        long SheetsMatchedByPointReleaseTolerance);

    /// <summary><see cref="SkipReason"/> is set when the check cannot run at all on these inputs
    /// (older layouts repository, older output); the other members are then empty.</summary>
    public sealed record Result(string? SkipReason, IReadOnlyList<Violation> Violations, Census Census);

    private sealed record GeneratedOrdinate(string MetricCode, (string Domain, string Code)? RestrictionHierarchy, (string Domain, string Code)? MetricHierarchy);

    public static Result Check(SqliteConnection generated, SqliteConnection layouts)
    {
        var empty = new Census(0, 0, [], 0, new Dictionary<string, long>(), [], 0, 0);

        if (!HasColumn(layouts, "LayoutValue", "HierarchyCode") || !HasColumn(layouts, "LayoutValue", "HierarchyDomainCode"))
        {
            return new Result(
                "the layouts repository was built before LayoutValue stored the metric bracket " +
                "(columns HierarchyDomainCode/HierarchyCode absent): re-extract the layouts to evaluate it", [], empty);
        }

        if (!TableExists(generated, RestrictionTable))
        {
            return new Result(
                $"the generated output has no {RestrictionTable} table (output produced before the per-ordinate " +
                "metric restriction existed): nothing to compare the layout metric brackets with", [], empty);
        }

        var (units, sheetLevel) = LoadUnits(layouts);

        var releaseKeysByTableId = PlaneCReleaseMatcher.LoadReleaseKeysByTableId(generated);
        var (ordinateIndex, tableIdsByTableCode) = LoadOrdinateIndex(generated);
        var metricByOrdinate = LoadMetricOrdinates(generated);

        long compared = 0;
        long notMatched = 0;
        var withoutHierarchy = new List<Unit>();
        var violations = new List<Violation>();
        var notMatchedByReason = new Dictionary<string, long>(StringComparer.Ordinal);
        var notMatchedSamples = new List<string>();
        var toleratedSheets = new HashSet<(string Framework, string Release, string TableCode)>();

        foreach (var unit in units)
        {
            var (reason, ordinates, usedPointRelease) = Resolve(
                unit, releaseKeysByTableId, tableIdsByTableCode, ordinateIndex, metricByOrdinate);
            if (reason is not null)
            {
                notMatched++;
                notMatchedByReason[reason] = notMatchedByReason.GetValueOrDefault(reason) + 1;
                if (notMatchedSamples.Count < CheckResult.MaxSamples)
                {
                    notMatchedSamples.Add($"{unit.BusinessKey} [{reason}]");
                }

                continue;
            }

            if (usedPointRelease)
            {
                toleratedSheets.Add((unit.FrameworkCode, unit.ReleaseLabel, unit.TableCode));
            }

            // Hierarchy of every matched ordinate that HAS one: ordinate restriction first, the
            // metric's own hierarchy second.
            var generatedHierarchies = new List<(string Domain, string Code, string Source)>();
            foreach (var o in ordinates)
            {
                if (o.RestrictionHierarchy is { } r)
                {
                    generatedHierarchies.Add((r.Domain, r.Code, SourceOrdinate));
                }
                else if (o.MetricHierarchy is { } m)
                {
                    generatedHierarchies.Add((m.Domain, m.Code, SourceMetric));
                }
            }

            if (generatedHierarchies.Count == 0)
            {
                withoutHierarchy.Add(unit);
                continue;
            }

            compared++;
            var layoutDomain = PlaneCSignature.StripEbaPrefix(unit.LayoutHierarchyDomainCode);
            var differing = generatedHierarchies.FindIndex(g =>
                !string.Equals(g.Code, unit.LayoutHierarchyCode, StringComparison.Ordinal)
                || !string.Equals(PlaneCSignature.StripEbaPrefix(g.Domain), layoutDomain, StringComparison.Ordinal));
            if (differing >= 0)
            {
                var g = generatedHierarchies[differing];
                violations.Add(new Violation(unit, $"{g.Domain}:{g.Code}", g.Source));
            }
        }

        var census = new Census(
            units.Count, compared, withoutHierarchy, notMatched, notMatchedByReason, notMatchedSamples, sheetLevel,
            toleratedSheets.Count);
        return new Result(null, violations, census);
    }

    /// <summary>The matched generated ordinates of a unit, or the reason it does not match.</summary>
    private static (string? Reason, List<GeneratedOrdinate> Ordinates, bool UsedPointRelease) Resolve(
        Unit unit,
        Dictionary<long, HashSet<(string Framework, string Release)>> releaseKeysByTableId,
        Dictionary<string, HashSet<long>> tableIdsByTableCode,
        Dictionary<(long TableId, string Orientation, string Code), HashSet<long>> ordinateIndex,
        Dictionary<long, GeneratedOrdinate> metricByOrdinate)
    {
        if (!tableIdsByTableCode.TryGetValue(unit.TableCode, out var tableIds))
        {
            return ("table absent from the output", [], false);
        }

        // Same precedence as plane C everywhere: exact pairings win over the REL.n -> REL tolerance.
        var selection = PlaneCReleaseMatcher.SelectTableIds(tableIds, releaseKeysByTableId, unit.FrameworkCode, unit.ReleaseLabel);
        if (selection.Used.Count == 0)
        {
            return ("different release", [], false);
        }

        var ordinateIds = new HashSet<long>();
        foreach (var tableId in selection.Used)
        {
            if (ordinateIndex.TryGetValue((tableId, unit.Axis, Normalization.OrdinateCode(unit.OrdinateCode)), out var ids))
            {
                ordinateIds.UnionWith(ids);
            }
        }

        if (ordinateIds.Count == 0)
        {
            return ("ordinate not found on the axis", [], false);
        }

        var metricCode = PlaneCSignature.StripEbaPrefix(unit.MetricCode);
        var matched = new List<GeneratedOrdinate>();
        foreach (var id in ordinateIds.Order())
        {
            if (metricByOrdinate.TryGetValue(id, out var o)
                && string.Equals(PlaneCSignature.StripEbaPrefix(o.MetricCode), metricCode, StringComparison.Ordinal))
            {
                matched.Add(o);
            }
        }

        return matched.Count == 0
            ? ("ordinate without that MET categorisation", [], false)
            : (null, matched, selection.Match == PlaneCReleaseMatch.PointRelease);
    }

    /// <summary>The distinct layout units and the number of sheet-level metric values (no ordinate)
    /// that are not examined.</summary>
    private static (List<Unit> Units, long SheetLevel) LoadUnits(SqliteConnection layouts)
    {
        long sheetLevel = 0;
        var seen = new HashSet<Unit>();
        var units = new List<Unit>();

        using var cmd = layouts.CreateCommand();
        cmd.CommandText =
            """
            SELECT f."FrameworkCode", f."ReleaseLabel", s."TableCode", o."Axis", o."OrdinateCode",
                   v."MemberCode", v."HierarchyDomainCode", v."HierarchyCode", v."OrdinateId"
            FROM "LayoutValue" v
            JOIN "LayoutSheet" s ON s."SheetId" = v."SheetId"
            JOIN "LayoutFile" f ON f."FileId" = s."FileId"
            LEFT JOIN "LayoutOrdinate" o ON o."OrdinateId" = v."OrdinateId"
            WHERE v."DomainCode" = 'MET' AND v."HierarchyCode" IS NOT NULL AND v."MemberCode" IS NOT NULL
            ORDER BY s."TableCode", f."ReleaseLabel", o."Axis", o."OrdinateCode", v."MemberCode"
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (reader.IsDBNull(8) || reader.IsDBNull(3) || reader.IsDBNull(4))
            {
                sheetLevel++;
                continue;
            }

            var unit = new Unit(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                Normalization.OrdinateCode(reader.GetString(4)), reader.GetString(5),
                reader.IsDBNull(6) ? string.Empty : reader.GetString(6), reader.GetString(7));

            // The same unit can appear on several sheets (Z suffixes of one table): once.
            if (seen.Add(unit))
            {
                units.Add(unit);
            }
        }

        return (units, sheetLevel);
    }

    /// <summary>(TableID, Orientation, trimmed OrdinateCode) -> OrdinateID(s) and TableCode -> TableIDs;
    /// the same construction as <see cref="PlaneCEqualityChecker"/> (without the open axis sentinel,
    /// which names no metric).</summary>
    private static (Dictionary<(long TableId, string Orientation, string Code), HashSet<long>> Index, Dictionary<string, HashSet<long>> TableIds)
        LoadOrdinateIndex(SqliteConnection generated)
    {
        var index = new Dictionary<(long TableId, string Orientation, string Code), HashSet<long>>();
        var tableIdsByTableCode = new Dictionary<string, HashSet<long>>(StringComparer.Ordinal);

        using var cmd = generated.CreateCommand();
        cmd.CommandText =
            """
            SELECT t."TableID", t."TableCode", a."AxisOrientation", ao."OrdinateCode", ao."OrdinateID"
            FROM "mAxisOrdinate" ao
            JOIN "mAxis" a ON a."AxisID" = ao."AxisID"
            JOIN "mTableAxis" ta ON ta."AxisID" = a."AxisID"
            JOIN "mTable" t ON t."TableID" = ta."TableID"
            WHERE t."TableCode" IS NOT NULL AND a."AxisOrientation" IS NOT NULL
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var tableId = reader.GetInt64(0);
            var tableCode = LayoutTableCodeNormalizer.Canonicalize(reader.GetString(1));
            var key = (tableId, reader.GetString(2), Normalization.OrdinateCode(reader.IsDBNull(3) ? string.Empty : reader.GetString(3)));
            if (!index.TryGetValue(key, out var set))
            {
                set = [];
                index[key] = set;
            }

            set.Add(reader.GetInt64(4));

            if (!tableIdsByTableCode.TryGetValue(tableCode, out var tableIds))
            {
                tableIds = [];
                tableIdsByTableCode[tableCode] = tableIds;
            }

            tableIds.Add(tableId);
        }

        return (index, tableIdsByTableCode);
    }

    /// <summary>The ordinates carrying a MET categorisation: metric member code, and the hierarchy
    /// from the per-ordinate restriction and from the metric itself (either may be absent).</summary>
    private static Dictionary<long, GeneratedOrdinate> LoadMetricOrdinates(SqliteConnection generated)
    {
        var result = new Dictionary<long, GeneratedOrdinate>();

        using var cmd = generated.CreateCommand();
        cmd.CommandText =
            """
            SELECT oc."OrdinateID", mm."MemberCode", rd."DomainCode", rh."HierarchyCode", md."DomainCode", mh."HierarchyCode"
            FROM "mOrdinateCategorisation" oc
            JOIN "mDimension" dim ON dim."DimensionID" = oc."DimensionID" AND dim."DimensionXBRLCode" = 'MET'
            JOIN "mMember" mm ON mm."MemberID" = oc."MemberID"
            LEFT JOIN "mOrdinateMetricRestriction" r ON r."OrdinateID" = oc."OrdinateID"
            LEFT JOIN "mHierarchy" rh ON rh."HierarchyID" = r."HierarchyID"
            LEFT JOIN "mDomain" rd ON rd."DomainID" = rh."DomainID"
            LEFT JOIN "mMetric" me ON me."CorrespondingMemberID" = mm."MemberID"
            LEFT JOIN "mHierarchy" mh ON mh."HierarchyID" = me."ReferencedHierarchyID"
            LEFT JOIN "mDomain" md ON md."DomainID" = mh."DomainID"
            WHERE mm."MemberCode" IS NOT NULL
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            (string, string)? restriction = reader.IsDBNull(3) ? null : (reader.IsDBNull(2) ? string.Empty : reader.GetString(2), reader.GetString(3));
            (string, string)? metric = reader.IsDBNull(5) ? null : (reader.IsDBNull(4) ? string.Empty : reader.GetString(4), reader.GetString(5));
            result[reader.GetInt64(0)] = new GeneratedOrdinate(reader.GetString(1), restriction, metric);
        }

        return result;
    }

    private static bool TableExists(SqliteConnection connection, string table)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """SELECT COUNT(1) FROM "sqlite_master" WHERE "type" = 'table' AND "name" = $name""";
        cmd.Parameters.AddWithValue("$name", table);
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    private static bool HasColumn(SqliteConnection connection, string table, string column)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""PRAGMA table_info("{table}")""";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
