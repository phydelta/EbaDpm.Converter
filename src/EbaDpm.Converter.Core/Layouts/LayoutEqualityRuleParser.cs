using System.Text.RegularExpressions;

namespace EbaDpm.Converter.Core.Layouts;

/// <summary>An already structured term of an equality rule:
/// <c>{table, r&lt;row&gt;, c &lt;column&gt;[, s&lt;sheet&gt;]}</c> -- the "BusinessCode with
/// different punctuation" form that the EBA uses in its cell comments to state that two cells of
/// DIFFERENT tables carry the same datapoint signature.</summary>
internal readonly record struct EqualityRuleTerm(string TableCode, string RowCode, string ColumnCode, string? ZCode)
{
    /// <summary>The canonical form of THIS term, rebuilt from its fields (not the raw text):
    /// it normalises stray spaces ("c0200" and "c 0200" are the same term) without losing any of
    /// the four pieces.</summary>
    public string Canonical => ZCode is null
        ? $"{TableCode}, r{RowCode}, c {ColumnCode}"
        : $"{TableCode}, r{RowCode}, c {ColumnCode}, s{ZCode}";
}

/// <summary>
/// Extracts <c>==</c> equality rules from an ALREADY resolved cell-comment text:
/// <c>_x000D_</c> converted to a real line break by <see cref="Xlsx.XlsxReader"/>. A comment
/// may carry several lines (KeyVariableID, "Value chosen from:"...) and ONLY those containing
/// <c>==</c> count -- the others are prose or the prose restriction of an open axis, and neither
/// is an equality rule.
///
/// The operator is the ONLY one across all the releases (3.2/4.0/4.2/4.3): <c>==</c>, chaining 2
/// or more <c>{...}</c> terms. A closed form of "2 terms" is not assumed: a chain
/// <c>A==B==C</c> is ONE rule of 3 terms, not two rules of 2.
/// </summary>
internal static class LayoutEqualityRuleParser
{
    // The content of a term: each "{...}" group is captured raw, so that rules can be
    // deduplicated by the TEXT as is before trying to structure it.
    private static readonly Regex BraceGroupPattern = new(@"\{([^{}]+)\}", RegexOptions.Compiled);

    // The structured form of a term without braces: "table, r<row>, c <column>[, s<Z>]".
    // Tolerates an optional space after 'r'/'c'/'s' ("r0090" without a space and "c 0200" with
    // one coexist in the same comment).
    private static readonly Regex TermPattern = new(
        @"^\s*([^,]+?)\s*,\s*r\s*(\S+)\s*,\s*c\s*(\S+)\s*(?:,\s*s\s*(\S+)\s*)?$",
        RegexOptions.Compiled);

    /// <summary>A comment line that contains <c>==</c>, with its RAW terms (the content between
    /// braces, as is, not yet structured) -- the unit that is deduplicated
    /// (<see cref="CanonicalKey"/> sorts these texts, not already structured rules).</summary>
    public readonly record struct RawRuleLine(string Line, IReadOnlyList<string> RawTerms);

    /// <summary>The lines of <paramref name="commentText"/> that contain <c>==</c>, each with the
    /// raw terms it chains (2 or more; fewer than 2 is not a rule and is discarded here
    /// -- a "one-term rule" is not possible with this operator).</summary>
    public static IEnumerable<RawRuleLine> ExtractRawLines(string commentText)
    {
        foreach (var rawLine in commentText.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || !line.Contains("==", StringComparison.Ordinal))
            {
                continue;
            }

            var terms = BraceGroupPattern.Matches(line)
                .Select(m => m.Groups[1].Value.Trim())
                .ToList();

            if (terms.Count >= 2)
            {
                yield return new RawRuleLine(line, terms);
            }
        }
    }

    /// <summary>The deduplication key of a rule: the alphabetically SORTED set of its raw terms,
    /// joined by <c>|</c>. The EBA annotates the SAME rule, in full, in the comment of EACH
    /// participating cell -- sometimes with the terms in a different order depending on which cell
    /// it is read from -- so deduplication has to be by SET, not by literal text.</summary>
    public static string CanonicalKey(IReadOnlyList<string> rawTerms) =>
        string.Join("|", rawTerms.OrderBy(t => t, StringComparer.Ordinal));

    /// <summary>Structures a raw term (the content between braces) into its four fields, or
    /// <see langword="null"/> if it did not follow the grammar (grammar failure -- it is counted in
    /// <c>LayoutUnparsed</c>, never silently discarded).</summary>
    public static EqualityRuleTerm? Parse(string rawTerm)
    {
        var match = TermPattern.Match(rawTerm);
        if (!match.Success)
        {
            return null;
        }

        // Same reason as LayoutSheetNameParser — 3.2 annotates these comments with the table
        // code WITH a space ("F 32.01"), and this term is compared against mTable.TableCode
        // (with an underscore) in PlaneCEqualityChecker.
        return new EqualityRuleTerm(
            LayoutTableCodeNormalizer.Canonicalize(match.Groups[1].Value.Trim()),
            match.Groups[2].Value.Trim(),
            match.Groups[3].Value.Trim(),
            match.Groups[4].Success ? match.Groups[4].Value.Trim() : null);
    }
}
