using System.Text.RegularExpressions;
using EbaDpm.Converter.Core.Layouts;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Core.Validation.PlaneC;

/// <summary>
/// The equality rules (<c>==</c>) found in the cell comments of the Annotated Table Layouts: a
/// positive control of the CONVERSION that does not go through any third-party database, does not
/// compare counts and CROSSES THE BORDER OF A TABLE. It verifies the INTERNAL CONSISTENCY of the
/// signature among the cells that a rule names, never that the signature is the correct one (two
/// cells with the SAME wrong signature pass); it complements the rest of plane C, it does not
/// replace it.
///
/// It deliberately does NOT use <c>mTableCell.BusinessCode</c> to resolve a term: its order is a
/// convention of ours that is not derived, and these terms already come labelled by axis
/// (<c>r</c>/<c>c</c>/<c>s</c>). They are resolved axis by axis against
/// <c>mAxis</c>/<c>mAxisOrdinate</c>/<c>mCellPosition</c>, independently of that convention.
///
/// <c>9999</c> is the OPEN AXIS SENTINEL: it is not a real <c>OrdinateCode</c> (our open axes mint
/// their own codes, 0010, 0020...) but "the SINGLE ordinate of the OPEN axis of that orientation in
/// that table". It is resolved by overlaying, for every (TableCode, Orientation) with some
/// <c>IsOpenAxis=1</c> axis, a synthetic entry under the code <c>"9999"</c> holding the set of
/// <c>OrdinateID</c> values of those open axes. Without this rule the checkable count drops from
/// 4,849 to 1,416 over the same 5,120 rules (positive control of the rule itself).
///
/// <c>s0000</c> (or the absence of the <c>s</c> term): NO Z-axis filter; the third term is not
/// required to identify a concrete sheet. If the table has a REAL Z axis with more than one
/// ordinate, the absence of a filter leaves more than one candidate cell and the rule falls into
/// "ambiguous cell". That is CORRECT behavior, not a special case: it emerges from not adding the
/// restriction.
///
/// A <c>TableCode</c> shared by several <c>TableID</c> values (reuse across taxonomies) DOES need
/// per-<c>TableID</c> treatment: blindly joining by <c>TableCode</c> put the <c>TableID</c> values of
/// 4.0, 4.2 and 4.3 into the same index in a cumulative cut, and the later intersection found more
/// than one candidate cell ("ambiguous cell": 5,056 of 5,120 rules against 4.3). The ordinate index
/// is built by <c>(TableID, Orientation, OrdinateCode)</c>, plus a map
/// <c>TableCode -> {TableID}</c>; when a term is resolved, that set of <c>TableID</c> values is
/// filtered FIRST by the release of the rule (<see cref="PlaneCReleaseMatcher"/>, the same criterion
/// that already decides "different release") and only then are the <c>OrdinateID</c> values of the
/// surviving <c>TableID</c> values joined. Normally there is a single one, and if there are more
/// (two <c>TableID</c> values of the same code in the SAME release), the later intersection with
/// <c>mCellPosition</c> isolates them as before. <see cref="PlaneCComparer"/> is not affected: its
/// unit of comparison is the table code by measured decision, and joining is fine there; it is
/// this equality checker, which needs EXACTLY one cell, that joining broke.
///
/// The <c>==</c> in the layout comment asserts identity of the VARIABLE, not of the whole
/// signature: the comment itself, below the <c>==</c>, writes <c>VariableID</c>/<c>VariableVID</c>,
/// and the Access confirms that one <c>VariableVID</c> can live in cells whose ONLY disagreement is
/// the restriction bracket of the open axis (<c>K_26.00.a</c> vs <c>K_29.00</c>, restricted by
/// <c>qAE2</c>/<c>qAE1</c> respectively: genuinely DIFFERENT subcategories, 20 members against 18).
/// That bracket belongs to the TABLE, not to the Variable, so it is RELAXED before comparing: every
/// <c>DIM(*[whatever])</c> term becomes <c>DIM(*)</c>. Only that bracket, nothing else in the
/// signature is touched (the <c>eba_...:</c> prefix is never stripped here, on purpose: this check
/// compares the literal <c>DatapointSignature</c> of <c>mTableCell</c>, not the normalized form used
/// in the rest of plane C). How many rules NEEDED the relaxation to come out equal is counted and
/// named, so that the relaxation is visible rather than hiding anything.
///
/// Each TERM is resolved only against a table whose release pairs with that of the sheet the rule
/// came from (<c>LayoutEqualityRule.SheetId -> LayoutSheet -> LayoutFile.FrameworkCode</c>/
/// <c>ReleaseLabel</c>), with the SAME mechanism used by <see cref="PlaneCComparer"/>
/// (<see cref="PlaneCReleaseMatcher"/>, including the <c>REL.n -> REL</c> tolerance). This is the
/// scope guard of <c>C-EQU-01</c>: without it, a 4.2 layouts repository compared DPM 1.0 tables
/// believing they were the same publication (Examined=162, Failed=19). A term whose table does not
/// pair on release is NOT CHECKABLE with the reason <c>"different release"</c>: it neither passes
/// nor fails, and if NO term of ANY rule turns out checkable, <c>C-EQU-01</c> comes out
/// <c>Skipped</c> through the existing guard (<c>RulesCheckable == 0</c>), never <c>pass</c>.
/// Against DPM 1.0 (whose <c>TaxonomyCode</c> did not follow "{framework} {release}") this gives 0
/// checkable, the correct answer. Against DPM 2.0 4.3 with the 4.2 repository it gives the same 0
/// checkable, now through <c>"different release"</c>.
/// </summary>
public static class PlaneCEqualityChecker
{
    // "(*[841])", "(*[eba_hry:BT3])"... -> "(*)": only the restriction bracket of the open axis
    // - the rest of the term (dimension, eba_ prefix, closed member) is not touched.
    private static readonly Regex OpenAxisBracketPattern = new(@"\(\*\[[^\]]*\]\)", RegexOptions.Compiled);

    /// <summary>Relaxes the open axis restriction bracket of a literal <c>DatapointSignature</c>;
    /// the only change <c>C-EQU-01</c> applies before comparing Variable identity.</summary>
    private static string RelaxOpenAxisBracket(string signature) => OpenAxisBracketPattern.Replace(signature, "(*)");

    public sealed record EqualityRuleTermData(string Term, string TableCode, string RowCode, string ColumnCode, string? ZCode);

    /// <summary>A resolved rule that VIOLATES the invariant: its cells do not carry the same
    /// <c>DatapointSignature</c> (not even after relaxing the open axis bracket).
    /// <see cref="BusinessKey"/> is the business key BOUND to the TEXT of the terms (not to the
    /// <c>RuleId</c>, an artifact of extraction order).</summary>
    public sealed record EqualityRuleViolation(long RuleId, IReadOnlyList<(string Term, string Signature)> Resolved)
    {
        public string BusinessKey => string.Join("==", Resolved.Select(r => "{" + r.Term + "}"));
    }

    /// <summary>The examined units ALWAYS come first, with each exclusion reason named; never a
    /// hollow <c>0</c> indistinguishable from "not evaluated".</summary>
    public sealed record EqualityCoverage(
        long RulesTotal,
        long RulesCheckable,
        long RulesIdentical,
        long RulesDivergent,
        IReadOnlyDictionary<string, long> NonCheckableByReason,
        // Of RulesIdentical, how many came out equal only after relaxing the open axis bracket
        // (they diverged without relaxing): named, never silent.
        long RulesRelaxedByOpenAxisBracket,
        // The release tolerance REL.n -> REL applies here exactly as in PlaneCComparer, and is
        // named the same way: how many rule source sheets paired ONLY through this tolerance
        // (never silent).
        long SheetsMatchedByPointReleaseTolerance,
        IReadOnlyList<string> PointReleaseToleranceSamples);

    public sealed record EqualityCheckResult(IReadOnlyList<EqualityRuleViolation> Violations, EqualityCoverage Coverage);

    public static EqualityCheckResult Check(SqliteConnection generated, SqliteConnection layouts)
    {
        var (rulesByRuleId, sheetIdByRuleId) = LoadRules(layouts);

        // The SOURCE sheet of each rule (provenance, LayoutEqualityRule.SheetId, a single one per
        // rule) -> (FrameworkCode, ReleaseLabel) of its LayoutFile. It is the release that governs
        // ALL the terms of that rule.
        var ruleReleaseBySheetId = new Dictionary<long, (string FrameworkCode, string ReleaseLabel)>();
        using (var cmd = layouts.CreateCommand())
        {
            cmd.CommandText =
                """
                SELECT s."SheetId", f."FrameworkCode", f."ReleaseLabel"
                FROM "LayoutSheet" s
                JOIN "LayoutFile" f ON f."FileId" = s."FileId"
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                ruleReleaseBySheetId[reader.GetInt64(0)] = (reader.GetString(1), reader.GetString(2));
            }
        }

        // The OWN release of each table of THE OUTPUT, by TableID (same mechanism as
        // PlaneCComparer, shared in PlaneCReleaseMatcher). By TableID, not by TableCode: the
        // release filter must be able to tell, within one code, which concrete TableID pairs with
        // the sheet of the rule.
        var releaseKeysByTableId = PlaneCReleaseMatcher.LoadReleaseKeysByTableId(generated);

        // (TableID, Orientation Y/X/Z, trimmed OrdinateCode) -> OrdinateID(s), and the map
        // TableCode -> {TableID} so that, when resolving a term, that set can be filtered by
        // release BEFORE joining OrdinateIDs (joining by TableCode without that filter is exactly
        // the accident that left "ambiguous cell" in a cumulative cut).
        var ordinateIndex = new Dictionary<(long TableId, string Orientation, string Code), HashSet<long>>();
        var tableIdsByTableCode = new Dictionary<string, HashSet<long>>(StringComparer.Ordinal);
        using (var cmd = generated.CreateCommand())
        {
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
                // Symmetric with LayoutSheetNameParser/LayoutEqualityRuleParser: the TableCode of
                // the rule term (term.TableCode, below) already arrives canonical from the layouts
                // repository; this side has to go through the SAME function.
                var tableCode = LayoutTableCodeNormalizer.Canonicalize(reader.GetString(1));
                var orientation = reader.GetString(2);
                var code = Normalization.OrdinateCode(reader.IsDBNull(3) ? string.Empty : reader.GetString(3));
                var ordinateId = reader.GetInt64(4);

                var key = (tableId, orientation, code);
                if (!ordinateIndex.TryGetValue(key, out var set))
                {
                    set = [];
                    ordinateIndex[key] = set;
                }

                set.Add(ordinateId);

                if (!tableIdsByTableCode.TryGetValue(tableCode, out var tableIds))
                {
                    tableIds = [];
                    tableIdsByTableCode[tableCode] = tableIds;
                }

                tableIds.Add(tableId);
            }
        }

        var tableCodesInIndex = new HashSet<string>(tableIdsByTableCode.Keys, StringComparer.Ordinal);

        // The "9999" SENTINEL: for every (TableID, Orientation) with some IsOpenAxis=1 axis, the
        // set of OrdinateIDs of THAT open axis, under the synthetic code "9999". It overwrites any
        // real "9999" code that might exist (not measured, and if it existed it would be the SAME
        // idea: "the ordinate that covers the whole grid of that orientation"). By TableID, like
        // the rest of the index.
        using (var cmd = generated.CreateCommand())
        {
            cmd.CommandText =
                """
                SELECT t."TableID", t."TableCode", a."AxisOrientation", ao."OrdinateID"
                FROM "mAxisOrdinate" ao
                JOIN "mAxis" a ON a."AxisID" = ao."AxisID"
                JOIN "mTableAxis" ta ON ta."AxisID" = a."AxisID"
                JOIN "mTable" t ON t."TableID" = ta."TableID"
                WHERE a."IsOpenAxis" = 1 AND t."TableCode" IS NOT NULL AND a."AxisOrientation" IS NOT NULL
                """;
            using var reader = cmd.ExecuteReader();
            var openSets = new Dictionary<(long TableId, string Orientation), HashSet<long>>();
            var openTableCodeByTableId = new Dictionary<long, string>();
            while (reader.Read())
            {
                var tableId = reader.GetInt64(0);
                var tableCode = LayoutTableCodeNormalizer.Canonicalize(reader.GetString(1)); // symmetric with the layout side
                var key = (tableId, reader.GetString(2));
                if (!openSets.TryGetValue(key, out var set))
                {
                    set = [];
                    openSets[key] = set;
                }

                set.Add(reader.GetInt64(3));
                openTableCodeByTableId[tableId] = tableCode;
            }

            foreach (var ((tableId, orientation), set) in openSets)
            {
                ordinateIndex[(tableId, orientation, "9999")] = set;

                var tableCode = openTableCodeByTableId[tableId];
                tableCodesInIndex.Add(tableCode);
                if (!tableIdsByTableCode.TryGetValue(tableCode, out var tableIds))
                {
                    tableIds = [];
                    tableIdsByTableCode[tableCode] = tableIds;
                }

                tableIds.Add(tableId);
            }
        }

        var cellsByOrdinate = new Dictionary<long, HashSet<long>>();
        using (var cmd = generated.CreateCommand())
        {
            cmd.CommandText = """SELECT "OrdinateID", "CellID" FROM "mCellPosition" """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var ordinateId = reader.GetInt64(0);
                var cellId = reader.GetInt64(1);
                if (!cellsByOrdinate.TryGetValue(ordinateId, out var set))
                {
                    set = [];
                    cellsByOrdinate[ordinateId] = set;
                }

                set.Add(cellId);
            }
        }

        var cellInfo = new Dictionary<long, (bool IsShaded, string? Signature)>();
        using (var cmd = generated.CreateCommand())
        {
            cmd.CommandText = """SELECT "CellID", "IsShaded", "DatapointSignature" FROM "mTableCell" """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var cellId = reader.GetInt64(0);
                var isShaded = !reader.IsDBNull(1) && reader.GetInt64(1) != 0;
                var signature = reader.IsDBNull(2) ? null : reader.GetString(2);
                cellInfo[cellId] = (isShaded, signature);
            }
        }

        long checkable = 0, identical = 0, divergent = 0, relaxedByOpenAxisBracket = 0;
        var nonCheckableByReason = new Dictionary<string, long>(StringComparer.Ordinal);
        var violations = new List<EqualityRuleViolation>();

        var toleratedRuleSheetIds = new HashSet<long>();
        var toleratedSamples = new List<string>();

        foreach (var (ruleId, terms) in rulesByRuleId)
        {
            // The release that governs this rule is that of ITS source sheet; if the sheet does
            // not resolve (it should not happen, LayoutEqualityRule.SheetId references LayoutSheet
            // by FK), defensively no term of this rule is checkable.
            var ruleSheetId = sheetIdByRuleId[ruleId];
            if (!ruleReleaseBySheetId.TryGetValue(ruleSheetId, out var ruleRelease))
            {
                nonCheckableByReason["rule release unresolved"] =
                    nonCheckableByReason.GetValueOrDefault("rule release unresolved") + 1;
                continue;
            }

            var (allOk, bucket, resolved, usedPointRelease) = EvaluateRule(
                terms, ruleRelease.FrameworkCode, ruleRelease.ReleaseLabel, releaseKeysByTableId,
                tableCodesInIndex, tableIdsByTableCode, ordinateIndex, cellsByOrdinate, cellInfo);
            if (!allOk)
            {
                nonCheckableByReason[bucket] = nonCheckableByReason.GetValueOrDefault(bucket) + 1;
                continue;
            }

            checkable++;

            if (usedPointRelease && toleratedRuleSheetIds.Add(ruleSheetId))
            {
                if (toleratedSamples.Count < 50)
                {
                    toleratedSamples.Add(
                        $"rule {ruleId} (sheet release='{ruleRelease.ReleaseLabel}', framework='{ruleRelease.FrameworkCode}')");
                }
            }

            // The identity asserted by "==" is of the VARIABLE, not of the whole signature: it is
            // compared after relaxing the restriction bracket of the open axis (a property of the
            // table, not of the Variable). It is ALSO measured without relaxing, only to be able to
            // name how many rules needed the relaxation, never to decide the result without it.
            var rawSignatures = resolved.Select(r => r.Signature).ToList();
            var rawDistinctCount = rawSignatures.Distinct(StringComparer.Ordinal).Count();
            var relaxedDistinctCount = rawSignatures.Select(RelaxOpenAxisBracket).Distinct(StringComparer.Ordinal).Count();

            if (relaxedDistinctCount == 1)
            {
                identical++;
                if (rawDistinctCount > 1)
                {
                    relaxedByOpenAxisBracket++;
                }
            }
            else
            {
                divergent++;
                violations.Add(new EqualityRuleViolation(ruleId, resolved));
            }
        }

        var coverage = new EqualityCoverage(
            rulesByRuleId.Count, checkable, identical, divergent, nonCheckableByReason, relaxedByOpenAxisBracket,
            toleratedRuleSheetIds.Count, toleratedSamples);
        return new EqualityCheckResult(violations, coverage);
    }

    /// <summary>
    /// Evaluates ONE rule term by term. A term fails, in this order of checking: table absent
    /// (the <c>TableCode</c> does not appear at all in the ordinate index) &gt; <b>different
    /// release</b> (NO <c>TableID</c> of that <c>TableCode</c> pairs with the release of the rule's
    /// source sheet, not even with the <c>REL.n -> REL</c> tolerance; this step comes right after
    /// "table absent" and before trying to resolve cells of a table that is not even from the
    /// publication of this rule) &gt; cell unresolved (some axis finds no ordinate with that code,
    /// already restricted to the <c>TableID</c> values that DO pair) &gt; ambiguous cell (more than
    /// one candidate cell) &gt; shaded or without signature. The reason for the RULE, if any term
    /// fails, is the alphabetically smallest among the DISTINCT reasons of its failed terms:
    /// deterministic, not "the first one found".
    /// </summary>
    private static (bool AllOk, string Bucket, List<(string Term, string Signature)> Resolved, bool UsedPointReleaseTolerance) EvaluateRule(
        IReadOnlyList<EqualityRuleTermData> terms,
        string ruleFrameworkCode,
        string ruleReleaseLabel,
        Dictionary<long, HashSet<(string Framework, string Release)>> releaseKeysByTableId,
        HashSet<string> tableCodesInIndex,
        Dictionary<string, HashSet<long>> tableIdsByTableCode,
        Dictionary<(long TableId, string Orientation, string Code), HashSet<long>> ordinateIndex,
        Dictionary<long, HashSet<long>> cellsByOrdinate,
        Dictionary<long, (bool IsShaded, string? Signature)> cellInfo)
    {
        var statuses = new List<string>(terms.Count);
        var resolved = new List<(string Term, string Signature)>(terms.Count);
        var usedPointReleaseTolerance = false;

        foreach (var term in terms)
        {
            if (!tableCodesInIndex.Contains(term.TableCode))
            {
                statuses.Add("table absent");
                continue;
            }

            // Of the TableIDs that share this TableCode, only those that pair release with the
            // rule's source sheet are candidates to resolve the cell: same criterion as
            // PlaneCComparer, via PlaneCReleaseMatcher (including the REL.n -> REL tolerance), but
            // applied PER TableID, not per aggregated TableCode.
            var matchingTableIds = new List<long>();
            foreach (var tableId in tableIdsByTableCode[term.TableCode])
            {
                var releaseKeys = releaseKeysByTableId.GetValueOrDefault(tableId, []);
                var releaseMatch = PlaneCReleaseMatcher.ClassifyRelease(releaseKeys, ruleFrameworkCode, ruleReleaseLabel);
                if (releaseMatch == PlaneCReleaseMatch.None)
                {
                    continue;
                }

                if (releaseMatch == PlaneCReleaseMatch.PointRelease)
                {
                    usedPointReleaseTolerance = true;
                }

                matchingTableIds.Add(tableId);
            }

            if (matchingTableIds.Count == 0)
            {
                statuses.Add("different release");
                continue;
            }

            var axisSteps = new List<(string Orientation, string Code)> { ("Y", term.RowCode), ("X", term.ColumnCode) };
            if (term.ZCode is not null && term.ZCode != "0000")
            {
                axisSteps.Add(("Z", term.ZCode));
            }

            HashSet<long>? candidate = null;
            var failed = false;
            foreach (var (orientation, code) in axisSteps)
            {
                var ordinateIds = new HashSet<long>();
                foreach (var tableId in matchingTableIds)
                {
                    if (ordinateIndex.TryGetValue((tableId, orientation, code), out var ids))
                    {
                        ordinateIds.UnionWith(ids);
                    }
                }

                if (ordinateIds.Count == 0)
                {
                    statuses.Add("cell unresolved");
                    failed = true;
                    break;
                }

                var cellsForStep = new HashSet<long>();
                foreach (var oid in ordinateIds)
                {
                    if (cellsByOrdinate.TryGetValue(oid, out var cells))
                    {
                        cellsForStep.UnionWith(cells);
                    }
                }

                candidate = candidate is null ? cellsForStep : Intersect(candidate, cellsForStep);
                if (candidate.Count == 0)
                {
                    statuses.Add("cell unresolved");
                    failed = true;
                    break;
                }
            }

            if (failed)
            {
                continue;
            }

            if (candidate!.Count > 1)
            {
                statuses.Add("ambiguous cell");
                continue;
            }

            var cellId = candidate.Single();
            if (!cellInfo.TryGetValue(cellId, out var info) || info.IsShaded || info.Signature is null)
            {
                statuses.Add("shaded or without signature");
                continue;
            }

            statuses.Add("ok");
            resolved.Add((term.Term, info.Signature));
        }

        var allOk = statuses.Count == terms.Count && statuses.All(s => s == "ok");
        if (allOk)
        {
            return (true, "all ok", resolved, usedPointReleaseTolerance);
        }

        var bucket = statuses.Where(s => s != "ok").Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).First();
        return (false, bucket, resolved, false);
    }

    private static HashSet<long> Intersect(HashSet<long> a, HashSet<long> b)
    {
        var result = new HashSet<long>();
        foreach (var x in a)
        {
            if (b.Contains(x))
            {
                result.Add(x);
            }
        }

        return result;
    }

    /// <summary>
    /// The equality rules and the SOURCE sheet of each rule (<c>LayoutEqualityRule.SheetId</c>, the
    /// same for all positions of a rule, see <c>LayoutExtractor</c>): the one that governs the
    /// release against which its terms are resolved.
    /// </summary>
    private static (Dictionary<long, List<EqualityRuleTermData>> Rules, Dictionary<long, long> SheetIdByRuleId) LoadRules(
        SqliteConnection layouts)
    {
        var result = new Dictionary<long, List<EqualityRuleTermData>>();
        var sheetIdByRuleId = new Dictionary<long, long>();

        // NEW table: "LayoutEqualityRule" does not exist in an older layouts repository;
        // defensive, not a failure: an old repository simply contributes no rules (0 total rules,
        // C-EQU-01/02 say so without crashing).
        using (var probe = layouts.CreateCommand())
        {
            probe.CommandText =
                """SELECT COUNT(*) FROM "sqlite_master" WHERE "type" = 'table' AND "name" = 'LayoutEqualityRule'""";
            var exists = Convert.ToInt64(probe.ExecuteScalar()) > 0;
            if (!exists)
            {
                return (result, sheetIdByRuleId);
            }
        }

        using var cmd = layouts.CreateCommand();
        cmd.CommandText =
            """
            SELECT "RuleId", "SheetId", "Term", "TableCode", "RowCode", "ColumnCode", "ZCode"
            FROM "LayoutEqualityRule"
            ORDER BY "RuleId", "Position"
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var ruleId = reader.GetInt64(0);
            var sheetId = reader.GetInt64(1);
            var term = reader.GetString(2);
            var tableCode = reader.GetString(3);
            var rowCode = reader.GetString(4);
            var columnCode = reader.GetString(5);
            var zCode = reader.IsDBNull(6) ? null : reader.GetString(6);

            if (!result.TryGetValue(ruleId, out var list))
            {
                list = [];
                result[ruleId] = list;
                sheetIdByRuleId[ruleId] = sheetId;
            }

            list.Add(new EqualityRuleTermData(term, tableCode, rowCode, columnCode, zCode));
        }

        return (result, sheetIdByRuleId);
    }
}
