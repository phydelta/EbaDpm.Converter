using EbaDpm.Converter.Core.Validation.PlaneC;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Core.Validation.Checks;

/// <summary>
/// Plane C: the signature-by-signature comparison against the EBA Annotated Table Layouts. It
/// only runs with <c>--layouts</c> (an option independent of <c>--reference</c>): without it,
/// <see cref="Run"/> is not called and <c>Validator</c> prints an explicit notice saying why.
///
/// Three checks:
/// <list type="bullet">
/// <item><b><c>C-DPS-01</c></b>, CRITICAL: for every signature of the layout there is one of ours
/// that CONTAINS it, and no dimension mentioned by the layout carries another member. Violations
/// are exempted by the plane C census of known divergences: a NAMED list, never a threshold.</item>
/// <item><b><c>C-DPS-02</c></b>, INFORMATIVE: terms that we emit and the layout does not mention.
/// Never critical, because the layout legitimately omits some (the table context is half of this
/// gap).</item>
/// <item><b><c>C-COB-01</c></b>, INFORMATIVE: the coverage, with the examined units ALWAYS first
/// and every exclusion reason named.</item>
/// </list>
///
/// The plane C census of known divergences is TIED to the DPM 2.0 release 4.2 corpus and is not
/// portable to another output. There are THREE guards, DIFFERENT although the symptom looks
/// similar:
/// <list type="bullet">
/// <item><b>(a) Zero tables compared.</b> With <c>TablesCompared == 0</c> nothing can have
/// expired: evaluating it would yield "all of them expired" for an empty universe, a hollow zero.
/// <c>C-DPS-01</c> is emitted as <c>Skipped</c>, and the census is NOT EVALUATED (
/// <see cref="KnownExceptions.ApplyOriginAnomalyExceptions"/> is never called).</item>
/// <item><b>(b) WELL-DEFINED but different release.</b> With tables really compared and an output
/// CUTOFF release (<c>mRelease.IsCurrent=1</c> in EXACTLY one row) that differs from
/// <see cref="KnownExceptions.PlaneCCensusRelease"/>: the case of DPM 2.0 4.3 against the 4.2
/// census. The census does not apply (a comparison must state which publication it speaks of).
/// <c>C-DPS-01</c> is emitted as <c>Skipped</c>, naming how many of the declared entries DO
/// reproduce against this output (structural evidence, not a failure of this run). Identical on
/// 4.2 and 4.3.</item>
/// <item><b>(b') UNDETERMINED release: DPM 1.0.</b> <c>mRelease.IsCurrent=1</c> in MORE than one
/// row (128 taxonomies from 2.0 to 4.1 at once): there is no SINGLE output release to ask whether
/// it matches that of the census, a different question from (b), not one more case of the same.
/// The table-to-sheet matching is ALREADY done by each table's OWN release
/// (<see cref="PlaneC.PlaneCReleaseMatcher"/>), so the comparison is valid anyway; what cannot be
/// consulted is the CENSUS, and an empty one is not invented: <c>C-DPS-01</c> is EVALUATED with the
/// raw violations (without going through the census), and can fail again.</item>
/// </list>
/// Outside (a)/(b)/(b'), <c>C-DPS-01</c> is evaluated as usual.
/// </summary>
public static class PlaneCChecks
{
    public static IEnumerable<CheckResult> Run(
        SqliteConnection generated, SqliteConnection layouts, List<KnownExceptions.Outcome> exceptionSink)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = PlaneCComparer.Compare(generated, layouts);
        sw.Stop();

        yield return BuildCDps01(generated, result, sw.ElapsedMilliseconds, exceptionSink);

        var extraSamples = result.ExtraTerms
            .Select(t => new CheckSample(t.BusinessKey))
            .Take(CheckResult.MaxSamples)
            .ToList();
        yield return CheckResult.FromViolationCount(
            "C-DPS-02", ValidationPlane.C, ValidationLayer.Informative, "mTableCell",
            "terms that we emit and the layout does not mention; the layout legitimately omits some " +
            "(abstract rows, open axis, table context): never critical",
            result.ExtraTerms.Count, result.ExtraTerms.Count, sw.ElapsedMilliseconds, extraSamples,
            totalFailed: result.ExtraTerms.Count > CheckResult.MaxSamples ? result.ExtraTerms.Count : null);

        var c = result.Coverage;
        var coverageStatement =
            $"plane C coverage, unit = table CODE (the layout publishes one sheet per code, not per " +
            $"TableID): {c.TablesCompared} tables compared, " +
            $"{c.SignaturesCompared} signatures compared ({c.SignaturesContained} contained); excluded: " +
            $"{c.TablesSkippedNoMatchingLayoutSheet} table(s) without a layout sheet of their release, " +
            $"{c.TablesSkippedNoOutputRows} table(s) without output, {c.LayoutTableCodesWithoutGeneratedTable} " +
            $"layout TableCode(s) without a table in the output, {c.LayoutCellsWithEmptySignature} of " +
            $"{c.LayoutCellsExamined} layout cell(s) with an empty signature (not compared), " +
            $"{c.SignaturesExcludedKeyColumn} signature(s) excluded for being THE KEY COLUMN of an open axis " +
            "(unique MET(m) and m(*) in the same signature: the layout draws it as a column, in the target " +
            "IT IS THE AXIS, with no possible counterpart; see C-KEY-01 for the per-table breakdown); " +
            $"{c.TableCodesWithMultipleTableIds} code(s) with more than one TableID " +
            $"({c.TableCodesWithMultipleTableIdsAndIdenticalSignatures} with identical signatures across their TableIDs), " +
            $"{c.TableCodesWithInconsistentClass} code(s) with an inconsistent table class across their TableIDs " +
            "(0 expected)";
        // Coverage is ALWAYS announced, even when there is nothing to report: Status=Info is fixed
        // (not FromViolationCount, which would collapse to "pass" with Failed=0 and stop being
        // printed in PrintPlane). A plane that only speaks when it fails is a hollow zero.
        yield return new CheckResult(
            "C-COB-01", ValidationPlane.C, ValidationLayer.Informative, "mTableCell", coverageStatement,
            CheckStatus.Info, c.LayoutCellsExamined, c.LayoutCellsExamined, 0, sw.ElapsedMilliseconds, []);

        // Measured per TableID: the REGULAR / OPEN_AXIS / REAL_Z breakdown, always visible and not
        // only when something fails, so that it can be verified object by object.
        var classSamples = result.Coverage.ByClass
            .OrderBy(kv => kv.Key)
            .Select(kv => new CheckSample(
                ClassLabel(kv.Key),
                Generated: $"{kv.Value.Tables} tables, {kv.Value.SignaturesCompared} signatures, {kv.Value.SignaturesContained} contained"))
            .ToList();
        yield return new CheckResult(
            "C-CLS-01", ValidationPlane.C, ValidationLayer.Informative, "mTable",
            "breakdown by table class (measured per TableID, aggregated by code afterwards: grouping " +
            "by TableCode before classifying fabricates a fourth class that does not exist)",
            CheckStatus.Info, c.TablesCompared, c.TablesCompared, 0, sw.ElapsedMilliseconds, classSamples);

        // The REL.n -> REL release tolerance is NAMED and never silent: it is announced whether or
        // not it was ever applied.
        var toleranceStatement =
            $"release tolerance REL.n → REL (hotfix/point release): {c.SheetsMatchedByPointReleaseTolerance} " +
            "layout sheet(s) matched ONLY by this tolerance (not by exact equality)";
        yield return new CheckResult(
            "C-REL-01", ValidationPlane.C, ValidationLayer.Informative, "LayoutSheet", toleranceStatement,
            CheckStatus.Info, c.SheetsMatchedByPointReleaseTolerance, c.SheetsMatchedByPointReleaseTolerance, 0,
            sw.ElapsedMilliseconds, c.PointReleaseToleranceSamples.Select(s => new CheckSample(s)).ToList());

        // The key column of the open axis, NAMED per table, never a loose count. The layout draws
        // it as one more cell of the grid (Main Property = the dimension itself); in the target it
        // is the axis, not a cell, so it leaves the population compared by C-DPS-01 instead of
        // counting as a violation.
        var keyColumnStatement =
            $"open-axis key column: {c.SignaturesExcludedKeyColumn} layout signature(s) whose " +
            "only MET(m) term names a dimension that the signature itself carries as open axis m(*): the " +
            "layout draws it as a grid column, in the target IT IS THE AXIS (same code and label); " +
            "outside the population compared by C-DPS-01 (neither contained nor a violation), never silent";
        yield return new CheckResult(
            "C-KEY-01", ValidationPlane.C, ValidationLayer.Informative, "mTableCell", keyColumnStatement,
            CheckStatus.Info, c.SignaturesExcludedKeyColumn, c.SignaturesExcludedKeyColumn, 0,
            sw.ElapsedMilliseconds, c.SignaturesExcludedKeyColumnSamples.Select(s => new CheckSample(s)).ToList());

        // The "==" equality rules in the cell comments: a positive control of the CONVERSION that
        // does not go through the reference database and crosses the boundary of a table.
        // Independent of the rest of plane C (it does not use PlaneCLayoutReader nor a DPS rebuilt
        // from the layout): it resolves every term directly against mAxis/mAxisOrdinate/
        // mCellPosition/mTableCell of THE OUTPUT; see PlaneCEqualityChecker.
        var equSw = System.Diagnostics.Stopwatch.StartNew();
        var equResult = PlaneCEqualityChecker.Check(generated, layouts);
        equSw.Stop();

        yield return BuildCEqu01(equResult, equSw.ElapsedMilliseconds, exceptionSink);

        var nonCheckableSamples = equResult.Coverage.NonCheckableByReason
            .OrderByDescending(kv => kv.Value)
            .Select(kv => new CheckSample(kv.Key, Generated: kv.Value.ToString()))
            .ToList();
        var equCoverageStatement =
            $"C-EQU-01 coverage, unit = equality RULE \"==\" of the cell comments: " +
            $"{equResult.Coverage.RulesTotal} total rules, {equResult.Coverage.RulesCheckable} " +
            $"checkable ({equResult.Coverage.RulesIdentical} with identical signature, " +
            $"{equResult.Coverage.RulesDivergent} with different signature); of the identical ones, " +
            $"{equResult.Coverage.RulesRelaxedByOpenAxisBracket} NEEDED the open-axis bracket to be " +
            "relaxed to hold (the \"==\" asserts Variable identity, and the restriction bracket " +
            "belongs to the table, not to the Variable; without relaxing they diverged); " +
            $"{equResult.Coverage.RulesTotal - equResult.Coverage.RulesCheckable} not checkable, breakdown " +
            "by reason below (not checkable != defect: a rule that cites an unfiltered Z axis " +
            "on a table with a real Z, or a table outside the universe of this output, checks nothing " +
            "and fails nothing).";
        yield return new CheckResult(
            "C-EQU-02", ValidationPlane.C, ValidationLayer.Informative, "LayoutEqualityRule", equCoverageStatement,
            CheckStatus.Info, equResult.Coverage.RulesTotal, equResult.Coverage.RulesTotal, 0, equSw.ElapsedMilliseconds,
            nonCheckableSamples);

        // The REL.n -> REL tolerance applies to C-EQU-01 just as to C-DPS-01/C-REL-01: NAMED here
        // whether or not it was ever applied, never silent.
        var equToleranceStatement =
            $"release tolerance REL.n → REL (hotfix/point release) on C-EQU-01: " +
            $"{equResult.Coverage.SheetsMatchedByPointReleaseTolerance} rule-origin sheet(s) matched " +
            "ONLY by this tolerance (not by exact equality); same shared mechanism of PlaneCReleaseMatcher.";
        yield return new CheckResult(
            "C-EQU-03", ValidationPlane.C, ValidationLayer.Informative, "LayoutEqualityRule", equToleranceStatement,
            CheckStatus.Info, equResult.Coverage.SheetsMatchedByPointReleaseTolerance,
            equResult.Coverage.SheetsMatchedByPointReleaseTolerance, 0, equSw.ElapsedMilliseconds,
            equResult.Coverage.PointReleaseToleranceSamples.Select(s => new CheckSample(s)).ToList());
    }

    private static CheckResult BuildCEqu01(
        PlaneCEqualityChecker.EqualityCheckResult result, long elapsedMs, List<KnownExceptions.Outcome> exceptionSink)
    {
        const string statement =
            "all cells named by an equality rule \"==\" in the cell comments of the " +
            "Annotated Table Layout carry the same DatapointSignature in this output, " +
            "relaxing the open-axis restriction bracket (the \"==\" asserts " +
            "VARIABLE identity, and that bracket belongs to the table, not to the Variable): INTERNAL " +
            "consistency of the signature, not proof that the signature is correct (complementary to " +
            "the rest of plane C)";

        // Guard (a): 0 checkable rules cannot produce findings, and it does not even distinguish
        // "no --layouts with LayoutEqualityRule" from "really 0 rules".
        if (result.Coverage.RulesCheckable == 0)
        {
            return CheckResult.Skip(
                "C-EQU-01", ValidationPlane.C, ValidationLayer.Critical, "mTableCell", statement,
                $"0 checkable rules out of {result.Coverage.RulesTotal} total: nothing to evaluate " +
                "over an empty universe. See C-EQU-02 for the reason (e.g. a layout repository " +
                "without LayoutEqualityRule, or no term matching the release with the origin sheet of " +
                "its rule: \"different release\", the case of an output of a different publication " +
                "than the layout repository, e.g. DPM 1.0 against a DPM 2.0 repository).");
        }

        var violatingKeys = result.Violations.Select(v => v.BusinessKey).ToHashSet(StringComparer.Ordinal);
        var (unresolved, outcomes) = KnownExceptions.ApplyOriginAnomalyExceptions("C-EQU-01", violatingKeys, ValidationPlane.C);
        exceptionSink.AddRange(outcomes);

        var violationByKey = result.Violations.ToDictionary(v => v.BusinessKey, StringComparer.Ordinal);
        var samples = unresolved
            .Select(key => violationByKey.TryGetValue(key, out var v)
                ? new CheckSample(v.BusinessKey, Generated: string.Join(" | ", v.Resolved.Select(r => r.Signature)))
                : new CheckSample(key)) // "(absent, <Id> no longer violates) ..." - a declared exception that expired.
            .ToList();

        return CheckResult.FromViolationCount(
            "C-EQU-01", ValidationPlane.C, ValidationLayer.Critical, "mTableCell", statement,
            result.Coverage.RulesCheckable, unresolved.Count, elapsedMs, samples);
    }

    private static string ClassLabel(PlaneCTableClass cls) => cls switch
    {
        PlaneCTableClass.Regular => "REGULAR",
        PlaneCTableClass.OpenAxis => "OPEN_AXIS",
        PlaneCTableClass.ZReal => "REAL_Z",
        _ => cls.ToString(),
    };

    private static CheckResult BuildCDps01(
        SqliteConnection generated, PlaneCResult result, long elapsedMs, List<KnownExceptions.Outcome> exceptionSink)
    {
        const string statement =
            "every datapoint signature of the layout is CONTAINED in some signature of ours, and no dimension " +
            "mentioned by the layout carries another member; violations NOT listed in the plane C census of known divergences";

        // Guard (a): zero examined units cannot produce findings nor expirations.
        if (result.Coverage.TablesCompared == 0)
        {
            return CheckResult.Skip(
                "C-DPS-01", ValidationPlane.C, ValidationLayer.Critical, "mTableCell", statement,
                "0 tables compared: the plane C census is not evaluated, nothing can have expired over an " +
                "empty universe. See C-COB-01 for the reason (e.g. the TaxonomyCode of this output does not " +
                "follow \"{framework} {release}\" - DPM 1.0).");
        }

        // Guard (b): the plane C census is tied to the DPM 2.0 release on which it was measured
        // (KnownExceptions.PlaneCCensusRelease). Against another WELL-DEFINED release (e.g. the
        // DPM 2.0 4.3 output against the 4.2 census) it is not evaluated; the message names how many of
        // its entries reproduce equally against this output (structural finding). Identical on
        // 4.2 and 4.3.
        var censusRelease = KnownExceptions.PlaneCCensusRelease;
        var generatedRelease = DetectCutoffRelease(generated);
        if (censusRelease is not null && generatedRelease is not null && generatedRelease != censusRelease)
        {
            var censusKeysSkip = KnownExceptions.All.Where(e => e.Id == "DD-23").Select(e => e.BusinessKey).ToHashSet(StringComparer.Ordinal);
            var violationKeysSkip = result.Violations.Select(v => v.BusinessKey).ToHashSet(StringComparer.Ordinal);
            var reproducedSkip = censusKeysSkip.Count(k => violationKeysSkip.Contains(k));

            return CheckResult.Skip(
                "C-DPS-01", ValidationPlane.C, ValidationLayer.Critical, "mTableCell", statement,
                $"plane C census measured on DPM 2.0 release {censusRelease}; the cutoff release of this output is " +
                $"{generatedRelease}: not evaluated (a comparison must state which publication it speaks of). Of the " +
                $"{censusKeysSkip.Count} declared entries, {reproducedSkip} reproduce the SAME violation against the " +
                "tables of this output (structural finding, not a defect of this run).");
        }

        // Guard (b'): the output does NOT resolve a single cutoff release: mRelease.IsCurrent=1 in
        // more than one row, the case of DPM 1.0 with 128 taxonomies from 2.0 to 4.1 at once
        // (DetectCutoffRelease returns null). This is NOT "the release does not match the census"
        // (guard above): it is that there is no SINGLE release to ask whether it matches. Two
        // different things were being confused under the same condition:
        //   - the table-to-layout-sheet MATCHING is already done by each table's OWN release
        //     (PlaneCReleaseMatcher.ClassifyRelease, shared with C-EQU-01). It does NOT depend on
        //     the output having a single cutoff release, and it already works: it compared 472
        //     tables / 86,115 signatures with 0 violations on DPM 1.0.
        //   - the plane C CENSUS is a list of NAMED exceptions, measured and dated on 4.2: there is
        //     no version of it for "12 DPM 1.0 releases at once", and an empty one is not fabricated
        //     to make the check pass. So the census is NOT consulted at all here, neither to exempt
        //     nor to accuse. Every violation that appears is, by construction, a real violation of
        //     THIS run: nothing names it as a known exception of any applicable census.
        // The census is not relaxed: it is declared as not applicable and the comparison is measured
        // without it, so the CRITICAL check can fail again on DPM 1.0.
        if (generatedRelease is null)
        {
            var rawSamples = result.Violations
                .Select(v => new CheckSample(v.BusinessKey, Generated: null, Reference: v.TypeText))
                .ToList();

            return CheckResult.FromViolationCount(
                "C-DPS-01", ValidationPlane.C, ValidationLayer.Critical, "mTableCell",
                statement + $"; plane C census (measured on DPM 2.0 release {censusRelease}) NOT CONSULTED: this " +
                "output does not resolve a single cutoff release (mRelease.IsCurrent=1 in more than one row - DPM " +
                "1.0, taxonomies of several releases at once), so there is no SINGLE release to contrast " +
                "the census with. The table-to-layout-sheet matching is already done by each table's OWN release " +
                "(PlaneCReleaseMatcher), so the comparison remains valid object by object; the only thing that " +
                "does not apply is the LIST OF EXCEPTIONS, tied to 4.2; an empty one is not invented: every " +
                "violation from here on is a real failure of this run.",
                result.Coverage.SignaturesCompared, result.Violations.Count, elapsedMs, rawSamples,
                totalFailed: result.Violations.Count > CheckResult.MaxSamples ? result.Violations.Count : null);
        }

        var violatingKeys = result.Violations.Select(v => v.BusinessKey).ToHashSet(StringComparer.Ordinal);
        var (unresolved, outcomes) = KnownExceptions.ApplyOriginAnomalyExceptions("C-DPS-01", violatingKeys, ValidationPlane.C);
        exceptionSink.AddRange(outcomes);

        var violationByKey = result.Violations.ToDictionary(v => v.BusinessKey, StringComparer.Ordinal);
        var samples = unresolved
            .Select(key => violationByKey.TryGetValue(key, out var v)
                ? new CheckSample(v.BusinessKey, Generated: null, Reference: v.TypeText)
                : new CheckSample(key)) // "(absent, <Id> no longer violates) ..." - expired, see KnownExceptions.
            .ToList();

        return CheckResult.FromViolationCount(
            "C-DPS-01", ValidationPlane.C, ValidationLayer.Critical, "mTableCell", statement,
            result.Coverage.SignaturesCompared, unresolved.Count, elapsedMs, samples);
    }

    /// <summary>
    /// The CUTOFF release of the generated output: <c>mRelease.IsCurrent=1</c>. DPM 2.0 marks
    /// <c>IsCurrent=1</c> in EXACTLY one row, that of the conversion cutoff ("4.2" when converting
    /// the 4.2 Access, "4.3" when converting the 4.3 one). DPM 1.0 marks <c>IsCurrent=1</c> in ALL
    /// its releases (different semantics, not a cutoff), which is indistinguishable from "there is
    /// no single cutoff release", so it is treated the same: <see langword="null"/> (defensive:
    /// without clear data nothing is asserted).
    /// </summary>
    private static string? DetectCutoffRelease(SqliteConnection generated)
    {
        var rows = SqlHelpers.Rows(generated, """SELECT DISTINCT "ReleaseCode" FROM "mRelease" WHERE "IsCurrent" = 1""", 1);
        return rows.Count == 1 ? rows[0][0] : null;
    }
}
