namespace EbaDpm.Converter.Core.Layouts.Xlsx;

/// <summary>A sheet of an <c>.xlsx</c>, already resolved to text (no formulas) and with the shading
/// of each cell: the grey that the EBA uses to disable whole columns or rows inside an enabled
/// grid, readable from <c>xl/styles.xml</c>.</summary>
internal sealed class XlsxSheet
{
    public required string Name { get; init; }

    /// <summary>Non-empty cells, indexed by 1-based (row, column). The key is the cell AS IT
    /// appears in the XML — a cell that is the anchor of a merged range has its value here; the
    /// rest of the range does NOT appear.</summary>
    public required Dictionary<(int Row, int Col), string> Cells { get; init; }

    /// <summary>Merged ranges (<c>mergeCells</c>), as (startRow,startCol,endRow,endCol).</summary>
    public required List<(int R1, int C1, int R2, int C2)> Merges { get; init; }

    /// <summary>GREY cells: the cell's <c>fillId</c>, resolved against the <c>&lt;fills&gt;</c> of
    /// THIS SAME file, with <c>fgColor</c> <c>indexed="55"</c> or <c>rgb="FFD8D8D8"</c>. It is also
    /// stored for cells WITHOUT text (a shaded column may not have a single value), so this
    /// collection is independent of <see cref="Cells"/>. The absence of a key here means "not
    /// shaded" — <see cref="IsCellShaded"/> is the single gate.</summary>
    public required HashSet<(int Row, int Col)> ShadedCells { get; init; }

    /// <summary>Cell comments: the FULL text of <c>xl/comments*.xml</c> for that sheet, indexed by
    /// (row, column), with the literal escape <c>_x000D_</c> that Excel uses for a line break INSIDE
    /// a comment already resolved to a real <c>'\n'</c> — <see cref="XlsxReader"/> is what
    /// resolves it, so that nobody downstream has to know that format detail. Empty if the sheet
    /// has no associated <c>xl/comments*.xml</c>.</summary>
    public required Dictionary<(int Row, int Col), string> Comments { get; init; }

    /// <summary>Is (row,column) shaded? Direct read, without resolving merges: the data cells
    /// that matter to <c>LayoutCell</c> are not, in the observed layouts, anchors of a merged range
    /// (if a future release changed that, this would need to be re-checked).</summary>
    public bool IsCellShaded(int row, int col) => ShadedCells.Contains((row, col));

    /// <summary>
    /// Resolves the "effective" text of (row,column): its own value if it has one, or that of the
    /// anchor of the merge it falls in, or empty if there is nothing.
    /// </summary>
    public string EffectiveText(int row, int col)
    {
        if (Cells.TryGetValue((row, col), out var direct))
        {
            return direct;
        }

        foreach (var (r1, c1, r2, c2) in Merges)
        {
            if (row >= r1 && row <= r2 && col >= c1 && col <= c2)
            {
                return Cells.GetValueOrDefault((r1, c1), string.Empty);
            }
        }

        return string.Empty;
    }

    /// <summary>Columns covered by the merge whose anchor is (row,col); only the column itself
    /// if (row,col) is not the anchor of any merge.</summary>
    public IReadOnlyList<int> MergedColumnsOf(int row, int col)
    {
        foreach (var (r1, c1, r2, c2) in Merges)
        {
            if (r1 == row && c1 == col)
            {
                return Enumerable.Range(c1, c2 - c1 + 1).ToList();
            }
        }

        return [col];
    }

    /// <summary>Last row with content, or 0 if the sheet is empty.</summary>
    public int LastRow => Cells.Count == 0 ? 0 : Cells.Keys.Max(k => k.Row);
}

internal sealed class XlsxWorkbook
{
    public required IReadOnlyList<XlsxSheet> Sheets { get; init; }
}
