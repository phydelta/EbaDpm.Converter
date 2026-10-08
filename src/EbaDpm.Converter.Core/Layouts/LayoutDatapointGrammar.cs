using System.Text.RegularExpressions;

namespace EbaDpm.Converter.Core.Layouts;

/// <summary>
/// The "datapoint identifier + data type" cell: digits, the literal separator
/// <c>_x000D_</c> (an Excel line break badly re-exported, not a DPM annotation) and the
/// type. The type is a literal (<c>text</c>, <c>positive</c>, <c>TRUE/FALSE</c>, <c>€£$</c>) or,
/// when the data comes from an enumerated domain, a <c>[free label]</c> — and it may carry both
/// on successive lines. It is not resolved semantically: it is kept as is.
/// </summary>
internal static class LayoutDatapointGrammar
{
    private static readonly Regex Pattern = new(
        @"^(\d+)_x000D_[\r\n]*(.*)$", RegexOptions.Singleline | RegexOptions.Compiled);

    public static bool TryParse(string text, out string datapointId, out string? dataType)
    {
        var match = Pattern.Match(text.Trim());
        if (!match.Success)
        {
            datapointId = string.Empty;
            dataType = null;
            return false;
        }

        datapointId = match.Groups[1].Value;
        var rest = match.Groups[2].Value.Replace("_x000D_", "\n").Trim();
        dataType = rest.Length == 0 ? null : rest;
        return true;
    }

    /// <summary>Recognises the shape without decomposing it (for the <c>LayoutUnparsed</c> gate:
    /// this cell is not a grammar failure even though it contains "[").</summary>
    public static bool Matches(string text) => Pattern.IsMatch(text.Trim());
}
