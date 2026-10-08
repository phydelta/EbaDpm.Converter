using System.IO.Compression;
using System.Text;

namespace EbaDpm.Converter.Tests.Layouts;

/// <summary>
/// Builds a minimal <c>.xlsx</c> (ZIP + OOXML, without any Office library) to test
/// <c>LayoutExtractor</c> with fabricated cases, NEVER touching the data directory. It only writes
/// the entries <c>XlsxReader</c> needs: workbook, rels and a sheet with inline-string cells
/// (this avoids sharedStrings.xml altogether, which is optional).
///
/// The file name on disk must follow the pattern required by <c>LayoutFileNameParser</c>;
/// <see cref="Save"/> generates it automatically.
/// </summary>
internal sealed class MinimalXlsxBuilder
{
    private readonly Dictionary<string, string> _cells = new(StringComparer.Ordinal);
    private readonly HashSet<string> _shadedCells = new(StringComparer.Ordinal);
    private readonly string _sheetName;

    public MinimalXlsxBuilder(string sheetName) => _sheetName = sheetName;

    /// <summary>Adds a cell with literal text (inline string: no formulas, no styles).</summary>
    public MinimalXlsxBuilder WithCell(string cellRef, string text)
    {
        _cells[cellRef] = text;
        return this;
    }

    /// <summary>
    /// Adds a GREY cell: it is written with the <c>s</c> attribute pointing to the
    /// <c>&lt;xf&gt;</c> of <c>cellXfs</c> whose <c>fillId</c> resolves to <c>indexed="55"</c> in
    /// the <c>styles.xml</c> fabricated by <see cref="Save"/> -- the exact same mechanism that
    /// <c>XlsxReader.IsShadedStyle</c> reads from a real Annotated Table Layout, not a test
    /// shortcut. The text may be empty (a shaded cell, by definition, carries no datapoint).
    /// </summary>
    public MinimalXlsxBuilder WithShadedCell(string cellRef, string text = "")
    {
        if (text.Length > 0)
        {
            _cells[cellRef] = text;
        }

        _shadedCells.Add(cellRef);
        return this;
    }

    /// <summary>
    /// Writes the file into <paramref name="directory"/> with a name that follows the
    /// <c>LayoutFileNameParser</c> pattern: <c>"{date} Annotated Table Layout  {FW} {REL} {MODULE}{FW} {REL}.xlsx"</c>.
    /// </summary>
    public string Save(string directory, string framework = "TEST", string release = "1.0", string module = "TEST")
    {
        Directory.CreateDirectory(directory);
        var fileName = $"20260101 Annotated Table Layout  {framework} {release} {module}{framework} {release}.xlsx";
        var path = Path.Combine(directory, fileName);

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);

        WriteEntry(zip, "xl/workbook.xml", WorkbookXml());
        WriteEntry(zip, "xl/_rels/workbook.xml.rels", WorkbookRelsXml());
        WriteEntry(zip, "xl/worksheets/sheet1.xml", SheetXml());
        if (_shadedCells.Count > 0)
        {
            // styles.xml is only written when needed (XlsxReader treats its absence as "no
            // fills", 0 shaded cells -- just like a real .xlsx without any grey cell).
            WriteEntry(zip, "xl/styles.xml", StylesXml());
        }

        return path;
    }

    /// <summary>
    /// Like <see cref="Save"/>, but with the LITERAL file name instead of the fabricated 4.2/4.3-era
    /// pattern -- needed to test <c>LayoutFileNameParser</c> with the other two name families (3.2
    /// without date or module, 4.0 without the leading "{FW} {REL}") without touching the data
    /// directory.
    /// </summary>
    public string SaveWithFileName(string directory, string fileName)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);

        WriteEntry(zip, "xl/workbook.xml", WorkbookXml());
        WriteEntry(zip, "xl/_rels/workbook.xml.rels", WorkbookRelsXml());
        WriteEntry(zip, "xl/worksheets/sheet1.xml", SheetXml());
        if (_shadedCells.Count > 0)
        {
            WriteEntry(zip, "xl/styles.xml", StylesXml());
        }

        return path;
    }

    /// <summary>
    /// <c>fills[0]</c> = no fill (default style, index 0, the one used by any cell without an
    /// <c>s</c> attribute); <c>fills[1]</c> = GREY, <c>indexed="55"</c> -- the exact same colour
    /// measured in the real Annotated Table Layouts, not a convenience value.
    /// <c>cellXfs[1]</c> is the only style used by the cells of <see cref="WithShadedCell"/>
    /// (<c>s="1"</c>).
    /// </summary>
    private static string StylesXml() =>
        """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
        <fills count="2">
        <fill><patternFill patternType="none"/></fill>
        <fill><patternFill patternType="solid"><fgColor indexed="55"/><bgColor indexed="64"/></patternFill></fill>
        </fills>
        <cellXfs count="2">
        <xf numFmtId="0" fontId="0" fillId="0" borderId="0"/>
        <xf numFmtId="0" fontId="0" fillId="1" borderId="0" applyFill="1"/>
        </cellXfs>
        </styleSheet>
        """;

    private string WorkbookXml() =>
        $"""
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
        <sheets><sheet name="{Escape(_sheetName)}" sheetId="1" r:id="rId1"/></sheets>
        </workbook>
        """;

    private static string WorkbookRelsXml() =>
        """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
        <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
        </Relationships>
        """;

    private string SheetXml()
    {
        // Union of the two sources: a shaded cell WITHOUT text (WithShadedCell with no argument)
        // is not in _cells, but XlsxReader.IsShadedStyle needs its own <c> element with the "s"
        // attribute -- the shading belongs to the cell, not to the value ("it may not have a
        // single value and still be, for the purposes of IsShaded, a grey cell").
        var allRefs = _cells.Keys.Concat(_shadedCells).Distinct(StringComparer.Ordinal);
        var byRow = allRefs
            .Select(cellRef => (Cell: cellRef, Row: ParseRow(cellRef)))
            .GroupBy(x => x.Row)
            .OrderBy(g => g.Key);

        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sb.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");
        foreach (var group in byRow)
        {
            sb.Append($"<row r=\"{group.Key}\">");
            foreach (var (cellRef, _) in group.OrderBy(x => x.Cell))
            {
                var styleAttr = _shadedCells.Contains(cellRef) ? " s=\"1\"" : string.Empty;
                if (_cells.TryGetValue(cellRef, out var text))
                {
                    sb.Append($"<c r=\"{cellRef}\"{styleAttr} t=\"inlineStr\"><is><t>{Escape(text)}</t></is></c>");
                }
                else
                {
                    sb.Append($"<c r=\"{cellRef}\"{styleAttr}/>");
                }
            }

            sb.Append("</row>");
        }

        sb.Append("</sheetData></worksheet>");
        return sb.ToString();
    }

    private static int ParseRow(string cellRef)
    {
        var i = 0;
        while (i < cellRef.Length && char.IsLetter(cellRef[i]))
        {
            i++;
        }

        return int.Parse(cellRef[i..]);
    }

    private static string Escape(string text) => text
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;");

    private static void WriteEntry(ZipArchive zip, string entryName, string content)
    {
        var entry = zip.CreateEntry(entryName);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }
}
