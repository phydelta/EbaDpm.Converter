using EbaDpm.Converter.Core.Validation.PlaneC;

namespace EbaDpm.Converter.Native.PlaneC;

/// <summary>One layout signature that no native signature contains. <see cref="Type"/> is a diagnosis
/// (<c>contradicts</c>: a dimension the layout mentions has another member; <c>no-match</c>: no native
/// signature has the layout's dimensions), not part of the business key.</summary>
internal sealed record NativeViolation(string Release, string TableCode, string Type, string Canonical)
{
    /// <summary>The business key inside the census of the release: <c>{TableCode}\t{LayoutSignature}</c>.</summary>
    public string BusinessKey => $"{TableCode}\t{Canonical}";
}

/// <summary>Figures of <c>C-DPS-01</c> for one release.</summary>
internal sealed class NativeReleaseStats
{
    public long TablesCompared { get; set; }

    public long SignaturesCompared { get; set; }

    public long SignaturesContained { get; set; }

    public long SignaturesExcludedKeyColumn { get; set; }

    public long TablesWithoutNativeSignature { get; set; }

    public long NativeCellsWithMissingCode { get; set; }

    public long NativeCellsWithoutVariable { get; set; }

    public long NativeTermsExamined { get; set; }

    public List<string> KeyColumnExclusions { get; } = [];
}

internal sealed record NativeExtraTerm(string Release, string TableCode, string Term);

internal sealed record NativeComparison(
    IReadOnlyList<NativeViolation> Violations,
    IReadOnlyList<NativeExtraTerm> ExtraTerms,
    IReadOnlyDictionary<string, NativeReleaseStats> ByRelease);

/// <summary>
/// The signature comparison of plane C over the native model. For each release R of the layouts, and for
/// each table code with a unique table version at R, every layout signature (one per non-shaded layout
/// cell, sheets of the same code taken together) must be CONTAINED in one of the native signatures of
/// that table version (never set equality), and no dimension it mentions may carry a different member
/// in the signatures that share its dimensions. The key column of an open table (a layout signature
/// whose only <c>MET(m)</c> names a dimension the signature itself carries as <c>m(*)</c>) is outside the
/// compared population, counted and named.
/// </summary>
internal static class NativeSignatureComparer
{
    public static NativeComparison Compare(
        IReadOnlyList<LayoutCellSignature> layoutSignatures,
        IReadOnlyDictionary<string, (long ReleaseId, NativeSignatureBuilder Builder)> releases,
        NativeTableVersions tableVersions,
        NativeStructure structure)
    {
        var violations = new List<NativeViolation>();
        var extraTerms = new List<NativeExtraTerm>();
        var statsByRelease = new Dictionary<string, NativeReleaseStats>(StringComparer.Ordinal);

        foreach (var releaseGroup in layoutSignatures.GroupBy(s => s.ReleaseLabel, StringComparer.Ordinal))
        {
            if (!releases.TryGetValue(releaseGroup.Key, out var release))
            {
                continue; // the release is not in the file: counted by the caller, nothing to compare.
            }

            var stats = new NativeReleaseStats();
            statsByRelease[releaseGroup.Key] = stats;

            foreach (var codeGroup in releaseGroup.GroupBy(s => s.TableCode, StringComparer.Ordinal))
            {
                if (tableVersions.Find(codeGroup.Key, release.ReleaseId, out var tableVid) != TableVersionLookup.Found
                    || !structure.Tables.TryGetValue(tableVid, out var table))
                {
                    continue;
                }

                var (nativeSignatures, missingCode, withoutVariable) = release.Builder.Build(table);
                stats.NativeCellsWithMissingCode += missingCode;
                stats.NativeCellsWithoutVariable += withoutVariable;

                var distinctNative = nativeSignatures
                    .GroupBy(s => s.Canonical, StringComparer.Ordinal)
                    .Select(g => g.First().Terms)
                    .ToList();
                if (distinctNative.Count == 0)
                {
                    stats.TablesWithoutNativeSignature++;
                    continue;
                }

                CompareTable(
                    releaseGroup.Key, codeGroup.Key, codeGroup.ToList(), distinctNative, stats, violations, extraTerms);
            }
        }

        return new NativeComparison(violations, extraTerms, statsByRelease);
    }

    private static void CompareTable(
        string release,
        string tableCode,
        List<LayoutCellSignature> layoutSignatures,
        List<IReadOnlyList<string>> nativeSignatures,
        NativeReleaseStats stats,
        List<NativeViolation> violations,
        List<NativeExtraTerm> extraTerms)
    {
        var termIndex = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var dimIndex = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var nativeUniverse = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < nativeSignatures.Count; i++)
        {
            foreach (var term in nativeSignatures[i])
            {
                nativeUniverse.Add(term);
                Add(termIndex, term, i);
                Add(dimIndex, PlaneCSignature.DimensionOf(term), i);
            }
        }

        var layoutUniverse = new HashSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        long compared = 0;
        long contained = 0;
        long keyColumn = 0;
        foreach (var layoutSignature in layoutSignatures)
        {
            foreach (var term in layoutSignature.NormalizedTerms)
            {
                layoutUniverse.Add(term);
            }

            var canonical = PlaneCSignature.Canonicalize(layoutSignature.NormalizedTerms);
            if (!seen.Add(canonical))
            {
                continue; // a duplicate within the layouts of this table and release.
            }

            if (IsOpenAxisKeyColumnSignature(layoutSignature.NormalizedTerms))
            {
                keyColumn++;
                continue;
            }

            compared++;
            if (HasCommonCandidate(layoutSignature.NormalizedTerms, termIndex))
            {
                contained++;
                continue;
            }

            var dimensions = layoutSignature.NormalizedTerms
                .Select(PlaneCSignature.DimensionOf)
                .Distinct(StringComparer.Ordinal);
            var type = HasCommonCandidate(dimensions, dimIndex) ? "contradicts" : "no-match";
            violations.Add(new NativeViolation(release, tableCode, type, canonical));
        }

        stats.TablesCompared++;
        stats.SignaturesCompared += compared;
        stats.SignaturesContained += contained;
        stats.SignaturesExcludedKeyColumn += keyColumn;
        stats.NativeTermsExamined += nativeUniverse.Count;
        if (keyColumn > 0)
        {
            stats.KeyColumnExclusions.Add($"{tableCode} ({keyColumn})");
        }

        foreach (var term in nativeUniverse.Where(t => !layoutUniverse.Contains(t)))
        {
            extraTerms.Add(new NativeExtraTerm(release, tableCode, term));
        }
    }

    private static void Add(Dictionary<string, List<int>> index, string key, int value)
    {
        if (!index.TryGetValue(key, out var list))
        {
            list = [];
            index[key] = list;
        }

        if (list.Count == 0 || list[^1] != value)
        {
            list.Add(value);
        }
    }

    /// <summary>A layout signature whose ONLY metric term <c>MET(m)</c> names a dimension that the
    /// same signature carries as an open axis <c>m(*)</c> is the key column of an open table: the
    /// layout draws it as one more grid column, in the native model it is the key header itself
    /// (no cell), so it has no counterpart. With zero or several <c>MET</c> terms the rule does not
    /// apply.</summary>
    private static bool IsOpenAxisKeyColumnSignature(IReadOnlyList<string> normalizedTerms)
    {
        string? metMember = null;
        var metTerms = 0;
        foreach (var term in normalizedTerms)
        {
            if (PlaneCSignature.DimensionOf(term) != "MET")
            {
                continue;
            }

            metTerms++;
            metMember = PlaneCSignature.MemberOf(term);
        }

        if (metTerms != 1 || string.IsNullOrEmpty(metMember))
        {
            return false;
        }

        var sentinel = metMember + "(*)";
        return normalizedTerms.Any(t => string.Equals(t, sentinel, StringComparison.Ordinal));
    }

    /// <summary>Is there one signature (index) that has every key? The posting lists are ascending.</summary>
    private static bool HasCommonCandidate(IEnumerable<string> keys, Dictionary<string, List<int>> index)
    {
        var lists = new List<List<int>>();
        foreach (var key in keys)
        {
            if (!index.TryGetValue(key, out var candidates))
            {
                return false;
            }

            lists.Add(candidates);
        }

        if (lists.Count == 0)
        {
            return false;
        }

        lists.Sort(static (x, y) => x.Count.CompareTo(y.Count));
        var current = lists[0];
        for (var k = 1; k < lists.Count && current.Count > 0; k++)
        {
            current = Intersect(current, lists[k]);
        }

        return current.Count > 0;
    }

    private static List<int> Intersect(List<int> a, List<int> b)
    {
        var result = new List<int>(Math.Min(a.Count, b.Count));
        int i = 0, j = 0;
        while (i < a.Count && j < b.Count)
        {
            if (a[i] < b[j])
            {
                i++;
            }
            else if (a[i] > b[j])
            {
                j++;
            }
            else
            {
                result.Add(a[i]);
                i++;
                j++;
            }
        }

        return result;
    }
}
