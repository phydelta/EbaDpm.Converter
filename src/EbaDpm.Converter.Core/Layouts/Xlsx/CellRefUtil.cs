using System.Text;

namespace EbaDpm.Converter.Core.Layouts.Xlsx;

/// <summary>
/// Conversion between an Excel cell reference (<c>"D12"</c>) and 1-based (row, column)
/// coordinates — a purely mechanical utility, with no knowledge of the DPM grammar.
/// </summary>
internal static class CellRefUtil
{
    /// <summary>Splits a reference such as <c>"AI12"</c> into (row=12, column=35).</summary>
    public static (int Row, int Col) Parse(string cellRef)
    {
        var i = 0;
        while (i < cellRef.Length && char.IsLetter(cellRef[i]))
        {
            i++;
        }

        var colPart = cellRef[..i];
        var rowPart = cellRef[i..];
        return (int.Parse(rowPart), ColumnLetterToIndex(colPart));
    }

    public static int ColumnLetterToIndex(string letters)
    {
        var col = 0;
        foreach (var c in letters)
        {
            col = col * 26 + (char.ToUpperInvariant(c) - 'A' + 1);
        }

        return col;
    }

    public static string IndexToColumnLetter(int col)
    {
        var sb = new StringBuilder();
        while (col > 0)
        {
            var rem = (col - 1) % 26;
            sb.Insert(0, (char)('A' + rem));
            col = (col - 1) / 26;
        }

        return sb.ToString();
    }

    public static string ToCellRef(int row, int col) => IndexToColumnLetter(col) + row.ToString();
}
