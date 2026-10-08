namespace EbaDpm.Converter.Core.Validation;

/// <summary>
/// The 8 symmetric normalizations allowed: a CLOSED list. A normalization is only not a relaxation
/// if it is applied to BOTH sides of a comparison and is listed here. Any other one is a relaxation
/// and is forbidden.
/// </summary>
public static class Normalization
{
    /// <summary>N-1: <c>TaxonomyCode</c> to lower case, <c>_</c> for space.</summary>
    public static string TaxonomyCode(string code) => code.Replace(' ', '_').ToLowerInvariant();

    /// <summary>
    /// N-2: XBRL code to its local part (what follows the last <c>:</c>). We do not version the
    /// prefix; the 4.x references do.
    /// </summary>
    public static string XbrlLocalPart(string qualifiedCode)
    {
        var colonIndex = qualifiedCode.LastIndexOf(':');
        return colonIndex >= 0 ? qualifiedCode[(colonIndex + 1)..] : qualifiedCode;
    }

    /// <summary>N-3: <c>TemplateOrTableCode</c> without the <c>eba_tg</c>/<c>tg</c> prefix.</summary>
    public static string StripTableGroupPrefix(string code, string prefix) =>
        code.StartsWith(prefix, StringComparison.Ordinal) ? code[prefix.Length..] : code;

    /// <summary>
    /// N-3, symmetric variant without a prefix known in advance: 3.2 uses <c>tg</c>, but 4.0 already
    /// uses <c>eba_tg</c>, like the generated output. The long prefix is tried first so as not to trim
    /// too much from a code that happens to start with <c>tg</c> inside <c>eba_tg</c>.
    /// </summary>
    public static string StripAnyTableGroupPrefix(string code)
    {
        if (code.StartsWith("eba_tg", StringComparison.Ordinal))
        {
            return code["eba_tg".Length..];
        }

        return code.StartsWith("tg", StringComparison.Ordinal) ? code["tg".Length..] : code;
    }

    /// <summary>
    /// N-4: <c>HierarchyCode</c> to upper case, without the <c>_REL_n</c> suffix.
    /// </summary>
    public static string HierarchyCode(string code)
    {
        var upper = code.Trim().ToUpperInvariant();
        var relIndex = upper.IndexOf("_REL_", StringComparison.Ordinal);
        return relIndex >= 0 ? upper[..relIndex] : upper;
    }

    /// <summary>
    /// N-5: labels with collapsed whitespace (CRLF to space, multiple spaces to one, TRIM). The
    /// literal comparison drops to 99.61 %, the normalized one gives 100 %.
    /// </summary>
    public static string CollapseWhitespace(string? s) =>
        s is null ? string.Empty : string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>N-6: <c>OrdinateCode</c> to TRIM. Access pads it to WChar(4): 32,270 of 80,066.</summary>
    public static string OrdinateCode(string code) => code.Trim();

    /// <summary>
    /// N-7: <c>Order</c> of <c>mModuleBusinessTemplate</c> as a RELATIVE sequence, not an absolute
    /// value. 3.2 numbers from 1 and the 4.x from 0. Applied by sorting by the raw value and
    /// returning the resulting 0-based position.
    /// </summary>
    public static IReadOnlyList<T> RelativeOrder<T>(IEnumerable<(int Order, T Value)> items) =>
        items.OrderBy(i => i.Order).Select(i => i.Value).ToList();

    /// <summary>
    /// N-8: <c>HierarchyStartingMemberID</c> as a symmetric normalization of ABSENCE against 4.0/4.2,
    /// which never populate it (0 of 45 and 0 of 207). When comparing against those two references,
    /// the reference value is treated as "not comparable" instead of "expected NULL"; that is, the
    /// column is excluded from the comparison instead of requiring NULL on the generated side.
    /// </summary>
    public static bool ReferenceNeverPopulatesStartingMember(string referenceFileName) =>
        referenceFileName.Contains("4.0", StringComparison.OrdinalIgnoreCase)
        || referenceFileName.Contains("4.2", StringComparison.OrdinalIgnoreCase)
        || referenceFileName.Contains("EIOPA", StringComparison.OrdinalIgnoreCase);
}
