using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Core.Validation.PlaneC;

/// <summary>
/// The table-to-release pairing of plane C, SHARED between <see cref="PlaneCComparer"/> and
/// <see cref="PlaneCEqualityChecker"/>, so the two checks cannot diverge on the same criterion (two
/// copies of the same criterion are sooner or later two criteria). The scope guard of
/// <c>C-EQU-01</c> relies on it: a layouts repository of release 4.2 used to compare 19 <c>==</c>
/// rules against DPM 1.0 tables believing they were the same publication.
///
/// The OWN release of a table is derived from <c>mTaxonomyTable -> mTaxonomy.TaxonomyCode ->
/// mReportingFramework.FrameworkCode</c> (<c>TaxonomyCode</c> = <c>"{framework in lower case}
/// {release}"</c> by construction in DPM 2.0, see <c>Dpm20TaxonomyDeriver</c>), never from data
/// derived from the Access. A <c>TaxonomyCode</c> that does not follow that shape (DPM 1.0: literal
/// from the Access, without the separating space) yields no release key: the table is left
/// WITHOUT a key and no layout sheet can pair with it. That is the correct answer, not a failure
/// to compensate with a list pinned in the code.
/// </summary>
public enum PlaneCReleaseMatch
{
    None,
    Exact,
    PointRelease,
}

public static class PlaneCReleaseMatcher
{
    /// <summary>
    /// <c>TableID</c> -&gt; set of <c>(FrameworkCode in upper case, release)</c> it belongs to,
    /// measured against <c>mTaxonomyTable</c>/<c>mTaxonomy</c>/<c>mReportingFramework</c> of the
    /// <paramref name="generated"/> output.
    /// </summary>
    public static Dictionary<long, HashSet<(string Framework, string Release)>> LoadReleaseKeysByTableId(SqliteConnection generated)
    {
        var releaseKeysByTableId = new Dictionary<long, HashSet<(string Framework, string Release)>>();
        using var cmd = generated.CreateCommand();
        cmd.CommandText =
            """
            SELECT tt."TableID", tax."TaxonomyCode", fw."FrameworkCode"
            FROM "mTaxonomyTable" tt
            JOIN "mTaxonomy" tax ON tax."TaxonomyID" = tt."TaxonomyID"
            JOIN "mReportingFramework" fw ON fw."FrameworkID" = tax."FrameworkID"
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var tableId = reader.GetInt64(0);
            var taxonomyCode = reader.IsDBNull(1) ? null : reader.GetString(1);
            var frameworkCode = reader.IsDBNull(2) ? null : reader.GetString(2);
            if (taxonomyCode is null || frameworkCode is null)
            {
                continue;
            }

            var spaceIndex = taxonomyCode.IndexOf(' ');
            if (spaceIndex < 0)
            {
                // It does not follow "{framework} {release}" (e.g. DPM 1.0, literal TaxonomyCode
                // from the Access): there is no release to extract, this table cannot pair with
                // any LayoutSheet. Defensive, not a failure.
                continue;
            }

            var release = taxonomyCode[(spaceIndex + 1)..];
            if (!releaseKeysByTableId.TryGetValue(tableId, out var set))
            {
                set = [];
                releaseKeysByTableId[tableId] = set;
            }

            set.Add((frameworkCode.ToUpperInvariant(), release));
        }

        return releaseKeysByTableId;
    }

    /// <summary>
    /// <c>TableCode</c> -&gt; union of the release keys of ALL the <c>TableID</c> values that share
    /// that code (as already done in <see cref="PlaneCComparer"/>: a code lives in more than one
    /// taxonomy, and the layout publishes a single sheet per code, not one per <c>TableID</c>).
    /// </summary>
    public static Dictionary<string, HashSet<(string Framework, string Release)>> LoadReleaseKeysByTableCode(SqliteConnection generated)
    {
        var tableCodeByTableId = new Dictionary<long, string>();
        using (var cmd = generated.CreateCommand())
        {
            cmd.CommandText = """SELECT "TableID", "TableCode" FROM "mTable" WHERE "TableCode" IS NOT NULL""";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                tableCodeByTableId[reader.GetInt64(0)] = reader.GetString(1);
            }
        }

        var releaseKeysByTableId = LoadReleaseKeysByTableId(generated);

        var result = new Dictionary<string, HashSet<(string Framework, string Release)>>(StringComparer.Ordinal);
        foreach (var (tableId, tableCode) in tableCodeByTableId)
        {
            if (!releaseKeysByTableId.TryGetValue(tableId, out var set))
            {
                continue;
            }

            if (!result.TryGetValue(tableCode, out var union))
            {
                union = [];
                result[tableCode] = union;
            }

            union.UnionWith(set);
        }

        return result;
    }

    /// <summary>
    /// A layout release <c>REL.n</c> (a hotfix/point release of <c>REL</c>) is the table's own
    /// release when the table has release <c>REL</c>: the hotfix mechanism leaves a clean trace
    /// (e.g. <c>FINREP9DP</c> in 4.2.1) and a 4.2 hotfix IS the arbiter of 4.2. General rule
    /// <c>REL.n -> REL</c>, not a special case of one framework; it is kept distinct from exact
    /// equality so that the report can NAME the sheets it was applied to (never silent).
    /// </summary>
    public static PlaneCReleaseMatch ClassifyRelease(
        HashSet<(string Framework, string Release)> releaseKeys, string frameworkCode, string layoutRelease)
    {
        var framework = frameworkCode.ToUpperInvariant();
        var sawPointRelease = false;
        foreach (var (fw, rel) in releaseKeys)
        {
            if (fw != framework)
            {
                continue;
            }

            if (string.Equals(rel, layoutRelease, StringComparison.Ordinal))
            {
                return PlaneCReleaseMatch.Exact;
            }

            if (layoutRelease.StartsWith(rel + ".", StringComparison.Ordinal))
            {
                sawPointRelease = true;
            }
        }

        return sawPointRelease ? PlaneCReleaseMatch.PointRelease : PlaneCReleaseMatch.None;
    }
}
