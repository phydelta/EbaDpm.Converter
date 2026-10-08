using EbaDpm.Converter.Core.Layouts.Xlsx;

namespace EbaDpm.Converter.Core.Layouts;

/// <summary>
/// Result of trying to link a <see cref="ValueRow"/> to the <see cref="OrdinateBuilder"/> —and thus
/// to the <c>OrdinateId</c> already assigned in <see cref="LayoutExtractor"/>— that represents the
/// ordinate the value belongs to.
/// </summary>
internal readonly record struct OrdinateLinkResult(int? OrdinateId, string? FailureReason)
{
    public bool IsLinked => OrdinateId is not null;

    public static OrdinateLinkResult Linked(int ordinateId) => new(ordinateId, null);

    public static OrdinateLinkResult Failed(string reason) => new(null, reason);
}

/// <summary>
/// Index <c>(Axis, OrdinateCode) -&gt; OrdinateId</c> built over the ordinates of ONE sheet
/// (the axis is determined by the dimension; within the axis, the ordinate is determined by its
/// code). <c>SheetId</c> is not part of the key because the index is built once per sheet, with the
/// ordinates of that sheet only — the sheet is already the scope.
///
/// With the current model (<see cref="LayoutSheetParser"/> creates at most ONE
/// <c>OrdinateBuilder</c> per column and one per row) the same <c>(Axis, OrdinateCode)</c> pair is
/// not repeated by construction; the <c>TryAdd</c> below is kept as a cheap safeguard for the case
/// —not observed, not ruled out— in which two columns or two rows of one sheet literally share the
/// same code (see the comment of <see cref="LayoutRepositorySchema"/> on why the database index is
/// not <c>UNIQUE</c>).
/// </summary>
internal sealed class LayoutOrdinateIndex
{
    private readonly Dictionary<(string Axis, string OrdinateCode), int> _byAxisAndCode = new();

    private LayoutOrdinateIndex()
    {
    }

    public static LayoutOrdinateIndex Build(
        IEnumerable<OrdinateBuilder> ordinates, IReadOnlyDictionary<OrdinateBuilder, int> ordinateIds)
    {
        var index = new LayoutOrdinateIndex();
        foreach (var ordinate in ordinates)
        {
            var key = (ordinate.Axis, ordinate.OrdinateCode);
            // First occurrence wins (see class comment) — a TryAdd is enough and documents the
            // rule in the code itself, not only in the comment.
            index._byAxisAndCode.TryAdd(key, ordinateIds[ordinate]);
        }

        return index;
    }

    /// <summary>
    /// Looks up the <c>OrdinateId</c> of the ordinate of this sheet whose geometric axis is
    /// <paramref name="axis"/> (<see cref="LayoutAxis.Row"/> or <see cref="LayoutAxis.Column"/>) and
    /// whose code is <paramref name="ordinateCode"/> — the <c>0010</c>/<c>0020</c> of the header,
    /// <c>mAxisOrdinate.OrdinateCode</c>. The link is ALWAYS by code: if the code does not exist on
    /// that axis of this sheet, there is no result — it never falls back to proximity or to the
    /// nearest cell.
    /// </summary>
    public bool TryGetOrdinateId(string axis, string ordinateCode, out int ordinateId)
        => _byAxisAndCode.TryGetValue((axis, ordinateCode), out ordinateId);
}

/// <summary>
/// The SINGLE POINT of value → ordinate resolution. Every <see cref="ValueRow"/> goes through here
/// before being written to <c>LayoutValue.OrdinateId</c> — see <see cref="LayoutExtractor"/>.
///
/// THE RULE: the anchor is <c>LayoutDeclaration.Region</c> AS IT IS RECORDED at the end of the
/// parsing — it does not state the axis directly, it states the DIRECTION in which the value was
/// paired with its declaration, and that direction reveals the axis:
///
/// <list type="bullet">
/// <item><b><c>Region = "Rows"</c></b> (matrix role): the value is in the SAME Excel ROW as its
/// declaration → it belongs to the <b>X</b> axis, through the ordinate whose code is that of the
/// value's COLUMN.</item>
/// <item><b><c>Region = "Columns"</c></b> (header-paired-in-the-grid role): the value is in the
/// SAME Excel COLUMN as its declaration → it belongs to the <b>Y</b> axis, through the ordinate
/// whose code is that of the value's ROW.</item>
/// <item><b><c>Region = "Header"</c></b>: the value was paired INSIDE the header block itself →
/// it is a <b>Z</b>-axis value (sheet-level) — WITH NO ordinate to link, by design, not by
/// gap.</item>
/// </list>
///
/// The link is ALWAYS by CODE, never by geometry: the position (column or row of the value) is
/// first resolved to the ordinate of THAT sheet that occupies that position
/// (<c>sheet.ColumnOrdinates</c>/<c>sheet.RowOrdinates</c>, seeded by
/// <see cref="LayoutSheetParser"/>), and THEN that code is looked up in
/// <see cref="LayoutOrdinateIndex"/> — <c>CellRef</c> is never compared against <c>CellRef</c>.
///
/// Any residue is an ordinate-extraction gap (for example entirely shaded columns whose code never
/// created a <c>LayoutOrdinate</c>) — not a failure of this rule. The reason recorded in each case
/// explicitly distinguishes "the rule does not apply" (Z axis, by design) from "the code does not
/// exist on that axis" (gap), so that no forensic query is needed to tell them apart.
/// </summary>
internal static class LayoutValueOrdinateLinker
{
    /// <summary>A Z-axis value (<c>Region = "Header"</c>) has no ordinate to link BY DESIGN — the
    /// Z axis is sheet-level and does not live in <c>LayoutOrdinate</c>. It is still recorded in
    /// <c>LayoutUnparsed</c> (no silent discards), but with a reason that cannot be mistaken for an
    /// extraction gap.</summary>
    public const string HeaderRegionReason =
        "Z axis (Region=Header): no ordinate to link by design — the link rule does not apply, it is not a gap";

    public static OrdinateLinkResult Resolve(ValueRow value, LayoutSheetResult sheet, LayoutOrdinateIndex index)
    {
        var (row, col) = CellRefUtil.Parse(value.CellRef);

        switch (value.Declaration.Region)
        {
            case "Rows":
                // Matrix role -> X axis, by the value's COLUMN (same Excel row as its declaration).
                if (!sheet.ColumnOrdinates.TryGetValue(col, out var columnOrdinate))
                {
                    return OrdinateLinkResult.Failed(
                        $"X axis: column {CellRefUtil.IndexToColumnLetter(col)} (cell {value.CellRef}) has no " +
                        "X ordinate registered in this sheet (ordinate extraction gap, not a code ambiguity)");
                }

                if (!index.TryGetOrdinateId(LayoutAxis.Column, columnOrdinate.OrdinateCode, out var xOrdinateId))
                {
                    return OrdinateLinkResult.Failed(
                        $"X axis: code '{columnOrdinate.OrdinateCode}' of column " +
                        $"{CellRefUtil.IndexToColumnLetter(col)} does not resolve in the ordinate index of this sheet");
                }

                return OrdinateLinkResult.Linked(xOrdinateId);

            case "Columns":
                // Header-paired-in-the-grid role -> Y axis, by the value's ROW (same Excel column
                // as its declaration).
                if (!sheet.RowOrdinates.TryGetValue(row, out var rowOrdinate))
                {
                    return OrdinateLinkResult.Failed(
                        $"Y axis: row {row} (cell {value.CellRef}) has no Y ordinate " +
                        "registered in this sheet");
                }

                if (!index.TryGetOrdinateId(LayoutAxis.Row, rowOrdinate.OrdinateCode, out var yOrdinateId))
                {
                    return OrdinateLinkResult.Failed(
                        $"Y axis: code '{rowOrdinate.OrdinateCode}' of row {row} does not resolve in the " +
                        "ordinate index of this sheet");
                }

                return OrdinateLinkResult.Linked(yOrdinateId);

            case "Header":
                return OrdinateLinkResult.Failed(HeaderRegionReason);

            default:
                // Defensive: exactly these three Region values have been observed. If a fourth
                // appeared, it is not guessed — it is recorded as a finding, not a silent fallback.
                return OrdinateLinkResult.Failed(
                    $"Unknown Region '{value.Declaration.Region}': the link rule only covers " +
                    "Rows/Columns/Header");
        }
    }
}
