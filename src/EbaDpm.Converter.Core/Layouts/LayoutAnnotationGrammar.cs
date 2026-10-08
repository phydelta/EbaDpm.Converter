using System.Text.RegularExpressions;

namespace EbaDpm.Converter.Core.Layouts;

/// <summary>The annotation shape that <see cref="LayoutAnnotationGrammar.Parse"/> recognises in
/// the text of a cell.</summary>
internal enum AnnotationKind
{
    /// <summary>Empty text, or text that neither starts with <c>"("</c> nor contains
    /// <c>"&lt;Key value&gt;"</c>: there is nothing to interpret here (structural label, ordinate
    /// code, title...).</summary>
    PlainText,

    /// <summary><c>"(A) label[hierarchy]"</c> — a single code.</summary>
    SingleCode,

    /// <summary><c>"(A:B) label"</c>, where <c>B</c> may carry an inline hierarchy
    /// <c>"H(D)"</c> — two codes.</summary>
    TwoCode,

    /// <summary>The text starts with <c>"("</c> but what follows the parenthesis is not shaped like
    /// a code (rhetorical parenthesis: <c>"(-)"</c>, or empty). This is NOT a grammar failure — it is
    /// prose, and the caller must not record it in <c>LayoutUnparsed</c>.</summary>
    NotCodeShaped,
}

/// <summary>
/// Result of interpreting the text of a cell. Fields that do not apply to the detected shape are
/// left as <see langword="null"/>.
/// </summary>
internal sealed record AnnotationParse(
    AnnotationKind Kind,
    string? Code1,
    string? Code2Raw,
    string? Code2Domain,
    string? Code2Hierarchy,
    string RestText,
    bool HasKeyMarker,
    string? TrailingBracketDomain,
    string? TrailingBracketCode)
{
    public static readonly AnnotationParse Empty = new(AnnotationKind.PlainText, null, null, null, null, string.Empty, false, null, null);
}

/// <summary>
/// Pure (no I/O) parser for the annotation grammar of the EBA Annotated Table Layouts.
/// The ambiguity "(code): metric or key dimension?" is NOT resolved here — it needs the
/// dictionary of the distribution schema; see <see cref="LayoutDictionary"/> and
/// <c>LayoutSheetParser</c>.
/// </summary>
internal static class LayoutAnnotationGrammar
{
    // Header "(A)" or "(A:B)", where B may be "" (no restriction) or "H(D)" (inline hierarchy;
    // D normally repeats the domain A itself). DPM codes are alphanumeric plus "_" (this
    // includes the "_NA" sentinel); the rest of the text is free.
    private static readonly Regex HeadPattern = new(
        @"^\(([A-Za-z0-9_]+)(?::([A-Za-z0-9_]*(?:\([A-Za-z0-9_]+\))?))?\)(.*)$",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex InlineHierarchyPattern = new(
        @"^([A-Za-z0-9_]+)\(([A-Za-z0-9_]+)\)$", RegexOptions.Compiled);

    // "[domain:hierarchy]" — distinct from the "[free label]" form (that second form lives in the
    // datapoint cell, not in the annotation cell; see LayoutDatapointGrammar).
    private static readonly Regex TrailingBracketPattern = new(
        @"\[([A-Za-z0-9_]+):([A-Za-z0-9_]+)\]", RegexOptions.Compiled);

    public const string KeyValueMarker = "<Key value>";

    /// <summary>Label of the metric column since 4.0.</summary>
    public const string MainPropertyLabel = "Main Property";

    /// <summary>Label of the metric column in 3.2 — the SAME cell (<c>E4</c>), a different
    /// vocabulary: in 3.2 it is called <c>Metric</c>, replaced by <c>Main Property</c> since 4.0.
    /// Without accepting both labels, a 3.2 layout is read without a single metric and WITHOUT
    /// any error.</summary>
    public const string MetricLabel = "Metric";

    public static AnnotationParse Parse(string? text)
    {
        text = text?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return AnnotationParse.Empty;
        }

        if (!text.StartsWith('('))
        {
            return new AnnotationParse(
                AnnotationKind.PlainText, null, null, null, null, text,
                text.Contains(KeyValueMarker, StringComparison.Ordinal), null, null);
        }

        var match = HeadPattern.Match(text);
        if (!match.Success)
        {
            return new AnnotationParse(AnnotationKind.NotCodeShaped, null, null, null, null, text, false, null, null);
        }

        var code1 = match.Groups[1].Value;
        string? code2Raw = match.Groups[2].Success ? match.Groups[2].Value : null;
        var rest = match.Groups[3].Value;

        string? domain = null;
        string? hierarchy = null;
        if (code2Raw is not null)
        {
            var hierarchyMatch = InlineHierarchyPattern.Match(code2Raw);
            if (hierarchyMatch.Success)
            {
                hierarchy = hierarchyMatch.Groups[1].Value;
                domain = hierarchyMatch.Groups[2].Value;
            }
            else
            {
                domain = code2Raw;
            }
        }

        var hasKey = rest.Contains(KeyValueMarker, StringComparison.Ordinal);
        var trailingMatch = TrailingBracketPattern.Match(rest);

        var kind = code2Raw is null ? AnnotationKind.SingleCode : AnnotationKind.TwoCode;
        return new AnnotationParse(
            kind, code1, code2Raw, domain, hierarchy, rest, hasKey,
            trailingMatch.Success ? trailingMatch.Groups[1].Value : null,
            trailingMatch.Success ? trailingMatch.Groups[2].Value : null);
    }

    /// <summary>Readable label: the text remaining after the parenthesis, without the key marker
    /// or the trailing hierarchy bracket.</summary>
    public static string ExtractLabel(AnnotationParse parse)
    {
        var text = parse.RestText;
        text = text.Replace(KeyValueMarker, string.Empty);
        if (parse.TrailingBracketDomain is not null && parse.TrailingBracketCode is not null)
        {
            text = TrailingBracketPattern.Replace(text, string.Empty);
        }

        return text.Trim();
    }
}
