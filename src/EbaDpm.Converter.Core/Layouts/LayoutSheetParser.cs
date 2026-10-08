using System.Text.RegularExpressions;
using EbaDpm.Converter.Core.Layouts.Xlsx;

namespace EbaDpm.Converter.Core.Layouts;

/// <summary>The kinds of <c>LayoutUnparsed</c> rows: a free-text <c>Reason</c> is not enough to
/// distinguish them mechanically without a forensic query.</summary>
internal static class UnparsedKind
{
    /// <summary>A cell in annotation POSITION (starts with <c>"("</c>, or contains the marker
    /// <c>"&lt;Key value&gt;"</c>, after at most the datapoint prefix) that does not parse in any
    /// of the three shapes — broken syntax, not rhetorical prose.</summary>
    public const string GrammarFailure = "GrammarFailure";

    /// <summary>A value (one- or two-code shape) with no declaration pairing it, or paired by
    /// position but whose code does not really exist in the dictionary.</summary>
    public const string OrphanValue = "OrphanValue";

    /// <summary>A declaration with no associated <c>LayoutValue</c>. It does not imply a loss by
    /// itself (an open axis declares the key without fixing a value) — it is recorded so that
    /// telling it apart from the other kinds does not require a forensic query.</summary>
    public const string OrphanDeclaration = "OrphanDeclaration";

    /// <summary>A well-formed <c>LayoutValue</c> (known dimension and member) to which
    /// <see cref="LayoutValueOrdinateLinker"/> could NOT assign an <c>OrdinateId</c>. No value is
    /// silently discarded — every one that does not link is counted here, with the failure reason in
    /// <c>Reason</c>. It is not interchangeable with <see cref="OrphanValue"/>: that one is a
    /// GRAMMAR/dictionary failure (the code does not exist or does not pair); this one is a LINK
    /// failure (the code exists as a value, but it was not resolved which ordinate of the sheet it
    /// belongs to).</summary>
    public const string UnlinkedValue = "UnlinkedValue";

    /// <summary>A comment line that contains <c>==</c> but whose terms do not follow the
    /// <c>{table, r&lt;row&gt;, c &lt;column&gt;[, s&lt;Z&gt;]}</c> grammar — a GRAMMAR failure of
    /// the equality rule, distinct from the four above (those belong to the grid; this one to the
    /// comment). It has not been seen in the 4.2 layouts, but it is not assumed — it is counted if
    /// it appears.</summary>
    public const string EqualityRuleGrammarFailure = "EqualityRuleGrammarFailure";
}

/// <summary>A declaration under construction. Mutable because a later cell of the same row or
/// column may complete it (the key marker, the hierarchy).</summary>
internal sealed class DeclarationBuilder
{
    public required string Region { get; set; } // Header | Rows | Columns
    public required string CellRef { get; init; }
    public string? DimensionCode { get; set; }
    public string? DomainCode { get; set; }
    public string? HierarchyCode { get; set; }
    public string? Label { get; set; }
    public bool IsKey { get; set; }
}

/// <summary>
/// An ordinate: it lives on its AXIS, identified by <c>(SheetId, Axis, OrdinateCode)</c>, with its
/// label. Nothing that is a property of a CELL (which concrete row/column a datapoint corresponds
/// to, its <c>DatapointId</c>, its type, whether it is shaded) lives here — that is
/// <see cref="CellBuilder"/>/<c>LayoutCell</c>, the cartesian product of two ordinates. There is
/// EXACTLY one row per pair, by construction (see <see cref="LayoutSheetParser"/>: the X axis is
/// created from the complete code row, deduplicated by column; the Y axis, once per grid row).
/// </summary>
internal sealed class OrdinateBuilder
{
    /// <summary>GEOMETRIC reading axis: <see cref="LayoutAxis.Row"/> or
    /// <see cref="LayoutAxis.Column"/> — see the comment of that class: it is not a statement about
    /// which table axis is the OPEN one, only where the code was read.</summary>
    public required string Axis { get; init; }
    public required string OrdinateCode { get; init; }

    /// <summary><see langword="true"/> when <see cref="OrdinateCode"/> is the LAST-RESORT column
    /// letter, not the real DPM code from the code row. A letter must not be indistinguishable from
    /// a real code.</summary>
    public bool IsFallbackCode { get; init; }

    public string? Label { get; set; }

    /// <summary>
    /// The GEOMETRIC position of this ordinate in the source Excel — the row, for
    /// <see cref="LayoutAxis.Row"/>; the column, for <see cref="LayoutAxis.Column"/>. It is NEVER
    /// persisted in <c>LayoutOrdinate</c> (the ordinate is identified by code, not by position) —
    /// it lives only in memory, during parsing, so that the cartesian product of
    /// <see cref="CellBuilder"/> can be GENERATED at the end of <see cref="LayoutSheetParser.Parse"/>
    /// without searching again for where each ordinate is in the sheet.
    /// </summary>
    public required int Position { get; init; }
}

/// <summary>
/// A grid CELL: the cartesian product of a row ordinate and a column ordinate, with
/// <c>CellRef</c>, <c>DatapointId</c>, <c>DataType</c> and <see cref="IsShaded"/>.
///
/// The cell is GENERATED, not searched for. Registering a <c>CellBuilder</c> only when the parsing
/// found a datapoint identifier at that position would never yield a shaded cell (a shaded cell by
/// definition NEVER carries one), so <see cref="IsShaded"/> would always be <c>false</c>: a field
/// that cannot be <c>true</c> is not a result. <see cref="LayoutSheetParser.Parse"/> therefore
/// emits, for each sheet with closed axes, THE COMPLETE row x column product — <c>DatapointId</c>
/// is <see langword="null"/> where there is no data (shaded or not), and <see cref="IsShaded"/> is
/// read from the Excel regardless of whether there is data.
/// </summary>
internal sealed class CellBuilder
{
    /// <summary>The <c>Y</c> ordinate of this row, if the sheet has any. <see langword="null"/>
    /// only when the sheet has NO <c>Y</c> ordinate at all (open row axis) — the row is supplied
    /// by the reporting party and there is nothing to reference; it is not a failure.</summary>
    public OrdinateBuilder? RowOrdinate { get; init; }

    public required OrdinateBuilder ColumnOrdinate { get; init; }
    public required string CellRef { get; init; }

    /// <summary>
    /// <see langword="null"/> when this position of the cartesian product does NOT carry a
    /// datapoint identifier — shaded or not: the absence of data and the shading are two
    /// INDEPENDENT measures of the same cell, not the same thing (see <see cref="IsShaded"/>).
    /// </summary>
    public string? DatapointId { get; init; }

    public string? DataType { get; init; }

    /// <summary>GREY (shaded, disabled) cell in the Excel itself. A property of the cell, not of
    /// the ordinate: a column may be shaded from top to bottom in the visible part and its column
    /// code still be a legitimate ordinate.</summary>
    public required bool IsShaded { get; init; }
}

internal sealed class ValueRow
{
    public required DeclarationBuilder Declaration { get; init; }
    public string? DomainCode { get; init; }
    public string? MemberCode { get; init; }
    public string? Label { get; init; }
    public required string CellRef { get; init; }
}

internal sealed class UnparsedRow
{
    public required string CellRef { get; init; }
    public required string Value { get; init; }
    public required string Kind { get; init; }
    public required string Reason { get; init; }
}

internal sealed class LayoutSheetResult
{
    public List<DeclarationBuilder> Declarations { get; } = [];

    /// <summary>The ordinates of the sheet, ALREADY deduplicated by <c>(Axis, OrdinateCode)</c> —
    /// exactly once each (see <see cref="OrdinateBuilder"/>).</summary>
    public List<OrdinateBuilder> Ordinates { get; } = [];

    /// <summary>The cells of the grid: in closed-axis sheets, ONE for every row x column
    /// intersection of the complete cartesian product — with or without a datapoint, shaded or
    /// not (the cell is GENERATED, not searched for where there is data). In open-axis sheets (with
    /// no fixed set of ordinates to cross) the single cell per position actually seen during parsing
    /// is kept. See <see cref="CellBuilder"/> and <see cref="LayoutSheetParser.Parse"/>.</summary>
    public List<CellBuilder> Cells { get; } = [];

    public List<ValueRow> Values { get; } = [];
    public List<UnparsedRow> Unparsed { get; } = [];
    public Dictionary<(int Row, int Col), string> Raw { get; } = [];

    /// <summary>The <c>Y</c> ordinate (row axis) of each grid row that has one, by Excel row
    /// number — exposed so that <see cref="LayoutValueOrdinateLinker"/> can resolve the link
    /// without re-parsing the sheet ("Columns" link → same row → this dictionary).</summary>
    public Dictionary<int, OrdinateBuilder> RowOrdinates { get; } = [];

    /// <summary>The <c>X</c> ordinate (column axis) of each column of the sheet that has one, by
    /// Excel column number — exposed for <see cref="LayoutValueOrdinateLinker"/> ("Rows" link →
    /// same column → this dictionary). The key set of this dictionary is THAT OF THE CODE ROW of the
    /// sheet (plus, where applicable, fallback columns where there is data without a code) — NOT
    /// that of the columns that have data: an entirely shaded column still appears here if its code
    /// is in the code row.</summary>
    public Dictionary<int, OrdinateBuilder> ColumnOrdinates { get; } = [];

    /// <summary>How many row-ordinate (Y) rows had a third column that did NOT have the shape of
    /// "datapoint identifier + type" — the <c>DatapointId</c> is left <see langword="null"/>, but it
    /// is counted instead of passing silently.</summary>
    public int DatapointSlotMismatches { get; set; }
}

/// <summary>
/// The extractor of one sheet: converts the grid of cells into declarations/values/ordinates.
/// Two pairing mechanisms coexist in the SAME sheet:
///
/// <list type="bullet">
/// <item><b>Matrix (role B)</b>: inside the grid (the "Rows" marker row in column A and those
/// that follow), the leftmost cell of a row declares a dimension and the other cells of that SAME
/// row carry its value — pairing by ROW.</item>
/// <item><b>Header (role A)</b>: above the marker row, a declaration fixes a column; its value is
/// in that SAME column, either in the next row of the header itself (enumerated Z axis) or further
/// down, inside the grid — pairing by COLUMN.</item>
/// </list>
///
/// The grid rows are processed BEFORE the header rows so that their values are <c>claimed</c>
/// first: two declarations of compatible shape may fall on the same column (one matrix, one
/// header) and only one is the right one.
///
/// A single-term code in the <c>Main Property</c> row is NOT always a metric — it may be the
/// typical dimension of the key column of an open axis. This is decided by consulting
/// <see cref="LayoutDictionary"/>, never by the shape of the text. And the two-code form
/// "(domain:member)" is not accepted merely for being well paired by position either — it is also
/// validated against the dictionary, because a correct position does not prove that the code
/// exists.
/// </summary>
internal static class LayoutSheetParser
{
    private static readonly Regex ShortCodePattern = new(@"^\d{2,6}$", RegexOptions.Compiled);
    private static readonly Regex DatapointIdPrefixPattern = new(@"^\d+_x000D_[\r\n]*", RegexOptions.Compiled);

    public static LayoutSheetResult Parse(XlsxSheet sheet, LayoutDictionary dictionary)
    {
        var result = new LayoutSheetResult();
        foreach (var (coords, text) in sheet.Cells)
        {
            result.Raw[coords] = text;
        }

        var claimed = new HashSet<(int Row, int Col)>();

        var markerRow = sheet.Cells
            .Where(kv => kv.Key.Col == 1 && string.Equals(kv.Value.Trim(), "Rows", StringComparison.Ordinal))
            .Select(kv => kv.Key.Row)
            .DefaultIfEmpty(int.MaxValue)
            .Min();

        // The real DPM code of each X-axis column lives in ONE row per sheet — the last header
        // row, right above where the grid starts ("Rows") — and applies to ALL the data rows of
        // that column, not just the first. In every real sheet with X-axis ordinates, the row
        // that resolves a short code (2-6 digits) is ALWAYS exactly markerRow-1, and it is the
        // SAME row for all the columns of the sheet. Looking at "the cell right above" row by row
        // only works for the first data row: from the second on, the cell above is another
        // datapoint, not a code.
        var columnCodeMap = BuildColumnCodeMap(sheet, markerRow);

        // The SET of ordinates of an axis is that of its code row (column) — not that of the
        // cells with content. One X ordinate is created HERE, at once, for each code of
        // columnCodeMap, BEFORE looking at a single datum: an entirely shaded column (with no
        // "visible" data cell) is still registered, because its code is in the row and that is
        // enough. What is added further down per column via GetOrCreateColumnOrdinate are only the
        // FALLBACK ones (without a real code) — it never duplicates these.
        foreach (var (col, code) in columnCodeMap)
        {
            var columnOrdinate = new OrdinateBuilder { Axis = LayoutAxis.Column, OrdinateCode = code, IsFallbackCode = false, Position = col };
            result.ColumnOrdinates[col] = columnOrdinate;
            result.Ordinates.Add(columnOrdinate);
        }

        // The explicit row<->column key. A row ordinate (Y) is created once per data row, in
        // ParseGridRow — it is registered here, by row, so that any cell of that SAME row (created
        // in that same step, or later by the header or by the safety net) can reference its Y
        // ordinate without re-parsing text. Empty in open-row-axis sheets: there is no Y to
        // reference, and that absence IS the answer, not a failure — see RegisterCell.
        var yOrdinateByRow = result.RowOrdinates;
        var columnOrdinates = result.ColumnOrdinates;

        // While parsing, every datapoint FOUND is noted here — it is the only source of
        // LayoutCell for open-axis sheets (see the end of this method), where there is no fixed
        // set of ordinates to generate the cartesian product from. In closed-axis sheets this list
        // is discarded: the complete cartesian product replaces it by construction.
        var pendingCells = new List<CellBuilder>();

        var rows = sheet.Cells.Keys.Select(k => k.Row).Distinct().OrderBy(r => r).ToList();

        // Grid first (matrix declarations and ordinates), so that its values are claimed before
        // the header tries to claim the same cells by column.
        foreach (var row in rows.Where(r => r >= markerRow))
        {
            ParseGridRow(sheet, row, columnOrdinates, yOrdinateByRow, pendingCells, dictionary, result, claimed);
        }

        foreach (var row in rows.Where(r => r < markerRow))
        {
            ParseHeaderRow(sheet, row, markerRow, columnOrdinates, yOrdinateByRow, pendingCells, dictionary, result, claimed);
        }

        // Safety net: any cell that no previous mechanism has resolved.
        foreach (var (coords, text) in sheet.Cells)
        {
            if (!claimed.Contains(coords))
            {
                EvaluateStandaloneCell(sheet, columnOrdinates, yOrdinateByRow, pendingCells, text, coords.Row, coords.Col, dictionary, result, claimed);
            }
        }

        // The other direction of the orphan. A declaration with no associated LayoutValue is not
        // lost (it stays in LayoutDeclaration), but there would be no mechanical way to find it
        // without joining against LayoutValue by hand — it is recorded too.
        var declaredWithValue = result.Values.Select(v => v.Declaration).ToHashSet();
        foreach (var decl in result.Declarations)
        {
            if (declaredWithValue.Contains(decl))
            {
                continue;
            }

            var (r, c) = CellRefUtil.Parse(decl.CellRef);
            var rawText = result.Raw.GetValueOrDefault((r, c), decl.Label ?? string.Empty);
            result.Unparsed.Add(new UnparsedRow
            {
                CellRef = decl.CellRef,
                Value = rawText,
                Kind = UnparsedKind.OrphanDeclaration,
                Reason = decl.IsKey
                    ? "open-axis declaration (IsKey) with no fixed value: correct by design, the reporting party supplies it"
                    : "declaration with no associated value in this sheet (key column of an open " +
                      "axis, or another case to review — it does not imply a loss by itself)",
            });
        }

        PopulateCells(sheet, result, pendingCells);

        return result;
    }

    /// <summary>
    /// Cells are not searched for, they are GENERATED. With both axes of the sheet closed
    /// (<c>RowOrdinates</c> AND <c>ColumnOrdinates</c> non-empty), <c>result.Cells</c> is the
    /// COMPLETE cartesian product — one row for each combination, whether or not there is data
    /// there, shaded or not — instead of only the positions where the parsing found a datapoint
    /// identifier. It is the only way for <see cref="CellBuilder.IsShaded"/> to ever be
    /// <see langword="true"/>: a shaded cell, by definition, carries no datapoint, so registering
    /// only what was found always excludes it.
    ///
    /// With any axis OPEN (there is no fixed set of ordinates for that axis — the reporting party
    /// supplies it) there is no product to generate: <paramref name="pendingCells"/> is kept, the
    /// single cell per position that the parsing really found.
    /// </summary>
    private static void PopulateCells(XlsxSheet sheet, LayoutSheetResult result, List<CellBuilder> pendingCells)
    {
        if (result.RowOrdinates.Count == 0 || result.ColumnOrdinates.Count == 0)
        {
            result.Cells.AddRange(pendingCells);
            return;
        }

        foreach (var rowOrdinate in result.RowOrdinates.Values)
        {
            foreach (var columnOrdinate in result.ColumnOrdinates.Values)
            {
                var row = rowOrdinate.Position;
                var col = columnOrdinate.Position;

                string? datapointId = null;
                string? dataType = null;
                if (sheet.Cells.TryGetValue((row, col), out var text) &&
                    LayoutDatapointGrammar.TryParse(text, out var parsedId, out var parsedType))
                {
                    datapointId = parsedId;
                    dataType = parsedType;
                }

                result.Cells.Add(new CellBuilder
                {
                    RowOrdinate = rowOrdinate,
                    ColumnOrdinate = columnOrdinate,
                    CellRef = CellRefUtil.ToCellRef(row, col),
                    DatapointId = datapointId,
                    DataType = dataType,
                    IsShaded = sheet.IsCellShaded(row, col),
                });
            }
        }
    }

    /// <summary>Grid row ("Rows" onwards): matrix declaration or ordinate. The two forms are
    /// mutually exclusive by construction: the first is only entered if the leftmost cell is shaped
    /// like a code.</summary>
    private static void ParseGridRow(
        XlsxSheet sheet, int row, Dictionary<int, OrdinateBuilder> columnOrdinates,
        Dictionary<int, OrdinateBuilder> yOrdinateByRow, List<CellBuilder> pendingCells, LayoutDictionary dictionary,
        LayoutSheetResult result, HashSet<(int, int)> claimed)
    {
        var cols = ColumnsOf(sheet, row);
        if (cols.Count == 0)
        {
            return;
        }

        var firstCol = cols[0];
        var firstText = sheet.Cells[(row, firstCol)];
        var firstParse = LayoutAnnotationGrammar.Parse(firstText);
        var isMainProperty = IsMainProperty(firstText);

        if (firstParse.Kind == AnnotationKind.TwoCode || isMainProperty)
        {
            var decl = isMainProperty
                ? new DeclarationBuilder { Region = "Rows", CellRef = CellRefUtil.ToCellRef(row, firstCol), DomainCode = "MET" }
                : BuildTwoCodeDeclaration(firstParse, "Rows", CellRefUtil.ToCellRef(row, firstCol));
            result.Declarations.Add(decl);
            claimed.Add((row, firstCol));

            foreach (var col in cols.Skip(1))
            {
                var text = sheet.Cells[(row, col)];
                if (!TryPairValue(text, row, col, decl, isMainProperty, dictionary, result, claimed, "Rows"))
                {
                    EvaluateStandaloneCell(sheet, columnOrdinates, yOrdinateByRow, pendingCells, text, row, col, dictionary, result, claimed);
                }
            }

            return;
        }

        // Not a declaration: a row ordinate (Y)? Short code right after the label.
        if (cols.Count >= 2 && ShortCodePattern.IsMatch(sheet.Cells[(row, cols[1])].Trim()))
        {
            var codeCol = cols[1];
            var ordinate = new OrdinateBuilder
            {
                Axis = LayoutAxis.Row,
                OrdinateCode = sheet.Cells[(row, codeCol)].Trim(),
                Label = firstText,
                Position = row,
            };
            claimed.Add((row, firstCol));
            claimed.Add((row, codeCol));
            yOrdinateByRow[row] = ordinate;

            if (cols.Count >= 3)
            {
                var dpCol = cols[2];
                var dpText = sheet.Cells[(row, dpCol)];
                if (LayoutDatapointGrammar.TryParse(dpText, out var dpId, out var dpType))
                {
                    claimed.Add((row, dpCol));

                    // The datapoint is a property of the CELL (row x column), not of the row
                    // identity NOR of the column ordinate — it is noted as a sighting
                    // (pendingCells), with the explicit reference to its row (RowOrdinate). If the
                    // sheet has both axes closed, PopulateCells discards it and regenerates this
                    // same cell —and all the others— from the cartesian product; if any axis is
                    // open, this sighting IS the cell.
                    RegisterCell(sheet, columnOrdinates, ordinate, row, dpCol, dpId, dpType, pendingCells, result);
                }
                else
                {
                    // The cell at cols[2] is not shaped like a datapoint (e.g. there is a gap in
                    // the grid and cols[2] is actually the next header annotation) — it is not
                    // claimed, so whoever corresponds resolves it later, but it IS COUNTED.
                    result.DatapointSlotMismatches++;
                }
            }

            result.Ordinates.Add(ordinate);
            // The remaining columns of this row are header values (role A): ParseHeaderRow
            // resolves them by column, or if nothing claims them, the final safety net.
            return;
        }

        // Neither declaration nor ordinate: structural row (group title...). Each cell is
        // evaluated on its own, in case it has the shape of a loose annotation after all.
        foreach (var col in cols)
        {
            EvaluateStandaloneCell(sheet, columnOrdinates, yOrdinateByRow, pendingCells, sheet.Cells[(row, col)], row, col, dictionary, result, claimed);
        }
    }

    /// <summary>Header row (before "Rows"): each cell shaped like a declaration fixes a column and
    /// looks for its value first in the rest of the header (Z axis) and, failing that, inside the
    /// grid (column dimension, the <c>Main Property</c> case).</summary>
    private static void ParseHeaderRow(
        XlsxSheet sheet, int row, int markerRow, Dictionary<int, OrdinateBuilder> columnOrdinates,
        Dictionary<int, OrdinateBuilder> yOrdinateByRow, List<CellBuilder> pendingCells, LayoutDictionary dictionary,
        LayoutSheetResult result, HashSet<(int, int)> claimed)
    {
        foreach (var col in ColumnsOf(sheet, row))
        {
            if (claimed.Contains((row, col)))
            {
                continue;
            }

            var text = sheet.Cells[(row, col)];
            if (LayoutDatapointGrammar.Matches(text))
            {
                RegisterDatapointOrdinate(sheet, columnOrdinates, yOrdinateByRow, pendingCells, row, col, text, result, claimed);
                continue;
            }

            var parse = LayoutAnnotationGrammar.Parse(text);
            var isMainProperty = IsMainProperty(text);

            if (parse.Kind == AnnotationKind.TwoCode || isMainProperty)
            {
                var decl = isMainProperty
                    ? new DeclarationBuilder { Region = "Columns", CellRef = CellRefUtil.ToCellRef(row, col), DomainCode = "MET" }
                    : BuildTwoCodeDeclaration(parse, "Columns", CellRefUtil.ToCellRef(row, col));
                claimed.Add((row, col));

                var mergedCols = sheet.MergedColumnsOf(row, col);
                var pairedInHeader = false;

                for (var r2 = row + 1; r2 < markerRow && !pairedInHeader; r2++)
                {
                    foreach (var c2 in mergedCols)
                    {
                        if (claimed.Contains((r2, c2)) || !sheet.Cells.TryGetValue((r2, c2), out var valueText))
                        {
                            continue;
                        }

                        if (TryPairValue(valueText, r2, c2, decl, isMainProperty, dictionary, result, claimed, "Header"))
                        {
                            decl.Region = "Header";
                            pairedInHeader = true;
                            break;
                        }
                    }
                }

                if (!pairedInHeader)
                {
                    for (var r2 = markerRow; r2 <= sheet.LastRow; r2++)
                    {
                        foreach (var c2 in mergedCols)
                        {
                            if (claimed.Contains((r2, c2)) || !sheet.Cells.TryGetValue((r2, c2), out var valueText))
                            {
                                continue;
                            }

                            TryPairValue(valueText, r2, c2, decl, isMainProperty, dictionary, result, claimed, "Columns");
                        }
                    }
                }

                result.Declarations.Add(decl);
                continue;
            }

            if (parse.HasKeyMarker)
            {
                // STANDALONE key marker: this single cell declares the domain (and, if present,
                // the hierarchy) and fixes the key at once — there is no previous
                // "(dimension:domain)" cell that completes it.
                result.Declarations.Add(new DeclarationBuilder
                {
                    Region = "Header",
                    CellRef = CellRefUtil.ToCellRef(row, col),
                    DomainCode = string.IsNullOrEmpty(parse.Code2Domain) ? parse.Code1 : parse.Code2Domain,
                    HierarchyCode = parse.Code2Hierarchy ?? parse.TrailingBracketCode,
                    Label = LayoutAnnotationGrammar.ExtractLabel(parse),
                    IsKey = true,
                });
                claimed.Add((row, col));
                continue;
            }

            EvaluateStandaloneCell(sheet, columnOrdinates, yOrdinateByRow, pendingCells, text, row, col, dictionary, result, claimed);
        }
    }

    /// <summary>Tries to interpret <paramref name="text"/> as the value of <paramref name="decl"/>.
    /// Returns <see langword="false"/> without claiming the cell if the shape is not compatible (so
    /// that it stays free for another declaration, or the final safety net, to consider). If the
    /// shape IS compatible but the code does not exist in the dictionary, the cell is claimed and
    /// recorded in <c>LayoutUnparsed</c> — a correct positional pairing does not prove that the
    /// code is real.</summary>
    private static bool TryPairValue(
        string text, int row, int col, DeclarationBuilder decl, bool declIsMainProperty,
        LayoutDictionary dictionary, LayoutSheetResult result, HashSet<(int, int)> claimed, string region)
    {
        if (LayoutDatapointGrammar.Matches(text))
        {
            return false;
        }

        var parse = LayoutAnnotationGrammar.Parse(text);
        var cellRef = CellRefUtil.ToCellRef(row, col);

        if (parse.HasKeyMarker && !declIsMainProperty)
        {
            decl.IsKey = true;
            decl.HierarchyCode ??= parse.Code2Hierarchy ?? parse.TrailingBracketCode;
            if (string.IsNullOrEmpty(decl.DomainCode) && parse.Code1 is not null)
            {
                decl.DomainCode = parse.Code1;
            }

            claimed.Add((row, col));
            return true;
        }

        if (parse.Kind == AnnotationKind.TwoCode && !declIsMainProperty)
        {
            claimed.Add((row, col));
            if (!dictionary.IsValidDomainMember(parse.Code1, parse.Code2Domain))
            {
                result.Unparsed.Add(new UnparsedRow
                {
                    CellRef = cellRef,
                    Value = text,
                    Kind = UnparsedKind.OrphanValue,
                    Reason = $"two-code value ({parse.Code1}:{parse.Code2Domain}) paired by position " +
                             $"with the declaration at {decl.CellRef}, but the dictionary does not recognise that member " +
                             "in that domain",
                });
                return true;
            }

            result.Values.Add(new ValueRow
            {
                Declaration = decl,
                DomainCode = parse.Code1,
                MemberCode = parse.Code2Domain,
                Label = LayoutAnnotationGrammar.ExtractLabel(parse),
                CellRef = cellRef,
            });
            return true;
        }

        if (parse.Kind == AnnotationKind.SingleCode && declIsMainProperty)
        {
            var resolution = dictionary.Resolve(parse.Code1!, out var domainCode);
            if (resolution == SingleCodeResolution.Metric || !dictionary.IsPresent)
            {
                // Without a dictionary (dictionary.IsPresent == false) the conservative behaviour
                // is kept (treat as a metric) because there is no way to tell the key-dimension
                // case apart without consulting the real codes.
                result.Values.Add(new ValueRow
                {
                    Declaration = decl,
                    DomainCode = "MET",
                    MemberCode = parse.Code1,
                    Label = LayoutAnnotationGrammar.ExtractLabel(parse),
                    CellRef = cellRef,
                });
                claimed.Add((row, col));
                return true;
            }

            if (resolution == SingleCodeResolution.Dimension)
            {
                result.Declarations.Add(new DeclarationBuilder
                {
                    Region = region,
                    CellRef = cellRef,
                    DimensionCode = parse.Code1,
                    DomainCode = domainCode,
                    Label = LayoutAnnotationGrammar.ExtractLabel(parse),
                });
                claimed.Add((row, col));
                return true;
            }

            // Unknown with a dictionary present: "(a)", "(EU)", "(call)"... is not a DPM
            // annotation. It is claimed so that nobody else considers it and it is not recorded
            // in Unparsed.
            claimed.Add((row, col));
            return true;
        }

        return false;
    }

    private static DeclarationBuilder BuildTwoCodeDeclaration(AnnotationParse parse, string region, string cellRef)
    {
        var decl = new DeclarationBuilder { Region = region, CellRef = cellRef, Label = LayoutAnnotationGrammar.ExtractLabel(parse) };
        if (parse.HasKeyMarker)
        {
            // Merged form "(domain:hierarchy-or-empty) ... <Key value>": this same cell
            // declares AND fixes the key. There is no dimension code of its own to extract
            // — Code1 is the DOMAIN, not the dimension.
            decl.DomainCode = string.IsNullOrEmpty(parse.Code2Domain) ? parse.Code1 : parse.Code2Domain;
            decl.HierarchyCode = parse.Code2Hierarchy ?? parse.TrailingBracketCode;
            decl.IsKey = true;
        }
        else
        {
            decl.DimensionCode = parse.Code1;
            decl.DomainCode = parse.Code2Domain;
            decl.HierarchyCode = parse.Code2Hierarchy;
        }

        return decl;
    }

    /// <summary>A cell that no pairing has claimed. Gate of <c>LayoutUnparsed</c>: it is evaluated
    /// whether it is in ANNOTATION POSITION — starts with <c>"("</c>, or contains the marker
    /// <c>"&lt;Key value&gt;"</c>, after stripping at most a datapoint prefix
    /// (<c>NNNNN_x000D_</c>) — and if so, whether it parses. Prose outside that position is simply
    /// ignored. Inside the position: "(-)", "(a)", "(call)", "(EU)" close the parenthesis with
    /// non-code content — rhetorical prose, ignored. "(ZZBROKEN unclosed..." does NOT close —
    /// broken syntax, recorded as <see cref="UnparsedKind.GrammarFailure"/>.
    /// The check is for the LITERAL MARKER, not for any "&lt;": a version that looked for a bare
    /// "&lt;" produced hundreds of false positives on 4.2, ALL of them range labels with
    /// mathematical inequalities ("0 &lt;= 1 month", "&gt; 1 &lt;= 3 months"), which are not DPM
    /// annotations.</summary>
    private static void EvaluateStandaloneCell(
        XlsxSheet sheet, Dictionary<int, OrdinateBuilder> columnOrdinates,
        IReadOnlyDictionary<int, OrdinateBuilder> yOrdinateByRow, List<CellBuilder> pendingCells, string text,
        int row, int col, LayoutDictionary dictionary, LayoutSheetResult result, HashSet<(int, int)> claimed)
    {
        if (LayoutDatapointGrammar.Matches(text))
        {
            RegisterDatapointOrdinate(sheet, columnOrdinates, yOrdinateByRow, pendingCells, row, col, text, result, claimed);
            return;
        }

        claimed.Add((row, col));

        var core = DatapointIdPrefixPattern.Replace(text.Trim(), string.Empty).TrimStart();
        var hasKeyMarker = core.Contains(LayoutAnnotationGrammar.KeyValueMarker, StringComparison.Ordinal);
        if (!core.StartsWith('(') && !hasKeyMarker)
        {
            // Prose outside annotation position: not recorded.
            return;
        }

        var parse = LayoutAnnotationGrammar.Parse(core);

        switch (parse.Kind)
        {
            case AnnotationKind.PlainText:
                // Contains "<Key value>" but does not start with "(": none of the three shapes
                // covers it — a key marker ALWAYS needs its domain parenthesis.
                RegisterUnparsed(
                    row, col, text, UnparsedKind.GrammarFailure, result,
                    "contains '<Key value>' in annotation position but is not preceded by '(domain:...)'");
                return;

            case AnnotationKind.NotCodeShaped:
                if (!core.Contains(')'))
                {
                    // Unclosed parenthesis: broken syntax, not rhetorical prose.
                    RegisterUnparsed(
                        row, col, text, UnparsedKind.GrammarFailure, result,
                        "starts with '(' in annotation position but the parenthesis does not close (broken syntax)");
                }

                // If it closes but the content is not shaped like a code ("(-)", "(a)", "(call)",
                // "(EU)"...): rhetorical prose, not a DPM annotation — not recorded.
                return;

            case AnnotationKind.SingleCode:
                var resolution = dictionary.Resolve(parse.Code1!, out _);
                if (resolution == SingleCodeResolution.Unknown)
                {
                    return;
                }

                RegisterUnparsed(
                    row, col, text, UnparsedKind.OrphanValue, result,
                    $"single-term code resolved by the dictionary as {resolution} but with no " +
                    "'Main Property' row to pair it with (orphan)");
                return;

            case AnnotationKind.TwoCode:
                RegisterUnparsed(
                    row, col, text, UnparsedKind.OrphanValue, result,
                    "two-code annotation with no declaration paired in its row or its column (orphan)");
                return;
        }
    }

    /// <summary>A cell shaped like "datapoint identifier + type" that does not fall in the usual
    /// position (after the code of a row ordinate): it is registered as a
    /// <see cref="CellBuilder"/>, with the column ordinate that corresponds to it — an existing one
    /// (from <paramref name="columnOrdinates"/>, seeded from the code row or from another earlier
    /// cell of the same column) or a fallback one, created here.</summary>
    private static void RegisterDatapointOrdinate(
        XlsxSheet sheet, Dictionary<int, OrdinateBuilder> columnOrdinates,
        IReadOnlyDictionary<int, OrdinateBuilder> yOrdinateByRow, List<CellBuilder> pendingCells, int row, int col,
        string text, LayoutSheetResult result, HashSet<(int, int)> claimed)
    {
        claimed.Add((row, col));
        if (!LayoutDatapointGrammar.TryParse(text, out var datapointId, out var dataType))
        {
            return;
        }

        var rowOrdinate = yOrdinateByRow.GetValueOrDefault(row);
        RegisterCell(sheet, columnOrdinates, rowOrdinate, row, col, datapointId, dataType, pendingCells, result);
    }

    /// <summary>
    /// Notes a data-cell SIGHTING: resolves — or creates, if it is a fallback — the column ordinate
    /// of <paramref name="col"/> and adds the corresponding <see cref="CellBuilder"/> to
    /// <paramref name="pendingCells"/>, with the shading read from the Excel itself.
    /// <paramref name="rowOrdinate"/> is the <c>Y</c> ordinate of this same row, if the sheet has
    /// any — it is propagated as is, including when it is <see langword="null"/> (open-row-axis
    /// sheet: the absence is the correct answer, not a resolution failure).
    ///
    /// This does NOT write directly to <c>result.Cells</c> —
    /// <see cref="PopulateCells"/> decides, when closing the sheet, whether this list really serves
    /// (open axis) or whether it is replaced by the complete cartesian product (closed axes). It is
    /// still needed in any case: it is how a fallback column (without a real code) is DISCOVERED —
    /// its ordinate has to exist in <paramref name="columnOrdinates"/> before
    /// <see cref="PopulateCells"/> can cross it with the rows.
    /// </summary>
    private static void RegisterCell(
        XlsxSheet sheet, Dictionary<int, OrdinateBuilder> columnOrdinates, OrdinateBuilder? rowOrdinate,
        int row, int col, string datapointId, string? dataType, List<CellBuilder> pendingCells, LayoutSheetResult result)
    {
        var columnOrdinate = GetOrCreateColumnOrdinate(columnOrdinates, col, result);
        pendingCells.Add(new CellBuilder
        {
            RowOrdinate = rowOrdinate,
            ColumnOrdinate = columnOrdinate,
            CellRef = CellRefUtil.ToCellRef(row, col),
            DatapointId = datapointId,
            DataType = dataType,
            IsShaded = sheet.IsCellShaded(row, col),
        });
    }

    /// <summary>
    /// The X-axis ordinate of <paramref name="col"/> in this sheet: the one already created (from
    /// the code row, in <see cref="Parse"/>, or from an earlier data cell in this same column) or,
    /// if this column never had a real code, a FALLBACK one with the column letter as a LAST
    /// RESORT — explicitly marked with <see cref="OrdinateBuilder.IsFallbackCode"/> so that it is
    /// not indistinguishable from a real code. It is created AT MOST ONCE per column: otherwise
    /// each data cell would create its own <c>OrdinateBuilder</c> even though it shares its code
    /// with all the others of its column.
    /// </summary>
    private static OrdinateBuilder GetOrCreateColumnOrdinate(
        Dictionary<int, OrdinateBuilder> columnOrdinates, int col, LayoutSheetResult result)
    {
        if (columnOrdinates.TryGetValue(col, out var existing))
        {
            return existing;
        }

        var ordinate = new OrdinateBuilder
        {
            Axis = LayoutAxis.Column,
            OrdinateCode = CellRefUtil.IndexToColumnLetter(col),
            IsFallbackCode = true,
            Position = col,
        };
        columnOrdinates[col] = ordinate;
        result.Ordinates.Add(ordinate);
        return ordinate;
    }

    /// <summary>
    /// The row of column codes of the X axis: the LAST header row (right above where the "Rows"
    /// grid starts, that is <c>markerRow - 1</c>), with a short code (2-6 digits) per column. In the
    /// real 4.2 sheets with X-axis ordinates, that row resolves 100&#160;% of the columns, and it is
    /// ALWAYS the same for all the columns of one sheet — there are no sheets with more than one
    /// block of columns or with the code row in another position. If
    /// <paramref name="markerRow"/> does not exist (sheet without a "Rows" marker) or the previous
    /// row has no cells, the map stays empty and <see cref="GetOrCreateColumnOrdinate"/> falls back
    /// to the column letter, marked as a fallback.
    /// THIS map —and not the presence of data— decides the SET of X ordinates of the sheet (see
    /// <see cref="Parse"/>): it is walked in full to seed <c>result.ColumnOrdinates</c> before
    /// looking at a single data cell.
    /// </summary>
    private static Dictionary<int, string> BuildColumnCodeMap(XlsxSheet sheet, int markerRow)
    {
        var map = new Dictionary<int, string>();
        if (markerRow is int.MaxValue or <= 1)
        {
            return map;
        }

        var codeRow = markerRow - 1;
        foreach (var ((r, c), value) in sheet.Cells)
        {
            if (r != codeRow)
            {
                continue;
            }

            var trimmed = value.Trim();
            if (ShortCodePattern.IsMatch(trimmed))
            {
                map[c] = trimmed;
            }
        }

        return map;
    }

    private static void RegisterUnparsed(int row, int col, string text, string kind, LayoutSheetResult result, string reason)
        => result.Unparsed.Add(new UnparsedRow { CellRef = CellRefUtil.ToCellRef(row, col), Value = text, Kind = kind, Reason = reason });

    // The label of the metric column changed name between publication eras ("Metric" in 3.2,
    // "Main Property" since 4.0) without changing cell. Accepting only the new literal leaves a
    // 3.2 layout WITHOUT a single metric and without any error — both must be accepted, and
    // neither is hard-wired to a specific axis: this only recognises the LABEL, the axis it
    // appears on is decided by the sheet, not by this method.
    private static bool IsMainProperty(string text)
    {
        var trimmed = text.Trim();
        return string.Equals(trimmed, LayoutAnnotationGrammar.MainPropertyLabel, StringComparison.Ordinal)
            || string.Equals(trimmed, LayoutAnnotationGrammar.MetricLabel, StringComparison.Ordinal);
    }

    private static List<int> ColumnsOf(XlsxSheet sheet, int row)
        => sheet.Cells.Keys.Where(k => k.Row == row && k.Col > 1).Select(k => k.Col).OrderBy(c => c).ToList();
}
