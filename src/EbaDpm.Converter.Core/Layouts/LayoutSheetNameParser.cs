using System.Text.RegularExpressions;

namespace EbaDpm.Converter.Core.Layouts;

/// <summary>
/// Splits a sheet name into <c>TableCode</c> and, if present, the parenthesised suffix that
/// materialises a combination of the Z axis: <c>"C_14.01(0010)"</c> →
/// (<c>"C_14.01"</c>, <c>"0010"</c>); <c>"C_02.00.a"</c> → (<c>"C_02.00.a"</c>, <see langword="null"/>).
/// </summary>
internal static class LayoutSheetNameParser
{
    private static readonly Regex Pattern = new(@"^(.+)\(([^()]+)\)$", RegexOptions.Compiled);

    public static (string TableCode, string? ZSuffix) Parse(string sheetName)
    {
        var match = Pattern.Match(sheetName);
        var rawTableCode = match.Success ? match.Groups[1].Value : sheetName;

        // Canonicalise HERE, on extraction — 3.2 writes the code with a space ("C 01.00"), the
        // target with an underscore ("C_01.00"). The raw literal is not lost: it lives in
        // LayoutSheet.SheetName (sheetName, untouched), which is inserted separately.
        var tableCode = LayoutTableCodeNormalizer.Canonicalize(rawTableCode);
        return match.Success ? (tableCode, match.Groups[2].Value) : (tableCode, null);
    }

    /// <summary>The table label from <c>A1</c> ("<c>C_14.01 - (CR SEC Details) Detailed
    /// information...</c>"): everything after the first " - ".</summary>
    public static string? ExtractTableLabel(string? titleCellText)
    {
        if (string.IsNullOrEmpty(titleCellText))
        {
            return null;
        }

        var separatorIndex = titleCellText.IndexOf(" - ", StringComparison.Ordinal);
        return separatorIndex < 0 ? null : titleCellText[(separatorIndex + 3)..].Trim();
    }
}
