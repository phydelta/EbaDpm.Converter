using EbaDpm.Converter.Core.Layouts;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Native.PlaneC;

internal sealed record NativeEqualityTerm(string Term, string TableCode, string RowCode, string ColumnCode, string? ZCode);

/// <summary>A resolved rule whose cells do not carry the same native signature. The business key is
/// bound to the TEXT of the terms, not to the extraction-order <c>RuleId</c>.</summary>
internal sealed record NativeEqualityViolation(long RuleId, IReadOnlyList<(string Term, string Signature)> Resolved)
{
    public string BusinessKey => string.Join("==", Resolved.Select(r => "{" + r.Term + "}"));
}

internal sealed record NativeEqualityResult(
    long RulesTotal,
    long RulesCheckable,
    long RulesIdentical,
    long RulesDivergent,
    IReadOnlyDictionary<string, long> NonCheckableByReason,
    IReadOnlyList<NativeEqualityViolation> Violations);

/// <summary>
/// The <c>==</c> equality rules of the layout cell comments, over the native model: the cells a rule
/// names must carry the same native datapoint signature. It verifies the internal consistency of the
/// signature among the cells of a rule (it crosses table boundaries), never that the signature is the
/// right one.
///
/// A term (<c>TableCode</c>, row, column, optional sheet) is resolved at the release of the sheet the
/// rule comes from: the table version with that code valid at that release, and in it the single
/// <c>TableVersionCell</c> whose <c>Cell.RowID</c>, <c>ColumnID</c> and <c>SheetID</c> point to headers
/// (<c>TableVersionHeader.HeaderVID</c> -&gt; <c>HeaderVersion.Code</c>) with the layout's ordinate
/// codes. <c>9999</c> is the open axis sentinel: the cell has no header on that axis (the axis is a key
/// header, which no cell points to). No sheet code, or <c>0000</c>, applies no sheet filter: on a table
/// with several sheets that leaves more than one candidate cell and the rule is not checkable
/// ("ambiguous cell"). A term that cannot be resolved makes the rule not checkable, with its reason;
/// that is never a failure.
/// </summary>
internal static class NativeEqualityChecker
{
    private const string OpenSentinel = "9999";

    public static NativeEqualityResult Check(
        SqliteConnection layouts,
        IReadOnlyDictionary<string, (long ReleaseId, NativeSignatureBuilder Builder)> releases,
        NativeTableVersions tableVersions,
        NativeStructure structure)
    {
        var (rules, sheetIdByRule) = LoadRules(layouts);
        var releaseBySheet = new Dictionary<long, string>();
        NativeSql.Query(
            layouts,
            """SELECT s."SheetId", f."ReleaseLabel" FROM "LayoutSheet" s JOIN "LayoutFile" f ON f."FileId" = s."FileId" """,
            r => releaseBySheet[r.GetInt64(0)] = r.GetString(1));

        long checkable = 0, identical = 0, divergent = 0;
        var nonCheckable = new Dictionary<string, long>(StringComparer.Ordinal);
        var violations = new List<NativeEqualityViolation>();

        foreach (var (ruleId, terms) in rules)
        {
            if (!releaseBySheet.TryGetValue(sheetIdByRule[ruleId], out var releaseLabel))
            {
                Bump(nonCheckable, "rule release unresolved");
                continue;
            }

            if (!releases.TryGetValue(releaseLabel, out var release))
            {
                Bump(nonCheckable, "release not in the file");
                continue;
            }

            var statuses = new List<string>(terms.Count);
            var resolved = new List<(string Term, string Signature)>(terms.Count);
            foreach (var term in terms)
            {
                var (status, signature) = Resolve(term, release.ReleaseId, release.Builder, tableVersions, structure);
                statuses.Add(status);
                if (signature is not null)
                {
                    resolved.Add((term.Term, signature));
                }
            }

            if (statuses.Any(s => s != "ok"))
            {
                // Deterministic: the alphabetically smallest of the distinct reasons.
                Bump(nonCheckable, statuses.Where(s => s != "ok").Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).First());
                continue;
            }

            checkable++;
            if (resolved.Select(r => r.Signature).Distinct(StringComparer.Ordinal).Count() == 1)
            {
                identical++;
            }
            else
            {
                divergent++;
                violations.Add(new NativeEqualityViolation(ruleId, resolved));
            }
        }

        return new NativeEqualityResult(rules.Count, checkable, identical, divergent, nonCheckable, violations);
    }

    private static (string Status, string? Signature) Resolve(
        NativeEqualityTerm term,
        long releaseId,
        NativeSignatureBuilder builder,
        NativeTableVersions tableVersions,
        NativeStructure structure)
    {
        switch (tableVersions.Find(term.TableCode, releaseId, out var tableVid))
        {
            case TableVersionLookup.NotValidAtRelease:
                return ("no table version at the release", null);
            case TableVersionLookup.Several:
                return ("several table versions at the release", null);
        }

        if (!structure.Tables.TryGetValue(tableVid, out var table))
        {
            return ("no table version at the release", null);
        }

        var rowIds = HeaderIds(table, term.RowCode);
        var columnIds = HeaderIds(table, term.ColumnCode);
        var sheetFiltered = term.ZCode is not null && term.ZCode != "0000";
        var sheetIds = sheetFiltered ? HeaderIds(table, term.ZCode!) : null;

        if ((rowIds.Count == 0 && term.RowCode != OpenSentinel)
            || (columnIds.Count == 0 && term.ColumnCode != OpenSentinel)
            || (sheetFiltered && sheetIds!.Count == 0 && term.ZCode != OpenSentinel))
        {
            return ("cell unresolved", null);
        }

        var candidates = table.Cells.Where(c =>
            Matches(c.RowId, term.RowCode, rowIds)
            && Matches(c.ColumnId, term.ColumnCode, columnIds)
            && (!sheetFiltered || Matches(c.SheetId, term.ZCode!, sheetIds!))).ToList();
        if (candidates.Count == 0)
        {
            return ("cell unresolved", null);
        }

        if (candidates.Count > 1)
        {
            return ("ambiguous cell", null);
        }

        var (signature, status) = builder.SignatureOf(table, candidates[0]);
        return status switch
        {
            NativeSignatureBuilder.CellStatus.Ok => ("ok", signature!.Canonical),
            NativeSignatureBuilder.CellStatus.MissingCode => ("signature with a missing code", null),
            _ => ("shaded or without signature", null),
        };
    }

    private static bool Matches(long? headerId, string code, HashSet<long> ids) =>
        headerId is { } id ? ids.Contains(id) : code == OpenSentinel;

    private static HashSet<long> HeaderIds(NativeTableData table, string code) =>
        table.Headers.Where(h => string.Equals(h.Code, code, StringComparison.Ordinal)).Select(h => h.HeaderId).ToHashSet();

    private static void Bump(Dictionary<string, long> counts, string reason) =>
        counts[reason] = counts.GetValueOrDefault(reason) + 1;

    private static (Dictionary<long, List<NativeEqualityTerm>> Rules, Dictionary<long, long> SheetIdByRule) LoadRules(
        SqliteConnection layouts)
    {
        var rules = new Dictionary<long, List<NativeEqualityTerm>>();
        var sheetIdByRule = new Dictionary<long, long>();

        // An older layout repository has no LayoutEqualityRule: it contributes no rules, it does not fail.
        if (!NativeSql.TableExists(layouts, "LayoutEqualityRule"))
        {
            return (rules, sheetIdByRule);
        }

        NativeSql.Query(
            layouts,
            """
            SELECT "RuleId", "SheetId", "Term", "TableCode", "RowCode", "ColumnCode", "ZCode"
            FROM "LayoutEqualityRule" ORDER BY "RuleId", "Position"
            """,
            r =>
            {
                var ruleId = r.GetInt64(0);
                if (!rules.TryGetValue(ruleId, out var list))
                {
                    list = [];
                    rules[ruleId] = list;
                    sheetIdByRule[ruleId] = r.GetInt64(1);
                }

                // The term's table code is already canonical in the repository; the native side goes
                // through the same normaliser in NativeTableVersions.
                list.Add(new NativeEqualityTerm(
                    r.GetString(2), LayoutTableCodeNormalizer.Canonicalize(r.GetString(3)), r.GetString(4).Trim(),
                    r.GetString(5).Trim(), NativeSql.NullableString(r, 6)?.Trim()));
            });

        return (rules, sheetIdByRule);
    }
}
