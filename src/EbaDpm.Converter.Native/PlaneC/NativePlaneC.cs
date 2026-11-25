using System.Diagnostics;
using EbaDpm.Converter.Core.Validation;
using EbaDpm.Converter.Core.Validation.PlaneC;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Native.PlaneC;

/// <summary>Result of plane C on a native output: the check results and the known divergences
/// that were evaluated (applied, stale or not applicable), for the report.</summary>
public sealed record NativePlaneCResult(
    IReadOnlyList<CheckResult> Checks,
    IReadOnlyList<ExceptionInfo> Divergences);

/// <summary>
/// Plane C of the native DPM 2.0 output: the comparison against the EBA Annotated Table Layouts,
/// read through the native model (see docs/validation.md, "Native output").
///
/// The native file holds every release, so each layout sheet is compared at ITS release R: the
/// <c>Release</c> row whose <c>Code</c> equals the sheet's <c>ReleaseLabel</c> (exact match, no
/// point-release tolerance), and the table version whose <c>Code</c> is the table code and that is valid
/// at R. Sheets whose release is not in the file, or whose table has no unique table version at R, are
/// not compared and are counted, by reason, in <c>C-COB-01</c>. The census of known divergences of
/// release R applies to the sheets of release R; an entry that matches nothing in an evaluated release
/// is stale and fails.
/// </summary>
public static class NativePlaneC
{
    private const string CDps01Statement =
        "every datapoint signature of the layout (at its release) is CONTAINED in one of the native signatures of " +
        "the table version valid at that release, and no dimension it mentions carries another member; violations " +
        "NOT listed in the native plane C census of the release";

    private const string CEqu01Statement =
        "all cells named by an equality rule \"==\" in the layout cell comments carry the same native datapoint " +
        "signature at the release of the rule: INTERNAL consistency of the signature, not proof that it is correct";

    private const string CMhr01Statement =
        "for every metric (MET) value of the layouts with a hierarchy bracket \"[domain:hierarchy]\" on an ordinate, the " +
        "matching non-key header of the table version (same axis and ordinate code, same metric) has " +
        "HeaderVersion.SubCategoryVID -> SubCategory.Code equal to the hierarchy and Category.Code equal to the " +
        "domain; violations NOT listed in the native plane C census of the release";

    /// <summary>Runs every native plane C check. Both connections are read-only.</summary>
    public static NativePlaneCResult Run(SqliteConnection native, SqliteConnection layouts) =>
        Run(native, layouts, NativePlaneCCensuses.EmbeddedCensuses);

    /// <summary>As <see cref="Run(SqliteConnection, SqliteConnection)"/>, with the censuses given
    /// instead of the embedded ones.</summary>
    internal static NativePlaneCResult Run(
        SqliteConnection native, SqliteConnection layouts, IReadOnlyList<NativePlaneCCensus> censuses)
    {
        ArgumentNullException.ThrowIfNull(native);
        ArgumentNullException.ThrowIfNull(layouts);
        ArgumentNullException.ThrowIfNull(censuses);

        var sw = Stopwatch.StartNew();
        var layoutData = PlaneCLayoutReader.Read(layouts);
        var sheets = LoadSheets(layouts);

        var releases = NativeReleases.Load(native);
        var tableVersions = NativeTableVersions.Load(native);
        var itemCodes = NativeItemCodes.Load(native);

        // The releases of the layouts that the file has, and the table versions to load for them.
        var releaseIds = new Dictionary<string, long>(StringComparer.Ordinal);
        var knownReleases = sheets.Select(s => s.ReleaseLabel).Distinct(StringComparer.Ordinal)
            .Select(label => (Label: label, Found: releases.TryGetId(label, out var id), Id: id))
            .Where(r => r.Found);
        foreach (var (label, _, id) in knownReleases)
        {
            releaseIds[label] = id;
        }

        var tableVids = new HashSet<long>();
        foreach (var id in releaseIds.Values)
        {
            tableVids.UnionWith(tableVersions.UniqueValidAt(id));
        }

        var structure = NativeStructure.Load(native, tableVids);
        var releaseScopes = releaseIds.ToDictionary(
            kv => kv.Key,
            kv => (ReleaseId: kv.Value, Builder: new NativeSignatureBuilder(structure, itemCodes, kv.Value)),
            StringComparer.Ordinal);
        var loadMs = sw.ElapsedMilliseconds;

        var coverage = CoverageOf(sheets, releaseIds, tableVersions);

        sw.Restart();
        var comparison = NativeSignatureComparer.Compare(layoutData.Signatures, releaseScopes, tableVersions, structure);
        var compareMs = sw.ElapsedMilliseconds + loadMs;

        sw.Restart();
        var equality = NativeEqualityChecker.Check(layouts, releaseScopes, tableVersions, structure);
        var equalityMs = sw.ElapsedMilliseconds;

        sw.Restart();
        var hierarchy = NativeHierarchyChecker.Check(native, layouts, releaseScopes, tableVersions, structure, itemCodes);
        var hierarchyMs = sw.ElapsedMilliseconds;

        var checks = new List<CheckResult>();
        var divergences = new List<ExceptionInfo>();

        var (dps01, dpsEvaluated, dpsMatched) = BuildCDps01(comparison, censuses, compareMs);
        checks.Add(dps01);
        checks.Add(BuildCDps02(comparison, compareMs));
        checks.Add(BuildCCob01(layoutData, coverage, comparison, releaseScopes, compareMs));
        checks.Add(BuildCKey01(comparison, compareMs));
        checks.Add(BuildCEqu01(equality, equalityMs));
        checks.Add(BuildCEqu02(equality, equalityMs));

        var (mhr01, mhrEvaluated, mhrMatched) = BuildCMhr01(hierarchy, censuses, hierarchyMs);
        checks.Add(mhr01);
        checks.Add(BuildCMhr02(hierarchy, hierarchyMs));

        divergences.AddRange(DivergenceReport(censuses, "C-DPS-01", dpsEvaluated, dpsMatched));
        divergences.AddRange(DivergenceReport(censuses, "C-MHR-01", mhrEvaluated, mhrMatched));

        return new NativePlaneCResult(checks, divergences);
    }

    // ---- Layout sheets and coverage -----------------------------------------------------------------

    private sealed record SheetInfo(long SheetId, string TableCode, string ReleaseLabel);

    private static List<SheetInfo> LoadSheets(SqliteConnection layouts)
    {
        var sheets = new List<SheetInfo>();
        NativeSql.Query(
            layouts,
            """
            SELECT s."SheetId", s."TableCode", f."ReleaseLabel"
            FROM "LayoutSheet" s JOIN "LayoutFile" f ON f."FileId" = s."FileId"
            """,
            r => sheets.Add(new SheetInfo(r.GetInt64(0), r.GetString(1), r.GetString(2))));
        return sheets;
    }

    /// <summary>Layout sheets by release and by what happens to them.</summary>
    private sealed class SheetCoverage
    {
        public long Total { get; set; }

        public long ReleaseNotInFile { get; set; }

        public long NoTableVersionAtRelease { get; set; }

        public long SeveralTableVersions { get; set; }

        public long Paired { get; set; }
    }

    private static Dictionary<string, SheetCoverage> CoverageOf(
        List<SheetInfo> sheets, Dictionary<string, long> releaseIds, NativeTableVersions tableVersions)
    {
        var result = new Dictionary<string, SheetCoverage>(StringComparer.Ordinal);
        foreach (var sheet in sheets)
        {
            if (!result.TryGetValue(sheet.ReleaseLabel, out var c))
            {
                c = new SheetCoverage();
                result[sheet.ReleaseLabel] = c;
            }

            c.Total++;
            if (!releaseIds.TryGetValue(sheet.ReleaseLabel, out var releaseId))
            {
                c.ReleaseNotInFile++;
                continue;
            }

            switch (tableVersions.Find(sheet.TableCode, releaseId, out _))
            {
                case TableVersionLookup.Found:
                    c.Paired++;
                    break;
                case TableVersionLookup.Several:
                    c.SeveralTableVersions++;
                    break;
                default:
                    c.NoTableVersionAtRelease++;
                    break;
            }
        }

        return result;
    }

    // ---- C-DPS-01, C-DPS-02, C-KEY-01, C-COB-01 -----------------------------------------------------

    private static (CheckResult Check, HashSet<string> Evaluated, Dictionary<string, HashSet<string>> Matched) BuildCDps01(
        NativeComparison comparison, IReadOnlyList<NativePlaneCCensus> censuses, long elapsedMs)
    {
        var evaluated = comparison.ByRelease.Where(kv => kv.Value.TablesCompared > 0).Select(kv => kv.Key)
            .ToHashSet(StringComparer.Ordinal);
        var matched = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        if (evaluated.Count == 0)
        {
            // Zero tables compared: nothing can have expired over an empty universe, and the census is not evaluated.
            return (CheckResult.Skip(
                "C-DPS-01", ValidationPlane.C, ValidationLayer.Critical, "TableVersionCell", CDps01Statement,
                "0 tables compared: the census is not evaluated, nothing can have expired over an empty universe. " +
                "See C-COB-01 for the reason (release of the layouts not in the file, table codes without a table " +
                "version at the release...)."), evaluated, matched);
        }

        var unresolvedItems = new List<CheckSample>();
        var notes = new List<string>();
        foreach (var release in evaluated.Order(StringComparer.Ordinal))
        {
            var census = censuses.FirstOrDefault(c => c.CorpusRelease == release);
            if (census is null)
            {
                notes.Add($"; no native census declared for release {release}: evaluated with an EMPTY census " +
                          "(every violation is a failure, nothing can expire)");
            }

            var byKey = comparison.Violations.Where(v => v.Release == release)
                .ToDictionary(v => v.BusinessKey, StringComparer.Ordinal);
            var (unresolved, matchedKeys) = NativePlaneCCensuses.Apply("C-DPS-01", census, byKey.Keys.ToHashSet(StringComparer.Ordinal));
            matched[release] = matchedKeys;
            foreach (var key in unresolved)
            {
                unresolvedItems.Add(byKey.TryGetValue(key, out var v)
                    ? new CheckSample($"{release}|{key}", Generated: null, Reference: v.Type)
                    : new CheckSample($"{release}|{key}")); // a stale entry of the census.
            }
        }

        var statement = CDps01Statement + string.Concat(notes);
        var signatures = comparison.ByRelease.Values.Sum(s => s.SignaturesCompared);
        if (signatures == 0 && unresolvedItems.Count == 0)
        {
            // Tables are paired but no signature was examined: a Pass over zero objects would claim a check that did not run.
            // The census stays evaluated (a stale entry still surfaces); a stale entry above keeps the check from skipping.
            var excluded = comparison.ByRelease.Values.Sum(s => s.SignaturesExcludedKeyColumn);
            return (CheckResult.Skip(
                "C-DPS-01", ValidationPlane.C, ValidationLayer.Critical, "TableVersionCell", statement,
                $"0 signatures compared over {comparison.ByRelease.Values.Sum(s => s.TablesCompared)} table(s) compared: " +
                (excluded > 0
                    ? $"every layout signature of the compared tables was excluded as a key column (C-KEY-01, {excluded} excluded)."
                    : "the compared tables have no layout signature to examine (see C-COB-01).")), evaluated, matched);
        }

        return (CheckResult.FromViolationCount(
            "C-DPS-01", ValidationPlane.C, ValidationLayer.Critical, "TableVersionCell", statement,
            signatures, unresolvedItems.Count, elapsedMs, unresolvedItems,
            totalFailed: unresolvedItems.Count > CheckResult.MaxSamples ? unresolvedItems.Count : null), evaluated, matched);
    }

    private static CheckResult BuildCDps02(NativeComparison comparison, long elapsedMs)
    {
        const string statement =
            "terms that the native signatures emit and the layout does not mention; the layout legitimately omits some " +
            "(abstract rows, open-axis keys, table context): never critical";
        var tables = comparison.ByRelease.Values.Sum(s => s.TablesCompared);
        if (tables == 0)
        {
            return CheckResult.Skip(
                "C-DPS-02", ValidationPlane.C, ValidationLayer.Informative, "TableVersionCell", statement,
                "0 tables compared: nothing to count over an empty universe (see C-COB-01).");
        }

        var samples = comparison.ExtraTerms
            .Select(t => new CheckSample($"{t.Release}|{t.TableCode}|{t.Term}"))
            .Take(CheckResult.MaxSamples)
            .ToList();
        return CheckResult.FromViolationCount(
            "C-DPS-02", ValidationPlane.C, ValidationLayer.Informative, "TableVersionCell", statement,
            comparison.ByRelease.Values.Sum(s => s.NativeTermsExamined), comparison.ExtraTerms.Count, elapsedMs, samples,
            totalFailed: comparison.ExtraTerms.Count > CheckResult.MaxSamples ? comparison.ExtraTerms.Count : null);
    }

    private static CheckResult BuildCKey01(NativeComparison comparison, long elapsedMs)
    {
        var total = comparison.ByRelease.Values.Sum(s => s.SignaturesExcludedKeyColumn);
        var statement =
            $"open-axis key column: {total} layout signature(s) whose only MET(m) term names a dimension that the " +
            "signature itself carries as open axis m(*): the layout draws it as a grid column, in the native model it is " +
            "the key header (no cell); outside the population compared by C-DPS-01 (neither contained nor a violation), never silent";
        var samples = comparison.ByRelease
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .SelectMany(kv => kv.Value.KeyColumnExclusions.Select(e => new CheckSample($"{kv.Key}|{e}")))
            .ToList();
        return new CheckResult(
            "C-KEY-01", ValidationPlane.C, ValidationLayer.Informative, "TableVersionCell", statement,
            CheckStatus.Info, total, total, 0, elapsedMs, samples.Take(CheckResult.MaxSamples).ToList(),
            samples.Count > CheckResult.MaxSamples ? samples.Count : null);
    }

    private static CheckResult BuildCCob01(
        PlaneCLayoutData layoutData,
        Dictionary<string, SheetCoverage> coverage,
        NativeComparison comparison,
        IReadOnlyDictionary<string, (long ReleaseId, NativeSignatureBuilder Builder)> releases,
        long elapsedMs)
    {
        var samples = new List<CheckSample>();
        foreach (var (label, c) in coverage.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            comparison.ByRelease.TryGetValue(label, out var stats);
            stats ??= new NativeReleaseStats();
            samples.Add(new CheckSample(
                $"release {label}",
                Generated: $"{c.Total} sheet(s): {c.Paired} with a unique table version at the release, " +
                           $"{c.NoTableVersionAtRelease} without a table version valid at the release, " +
                           $"{c.SeveralTableVersions} with several table versions of the same code (not compared), " +
                           $"{c.ReleaseNotInFile} of a release not in the file (not compared); " +
                           $"{stats.TablesCompared} table(s) compared, {stats.SignaturesCompared} signature(s) " +
                           $"({stats.SignaturesContained} contained), {stats.SignaturesExcludedKeyColumn} excluded as key column, " +
                           $"{stats.TablesWithoutNativeSignature} table(s) paired without any native signature, " +
                           $"{stats.NativeCellsWithMissingCode} native cell(s) without signature for a code missing at the release, " +
                           $"{stats.NativeCellsWithoutVariable} native cell(s) not excluded and without variable"));
        }

        foreach (var (label, scope) in releases.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            foreach (var kv in scope.Builder.MissingByKind)
            {
                samples.Add(new CheckSample($"release {label}", Generated: $"{kv.Value} code(s) missing at the release: {kv.Key}"));
            }

            samples.AddRange(scope.Builder.MissingSamples.Take(5).Select(s => new CheckSample($"release {label}", Generated: s)));
        }

        var sheets = coverage.Values.Sum(c => c.Total);
        var statement =
            "plane C coverage on the native output, per release of the layouts (each sheet is compared at its own release; " +
            $"exact release match, no tolerance): {sheets} layout sheet(s) in {coverage.Count} release(s), " +
            $"{coverage.Values.Sum(c => c.Paired)} paired with a unique table version, " +
            $"{coverage.Values.Sum(c => c.NoTableVersionAtRelease)} without a table version valid at their release, " +
            $"{coverage.Values.Sum(c => c.SeveralTableVersions)} with several, " +
            $"{coverage.Values.Sum(c => c.ReleaseNotInFile)} of a release not in the file; " +
            $"{comparison.ByRelease.Values.Sum(s => s.TablesCompared)} table(s) and " +
            $"{comparison.ByRelease.Values.Sum(s => s.SignaturesCompared)} signature(s) compared; " +
            $"{layoutData.CellsWithEmptySignature} of {layoutData.NonShadedCellsExamined} layout cell(s) with an empty signature (not compared)";
        return new CheckResult(
            "C-COB-01", ValidationPlane.C, ValidationLayer.Informative, "TableVersionCell", statement,
            CheckStatus.Info, layoutData.NonShadedCellsExamined, layoutData.NonShadedCellsExamined, 0, elapsedMs,
            samples.Take(CheckResult.MaxSamples).ToList(), samples.Count > CheckResult.MaxSamples ? samples.Count : null);
    }

    // ---- C-EQU-01, C-EQU-02 ---------------------------------------------------------------------------

    private static CheckResult BuildCEqu01(NativeEqualityResult result, long elapsedMs)
    {
        if (result.RulesCheckable == 0)
        {
            return CheckResult.Skip(
                "C-EQU-01", ValidationPlane.C, ValidationLayer.Critical, "TableVersionCell", CEqu01Statement,
                $"0 checkable rules out of {result.RulesTotal} total: nothing to evaluate over an empty universe. " +
                "See C-EQU-02 for the reason.");
        }

        var samples = result.Violations
            .Select(v => new CheckSample(v.BusinessKey, Generated: string.Join(" | ", v.Resolved.Select(r => r.Signature))))
            .ToList();
        return CheckResult.FromViolationCount(
            "C-EQU-01", ValidationPlane.C, ValidationLayer.Critical, "TableVersionCell", CEqu01Statement,
            result.RulesCheckable, result.Violations.Count, elapsedMs, samples,
            totalFailed: samples.Count > CheckResult.MaxSamples ? samples.Count : null);
    }

    private static CheckResult BuildCEqu02(NativeEqualityResult result, long elapsedMs)
    {
        var statement =
            $"C-EQU-01 coverage, unit = equality RULE \"==\" of the cell comments: {result.RulesTotal} total rules, " +
            $"{result.RulesCheckable} checkable ({result.RulesIdentical} with identical signature, {result.RulesDivergent} " +
            $"with different signature), {result.RulesTotal - result.RulesCheckable} not checkable, breakdown by reason " +
            "below (not checkable != defect: a rule that cites an unfiltered sheet axis on a table with several sheets, or a " +
            "table outside this file, checks nothing and fails nothing)";
        var samples = result.NonCheckableByReason
            .OrderByDescending(kv => kv.Value)
            .Select(kv => new CheckSample(kv.Key, Generated: kv.Value.ToString()))
            .ToList();
        return new CheckResult(
            "C-EQU-02", ValidationPlane.C, ValidationLayer.Informative, "LayoutEqualityRule", statement,
            CheckStatus.Info, result.RulesTotal, result.RulesTotal, 0, elapsedMs, samples);
    }

    // ---- C-MHR-01, C-MHR-02 ---------------------------------------------------------------------------

    private static (CheckResult Check, HashSet<string> Evaluated, Dictionary<string, HashSet<string>> Matched) BuildCMhr01(
        NativeHierarchyResult result, IReadOnlyList<NativePlaneCCensus> censuses, long elapsedMs)
    {
        var matched = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var evaluated = new HashSet<string>(StringComparer.Ordinal);
        if (result.SkipReason is not null)
        {
            return (CheckResult.Skip(
                "C-MHR-01", ValidationPlane.C, ValidationLayer.Critical, "HeaderVersion", CMhr01Statement, result.SkipReason),
                evaluated, matched);
        }

        var total = result.ComparedByRelease.Values.Sum();
        if (total == 0)
        {
            return (CheckResult.Skip(
                "C-MHR-01", ValidationPlane.C, ValidationLayer.Critical, "HeaderVersion", CMhr01Statement,
                $"0 units compared out of {result.UnitsTotal} layout metric bracket(s): nothing to evaluate over an empty " +
                "universe, and nothing can have expired. See C-MHR-02 for the reason."), evaluated, matched);
        }

        var notes = new List<string>();
        var unresolvedItems = new List<CheckSample>();
        foreach (var release in result.ComparedByRelease.Where(kv => kv.Value > 0).Select(kv => kv.Key).Order(StringComparer.Ordinal))
        {
            evaluated.Add(release);
            var census = censuses.FirstOrDefault(c => c.CorpusRelease == release);
            if (census is null)
            {
                notes.Add($"; no native census declared for release {release}: evaluated with an EMPTY census " +
                          "(every violation is a failure, nothing can expire)");
            }

            var byKey = result.Violations.Where(v => v.Unit.ReleaseLabel == release)
                .ToDictionary(v => v.Unit.BusinessKey, StringComparer.Ordinal);
            var (unresolved, matchedKeys) = NativePlaneCCensuses.Apply("C-MHR-01", census, byKey.Keys.ToHashSet(StringComparer.Ordinal));
            matched[release] = matchedKeys;
            foreach (var key in unresolved)
            {
                unresolvedItems.Add(byKey.TryGetValue(key, out var v)
                    ? new CheckSample(key, Generated: v.NativeHierarchy, Reference: v.Unit.LayoutHierarchy)
                    : new CheckSample(key));
            }
        }

        var statement = CMhr01Statement + string.Concat(notes);
        return (CheckResult.FromViolationCount(
            "C-MHR-01", ValidationPlane.C, ValidationLayer.Critical, "HeaderVersion", statement,
            total, unresolvedItems.Count, elapsedMs, unresolvedItems,
            totalFailed: unresolvedItems.Count > CheckResult.MaxSamples ? unresolvedItems.Count : null), evaluated, matched);
    }

    private static CheckResult BuildCMhr02(NativeHierarchyResult result, long elapsedMs)
    {
        const string statement = "C-MHR-01 census, unit = layout metric value with a hierarchy bracket on an ordinate";
        if (result.SkipReason is not null)
        {
            return CheckResult.Skip(
                "C-MHR-02", ValidationPlane.C, ValidationLayer.Informative, "HeaderVersion", statement, result.SkipReason);
        }

        var compared = result.ComparedByRelease.Values.Sum();
        var reasons = string.Join(
            ", ", result.NotMatchedByReason.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}: {kv.Value}"));
        var full =
            $"{statement}: {result.UnitsTotal} total units, {compared} matched and compared, " +
            $"{result.MatchedWithoutHierarchy.Count} matched with no hierarchy on the header (not violations), " +
            $"{result.NotMatched} not matched ({(reasons.Length == 0 ? "no reason" : reasons)}), " +
            $"{result.SheetLevelNotExamined} sheet-level value(s) (no ordinate) not examined";
        var samples = result.MatchedWithoutHierarchy
            .Take(CheckResult.MaxSamples)
            .Select(u => new CheckSample(u.BusinessKey, Generated: "matched, no hierarchy on the header", Reference: u.LayoutHierarchy))
            .Concat(result.NotMatchedSamples.Take(10).Select(s => new CheckSample(s, Generated: "not matched")))
            .ToList();
        return new CheckResult(
            "C-MHR-02", ValidationPlane.C, ValidationLayer.Informative, "HeaderVersion", full,
            CheckStatus.Info, result.UnitsTotal, compared, 0, elapsedMs, samples);
    }

    // ---- Known divergences ------------------------------------------------------------------------------

    /// <summary>Every entry of every census for one check: applied (its release was evaluated), stale (applied
    /// and no longer violating), or not applicable (its release was not evaluated). When the check did not
    /// evaluate any release, no entry counts as evaluated.</summary>
    private static IEnumerable<ExceptionInfo> DivergenceReport(
        IReadOnlyList<NativePlaneCCensus> censuses, string check, HashSet<string> evaluatedReleases,
        Dictionary<string, HashSet<string>> matchedByRelease)
    {
        foreach (var census in censuses.OrderBy(c => c.CorpusRelease, StringComparer.Ordinal))
        {
            var applies = evaluatedReleases.Contains(census.CorpusRelease);
            foreach (var entry in census.Entries.Where(e => e.Check == check))
            {
                var stillViolating = applies
                    && matchedByRelease.TryGetValue(census.CorpusRelease, out var keys)
                    && keys.Contains(entry.BusinessKey);
                yield return new ExceptionInfo(
                    entry.Id, "originAnomaly", entry.BusinessKey, check, "C", "-",
                    $"Census of release {census.CorpusRelease}. {entry.Reason}",
                    applies, stillViolating ? 1 : 0, applies && !stillViolating, evaluatedReleases.Count > 0);
            }
        }
    }
}
