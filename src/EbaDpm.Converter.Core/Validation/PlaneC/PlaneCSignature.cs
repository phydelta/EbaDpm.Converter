using System.Text.RegularExpressions;

namespace EbaDpm.Converter.Core.Validation.PlaneC;

/// <summary>
/// The plane C normalization. It is DIFFERENT from the 8 of <see cref="Normalization"/>: those are
/// the CLOSED list of plane B; this one belongs to plane C (against the Annotated Table Layout) and
/// is not mixed with that list.
///
/// It has to be SYMMETRIC: applied, literally, with the same code to both sides of the comparison
/// (our <c>DatapointSignature</c> and the signature rebuilt from the layout), because an
/// asymmetric normalization manufactures false findings: <c>^eba_[A-Za-z0-9]+:</c> matches
/// <c>eba_met:</c> and fails on <c>eba_met_4.0:</c> (the dot and the underscore were not in the
/// class), which produced 249 false differences. Here the class includes digits, dot and
/// underscore on purpose.
/// </summary>
public static class PlaneCSignature
{
    // "eba_dim_4.2:", "eba_met:", "eba_met_4.0:", "eba_qCS:"... - any prefix
    // "eba_<something with letters/digits/dot/underscore>:". The layout side never carries it (the
    // codes are already raw), so applying it there is a no-op, but it is applied anyway, on
    // purpose, so that the normalization is literally the SAME function on both sides.
    private static readonly Regex EbaPrefixPattern = new(@"^eba_[A-Za-z0-9_.]+:", RegexOptions.Compiled);

    // "*[new_CO1]" -> "*": the layout does not write the open axis bracket.
    private static readonly Regex OpenAxisBracketPattern = new(@"^\*\[[^\]]*\]$", RegexOptions.Compiled);

    /// <summary>Strips the <c>eba_&lt;namespace[_version]&gt;:</c> prefix from a single code (dimension or member).</summary>
    public static string StripEbaPrefix(string code) => EbaPrefixPattern.Replace(code, string.Empty);

    /// <summary>
    /// Normalizes ONE signature term, already composed as <c>dim(member)</c>, the form in which it
    /// lives both in <c>mTableCell.DatapointSignature</c> and in the signature rebuilt from the
    /// layout. Splits at the FIRST <c>'('</c> (terms do not nest parentheses: the only bracket that
    /// can appear inside is <c>[...]</c>, never <c>(...)</c>).
    /// </summary>
    public static string NormalizeTerm(string rawTerm)
    {
        var openParen = rawTerm.IndexOf('(', StringComparison.Ordinal);
        if (openParen < 0 || !rawTerm.EndsWith(')'))
        {
            // Unexpected shape: kept as is (defensive, nothing is guessed).
            return rawTerm;
        }

        var dimPart = rawTerm[..openParen];
        var memberPart = rawTerm[(openParen + 1)..^1];

        var normDim = StripEbaPrefix(dimPart);
        var normMember = StripEbaPrefix(memberPart);
        if (OpenAxisBracketPattern.IsMatch(normMember))
        {
            normMember = "*";
        }

        return normDim + "(" + normMember + ")";
    }

    /// <summary>Builds the term directly from loose dimension/member tokens (layout side, already raw).</summary>
    public static string ComposeAndNormalizeTerm(string dimensionToken, string memberToken) =>
        NormalizeTerm(dimensionToken + "(" + memberToken + ")");

    /// <summary>Splits a <c>DatapointSignature</c> (<c>"T1|T2|T3"</c>) into its normalized terms.</summary>
    public static IReadOnlyList<string> SplitAndNormalize(string dps) =>
        dps.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeTerm)
            .ToList();

    /// <summary>The dimension token (what precedes the first <c>'('</c>) of an ALREADY normalized term.</summary>
    public static string DimensionOf(string normalizedTerm)
    {
        var openParen = normalizedTerm.IndexOf('(', StringComparison.Ordinal);
        return openParen < 0 ? normalizedTerm : normalizedTerm[..openParen];
    }

    /// <summary>The member token (between parentheses) of an ALREADY normalized term; needed to
    /// recognize the key column of an open axis (<c>MET(m)</c> whose own signature carries
    /// <c>m(*)</c>).</summary>
    public static string MemberOf(string normalizedTerm)
    {
        var openParen = normalizedTerm.IndexOf('(', StringComparison.Ordinal);
        if (openParen < 0 || !normalizedTerm.EndsWith(')'))
        {
            return string.Empty;
        }

        return normalizedTerm[(openParen + 1)..^1];
    }

    /// <summary>
    /// The canonical form of a signature (a set of already normalized terms): ALL terms,
    /// including the metric, sorted ORDINALLY by the whole term, joined by <c>|</c>.
    /// It does NOT pin <c>MET(...)</c> first: although
    /// <c>Dpm20AxisAndCellLoader.ComposeCellSignature</c> does put the metric first when composing
    /// <c>mTableCell.DatapointSignature</c>, this is the canonical form of PLANE C, measured against
    /// <c>src/EbaDpm.Converter.Core/Resources/plane-c-known-divergences-4.2.tsv</c> (e.g.
    /// <c>L_06.00</c>: <c>FIH(*)|MET(ei1404)|ei1404(*)|...</c>, <c>F</c> before <c>M</c>, pure
    /// ordinal order). Since no dimension repeats within a signature (one dimension, one axis),
    /// the ordinal order of the whole term coincides with that of the dimension token, and it is
    /// irrelevant for CONTAINMENT (which compares sets, not strings); it only matters here so that
    /// the detection of an "expired exception" compares strings literally equal to those of the
    /// census.
    /// </summary>
    public static string Canonicalize(IEnumerable<string> normalizedTerms) =>
        string.Join("|", normalizedTerms.OrderBy(t => t, StringComparer.Ordinal));
}
