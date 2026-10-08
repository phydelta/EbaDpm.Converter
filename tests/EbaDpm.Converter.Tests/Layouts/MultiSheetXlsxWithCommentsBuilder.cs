using System.IO.Compression;
using System.Text;

namespace EbaDpm.Converter.Tests.Layouts;

/// <summary>
/// Builds a MULTI-SHEET <c>.xlsx</c> with cell comments, by hand (ZIP + OOXML), in order to
/// fabricate the specific case that <c>XlsxReader.ReadComments</c> claims to resolve through
/// <c>.rels</c> and NOT by file number: a sheet WITHOUT comments followed by a sheet WITH
/// comments, where Excel numbers the first real <c>commentsN.xml</c> as <c>comments1.xml</c>
/// regardless of whether it is sheet 2, 3... that carries it (the comment number and the sheet
/// number are independent namespaces in OOXML).
///
/// It deliberately does NOT reuse <see cref="MinimalXlsxBuilder"/> (fixed to a SINGLE sheet
/// "sheet1.xml"): here the file number of each sheet and of each <c>comments*.xml</c> are the
/// very object under test, so they are controlled one by one.
/// </summary>
internal sealed class MultiSheetXlsxWithCommentsBuilder
{
    private sealed class SheetSpec
    {
        public required string Name { get; init; }
        public Dictionary<string, string> Cells { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> Comments { get; } = new(StringComparer.Ordinal);

        /// <summary>EXPLICIT file number of the <c>commentsN.xml</c> that belongs to this sheet --
        /// deliberately independent of the position of the sheet in <see cref="Sheets"/>, so that
        /// the misalignment can be fabricated at will.</summary>
        public int? CommentsFileNumber { get; set; }
    }

    private readonly List<SheetSpec> _sheets = [];

    public MultiSheetXlsxWithCommentsBuilder AddSheet(string name, out int sheetIndex)
    {
        _sheets.Add(new SheetSpec { Name = name });
        sheetIndex = _sheets.Count - 1;
        return this;
    }

    public MultiSheetXlsxWithCommentsBuilder WithCell(int sheetIndex, string cellRef, string text)
    {
        _sheets[sheetIndex].Cells[cellRef] = text;
        return this;
    }

    /// <summary>
    /// Adds a comment to (sheetIndex, cellRef), and fixes the <c>commentsN.xml</c> file number
    /// that will contain it -- the mechanism with which this builder fabricates the
    /// <c>sheetN</c> &lt;-&gt; <c>commentsN</c> misalignment: two sheets may share the same
    /// <paramref name="commentsFileNumber"/> only if that case is really wanted (not validated
    /// here, on purpose -- the test decides the exact shape).
    /// </summary>
    public MultiSheetXlsxWithCommentsBuilder WithComment(int sheetIndex, string cellRef, string text, int commentsFileNumber)
    {
        _sheets[sheetIndex].Comments[cellRef] = text;
        _sheets[sheetIndex].CommentsFileNumber = commentsFileNumber;
        return this;
    }

    public string Save(string directory, string framework = "TEST", string release = "1.0", string module = "TEST")
    {
        Directory.CreateDirectory(directory);
        var fileName = $"20260101 Annotated Table Layout  {framework} {release} {module}{framework} {release}.xlsx";
        var path = Path.Combine(directory, fileName);

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);

        WriteEntry(zip, "xl/workbook.xml", WorkbookXml());
        WriteEntry(zip, "xl/_rels/workbook.xml.rels", WorkbookRelsXml());

        for (var i = 0; i < _sheets.Count; i++)
        {
            var sheetFileNumber = i + 1; // sheet1.xml, sheet2.xml... IN ORDER, unrelated to comments.
            WriteEntry(zip, $"xl/worksheets/sheet{sheetFileNumber}.xml", SheetXml(_sheets[i]));

            if (_sheets[i].Comments.Count > 0)
            {
                var commentsFileNumber = _sheets[i].CommentsFileNumber
                    ?? throw new InvalidOperationException($"Sheet '{_sheets[i].Name}' has comments without a CommentsFileNumber.");

                WriteEntry(zip, $"xl/worksheets/_rels/sheet{sheetFileNumber}.xml.rels", SheetRelsXml(commentsFileNumber));
                WriteEntry(zip, $"xl/comments{commentsFileNumber}.xml", CommentsXml(_sheets[i]));
            }
        }

        return path;
    }

    private string WorkbookXml()
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sb.Append("<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">");
        sb.Append("<sheets>");
        for (var i = 0; i < _sheets.Count; i++)
        {
            sb.Append($"<sheet name=\"{Escape(_sheets[i].Name)}\" sheetId=\"{i + 1}\" r:id=\"rId{i + 1}\"/>");
        }

        sb.Append("</sheets></workbook>");
        return sb.ToString();
    }

    private string WorkbookRelsXml()
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sb.Append("<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
        for (var i = 0; i < _sheets.Count; i++)
        {
            var sheetFileNumber = i + 1;
            sb.Append($"<Relationship Id=\"rId{i + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{sheetFileNumber}.xml\"/>");
        }

        sb.Append("</Relationships>");
        return sb.ToString();
    }

    private static string SheetRelsXml(int commentsFileNumber) =>
        $"""
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
        <Relationship Id="rIdC1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/comments" Target="../comments{commentsFileNumber}.xml"/>
        </Relationships>
        """;

    private static string CommentsXml(SheetSpec sheet)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sb.Append("<comments xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
        sb.Append("<authors><author>Test</author></authors><commentList>");
        foreach (var (cellRef, text) in sheet.Comments)
        {
            sb.Append($"<comment ref=\"{cellRef}\" authorId=\"0\"><text><t>{Escape(text)}</t></text></comment>");
        }

        sb.Append("</commentList></comments>");
        return sb.ToString();
    }

    private static string SheetXml(SheetSpec sheet)
    {
        var byRow = sheet.Cells.Keys
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
                sb.Append($"<c r=\"{cellRef}\" t=\"inlineStr\"><is><t>{Escape(sheet.Cells[cellRef])}</t></is></c>");
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
