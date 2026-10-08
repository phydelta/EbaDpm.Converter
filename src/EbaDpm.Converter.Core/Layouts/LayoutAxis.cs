namespace EbaDpm.Converter.Core.Layouts;

/// <summary>
/// The two GEOMETRIC axis labels used by <see cref="LayoutOrdinate"/>/<see cref="OrdinateBuilder"/>:
/// <see cref="Row"/> for an ordinate read under the <c>"Rows"</c> marker of the grid,
/// <see cref="Column"/> for one read in the header or in the per-column data row.
///
/// This is a label of POSITION in the Excel sheet — "read going down the rows" / "read across the
/// columns" —, NOT a statement about which of the two table axes is the OPEN one. The open axis is
/// normally Y, but X may be open in the future: labelling an ordinate <see cref="Row"/> says
/// nothing about whether that axis is enumerated or open — that is a different question, answered
/// by <c>LayoutDeclaration.IsKey</c>, not by this field. The values are centralised here so that
/// the literals <c>"Y"</c>/<c>"X"</c> are not scattered through <c>LayoutSheetParser</c> and
/// <c>LayoutExtractor</c>, where they would look like a business decision about which axis is open.
/// <see cref="Row"/> is <c>"Y"</c> and <see cref="Column"/> is <c>"X"</c>.
/// </summary>
internal static class LayoutAxis
{
    /// <summary>Ordinate read in a grid row (under the <c>"Rows"</c> marker).</summary>
    public const string Row = "Y";

    /// <summary>Ordinate read in a column (header or data row, column code).</summary>
    public const string Column = "X";
}
