using System.IO.Compression;
using System.Xml.Linq;

namespace EbaDpm.Converter.Core.Layouts.Xlsx;

/// <summary>
/// Minimal <c>.xlsx</c> (OOXML) reader: the file is a ZIP with XML inside, so
/// <see cref="System.IO.Compression.ZipFile"/> + <see cref="System.Xml.Linq"/> are enough — no extra
/// NuGet package. It only resolves what the layout extractor needs: sheet name and ID, cell text
/// (formulas out of scope), merged ranges and the SHADING of each cell (<c>xl/styles.xml</c>:
/// <c>cellXfs</c> → <c>fillId</c> → colour of <c>fills</c>) — the rest of the style (fonts,
/// borders, number format) remains out of scope.
/// </summary>
internal static class XlsxReader
{
    // Cell comments are linked via _rels through the "comments" relationship -
    // never by file number (see ReadComments).
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace RIdNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    public static XlsxWorkbook Read(string path)
    {
        using var zip = ZipFile.OpenRead(path);

        var sharedStrings = ReadSharedStrings(zip);
        var shadedFillIds = ReadShadedFillIds(zip);
        var cellXfFillIds = ReadCellXfFillIds(zip);

        var workbookXml = LoadEntry(zip, "xl/workbook.xml")
            ?? throw new InvalidOperationException($"'{path}': xl/workbook.xml is missing.");
        var relsXml = LoadEntry(zip, "xl/_rels/workbook.xml.rels")
            ?? throw new InvalidOperationException($"'{path}': xl/_rels/workbook.xml.rels is missing.");

        // r:id -> path of the sheet XML (worksheets/sheetN.xml).
        var targetByRId = relsXml.Descendants(Rel + "Relationship")
            .Where(r => (string?)r.Attribute("Type") is { } t && t.EndsWith("/worksheet", StringComparison.Ordinal))
            .ToDictionary(
                r => (string)r.Attribute("Id")!,
                r => "xl/" + (string)r.Attribute("Target")!);

        var sheets = new List<XlsxSheet>();
        foreach (var sheetEl in workbookXml.Descendants(Main + "sheet"))
        {
            var name = (string)sheetEl.Attribute("name")!;
            var rId = (string?)sheetEl.Attribute(RIdNs + "id");
            if (rId is null || !targetByRId.TryGetValue(rId, out var target))
            {
                continue;
            }

            var sheetXml = LoadEntry(zip, target)
                ?? throw new InvalidOperationException($"'{path}': sheet '{name}' references '{target}', which does not exist.");
            var comments = ReadComments(zip, target);
            sheets.Add(ReadSheet(name, sheetXml, sharedStrings, shadedFillIds, cellXfFillIds, comments));
        }

        return new XlsxWorkbook { Sheets = sheets };
    }

    /// <summary>
    /// The comments of ONE sheet, indexed by (row, column) — or empty if the sheet has none. The
    /// link is <c>sheetTarget</c> (e.g. <c>xl/worksheets/sheet2.xml</c>) -&gt;
    /// <c>xl/worksheets/_rels/sheet2.xml.rels</c> -&gt; relationship whose <c>Type</c> ends in
    /// <c>/comments</c> -&gt; its <c>Target</c>, relative to the directory of
    /// <paramref name="sheetTarget"/> (typically <c>../comments1.xml</c>).
    /// "sheetN.xml -&gt; commentsN.xml" is deliberately NOT assumed: the comments number and the
    /// sheet number are independent namespaces in OOXML, and the layouts have them misaligned as
    /// soon as a sheet carries no comments (the following commentsN.xml shift) — only the
    /// relationship in the file itself can be trusted.
    /// </summary>
    private static Dictionary<(int Row, int Col), string> ReadComments(ZipArchive zip, string sheetTarget)
    {
        var result = new Dictionary<(int, int), string>();

        var sheetDir = sheetTarget.Contains('/', StringComparison.Ordinal)
            ? sheetTarget[..sheetTarget.LastIndexOf('/')]
            : string.Empty;
        var sheetFileName = sheetTarget[(sheetDir.Length > 0 ? sheetDir.Length + 1 : 0)..];
        var relsEntryName = (sheetDir.Length > 0 ? sheetDir + "/" : string.Empty) + "_rels/" + sheetFileName + ".rels";

        var relsXml = LoadEntry(zip, relsEntryName);
        if (relsXml is null)
        {
            return result;
        }

        var commentsTarget = relsXml.Descendants(Rel + "Relationship")
            .Where(r => (string?)r.Attribute("Type") is { } t && t.EndsWith("/comments", StringComparison.Ordinal))
            .Select(r => (string?)r.Attribute("Target"))
            .FirstOrDefault();
        if (commentsTarget is null)
        {
            return result;
        }

        var commentsEntryName = ResolveRelativePath(sheetDir, commentsTarget);
        var commentsXml = LoadEntry(zip, commentsEntryName);
        if (commentsXml is null)
        {
            return result;
        }

        foreach (var commentEl in commentsXml.Descendants(Main + "comment"))
        {
            var cellRef = (string?)commentEl.Attribute("ref");
            var textEl = commentEl.Element(Main + "text");
            if (cellRef is null || textEl is null)
            {
                continue;
            }

            // The literal escape "_x000D_" is how Excel stores a line break INSIDE a comment (the
            // '\r' of an Alt+Enter does not survive as is in an XML text node) — it is resolved
            // here, once, so that LayoutEqualityRuleParser can split by '\n' without knowing this
            // format detail.
            var text = textEl.Value.Replace("_x000D_", "\n", StringComparison.Ordinal);
            var (row, col) = CellRefUtil.Parse(cellRef);
            result[(row, col)] = text;
        }

        return result;
    }

    /// <summary>Resolves a RELATIVE relationship <c>Target</c> (it may carry <c>../</c>) against the
    /// directory of the file that declares it — without <see cref="Path"/>, which on Windows
    /// normalises with <c>\</c> whereas <see cref="ZipArchive"/> entries always use <c>/</c>.</summary>
    private static string ResolveRelativePath(string baseDir, string relativeTarget)
    {
        var combined = (baseDir.Length > 0 ? baseDir + "/" : string.Empty) + relativeTarget;
        var stack = new List<string>();
        foreach (var part in combined.Split('/'))
        {
            if (part == "..")
            {
                if (stack.Count > 0)
                {
                    stack.RemoveAt(stack.Count - 1);
                }
            }
            else if (part.Length > 0 && part != ".")
            {
                stack.Add(part);
            }
        }

        return string.Join("/", stack);
    }

    private static XlsxSheet ReadSheet(
        string name, XElement sheetXml, IReadOnlyList<string> sharedStrings,
        IReadOnlyList<bool> shadedFillIds, IReadOnlyList<int> cellXfFillIds,
        Dictionary<(int Row, int Col), string> comments)
    {
        var cells = new Dictionary<(int Row, int Col), string>();
        var shaded = new HashSet<(int Row, int Col)>();
        foreach (var rowEl in sheetXml.Descendants(Main + "sheetData").Elements(Main + "row"))
        {
            foreach (var cellEl in rowEl.Elements(Main + "c"))
            {
                var cellRef = (string?)cellEl.Attribute("r");
                if (cellRef is null)
                {
                    continue;
                }

                var (row, col) = CellRefUtil.Parse(cellRef);

                // Shading belongs to the cell STYLE (attribute "s", index into
                // xl/styles.xml/cellXfs), independent of whether the cell has text — a disabled
                // column may not carry a single value and still be, for LayoutCell.IsShaded, a
                // grey cell. It is ALWAYS resolved, not only when there is text.
                if (IsShadedStyle(cellEl, shadedFillIds, cellXfFillIds))
                {
                    shaded.Add((row, col));
                }

                var text = ReadCellText(cellEl, sharedStrings);
                if (text is null || text.Length == 0)
                {
                    continue;
                }

                cells[(row, col)] = text;
            }
        }

        var merges = new List<(int R1, int C1, int R2, int C2)>();
        foreach (var mergeEl in sheetXml.Descendants(Main + "mergeCells").Elements(Main + "mergeCell"))
        {
            var refValue = (string?)mergeEl.Attribute("ref");
            if (refValue is null)
            {
                continue;
            }

            var parts = refValue.Split(':');
            var (r1, c1) = CellRefUtil.Parse(parts[0]);
            var (r2, c2) = parts.Length > 1 ? CellRefUtil.Parse(parts[1]) : (r1, c1);
            merges.Add((r1, c1, r2, c2));
        }

        return new XlsxSheet { Name = name, Cells = cells, Merges = merges, ShadedCells = shaded, Comments = comments };
    }

    /// <summary>
    /// Is <paramref name="cellEl"/> a shaded cell? Resolves <c>s</c> (index into
    /// <c>cellXfs</c>) -&gt; <c>fillId</c> -&gt; whether THAT <c>fillId</c>, in THIS file, is grey.
    /// Without an <c>s</c> attribute, the cell uses the default style (index 0), which in the
    /// observed layouts is never the grey fill.
    /// </summary>
    private static bool IsShadedStyle(XElement cellEl, IReadOnlyList<bool> shadedFillIds, IReadOnlyList<int> cellXfFillIds)
    {
        var styleAttr = (string?)cellEl.Attribute("s");
        var styleIndex = styleAttr is null ? 0 : int.Parse(styleAttr);
        if (styleIndex < 0 || styleIndex >= cellXfFillIds.Count)
        {
            return false;
        }

        var fillId = cellXfFillIds[styleIndex];
        return fillId >= 0 && fillId < shadedFillIds.Count && shadedFillIds[fillId];
    }

    /// <summary>
    /// Resolves the text of a cell according to its type (<c>t</c>): shared string (<c>s</c>),
    /// inline string (<c>inlineStr</c>), formula with a text result (<c>str</c>), boolean
    /// (<c>b</c>) or numeric (no <c>t</c>, or <c>n</c>). Errors (<c>e</c>) are returned as is.
    /// </summary>
    private static string? ReadCellText(XElement cellEl, IReadOnlyList<string> sharedStrings)
    {
        var type = (string?)cellEl.Attribute("t");
        if (type == "inlineStr")
        {
            var isEl = cellEl.Element(Main + "is");
            return isEl?.Value;
        }

        var vEl = cellEl.Element(Main + "v");
        if (vEl is null)
        {
            return null;
        }

        var raw = vEl.Value;
        if (type == "s")
        {
            var index = int.Parse(raw);
            return index >= 0 && index < sharedStrings.Count ? sharedStrings[index] : string.Empty;
        }

        if (type == "b")
        {
            return raw == "1" ? "TRUE" : "FALSE";
        }

        // "str" (formula->text), "e" (error) or numeric without a t attribute: the value as is.
        return raw;
    }

    /// <summary>
    /// Reads <c>xl/sharedStrings.xml</c>. The text of each <c>&lt;si&gt;</c> may come in a direct
    /// <c>&lt;t&gt;</c> or be split across several <c>&lt;r&gt;&lt;t&gt;</c> runs (rich text);
    /// <see cref="XElement.Value"/> concatenates both cases the same way. Absent in files that only
    /// use inline strings: an empty list is returned.
    /// </summary>
    private static List<string> ReadSharedStrings(ZipArchive zip)
    {
        var xml = LoadEntry(zip, "xl/sharedStrings.xml");
        if (xml is null)
        {
            return [];
        }

        return xml.Elements(Main + "si").Select(si => si.Value).ToList();
    }

    /// <summary>
    /// Reads <c>xl/styles.xml</c> / <c>&lt;fills&gt;</c> and returns, indexed by POSITION in that
    /// list (which is the <c>fillId</c> used by the <c>&lt;xf&gt;</c> of <c>cellXfs</c> — not a
    /// separate table of IDs), whether that fill is the disabled GREY:
    /// <c>patternType="solid"</c> with <c>fgColor</c> <c>indexed="55"</c> (legacy Excel palette) or
    /// <c>rgb="FFD8D8D8"</c>.
    ///
    /// "<c>fillId</c> number N is the grey" is deliberately NOT hard-wired: the ORDER of
    /// <c>&lt;fills&gt;</c> —and therefore which <c>fillId</c> number the grey fill gets— is a
    /// detail of EACH file, not a constant of the format (another release may number differently).
    /// The only stable thing is the COLOUR (<c>indexed=55</c> / <c>FFD8D8D8</c>), so it is
    /// resolved by reading the colour of each <c>fillId</c> in the <c>styles.xml</c> of the file
    /// being opened.
    /// </summary>
    private static List<bool> ReadShadedFillIds(ZipArchive zip)
    {
        var xml = LoadEntry(zip, "xl/styles.xml");
        var fillsEl = xml?.Element(Main + "fills");
        if (fillsEl is null)
        {
            return [];
        }

        var result = new List<bool>();
        foreach (var fillEl in fillsEl.Elements(Main + "fill"))
        {
            var patternFillEl = fillEl.Element(Main + "patternFill");
            var patternType = (string?)patternFillEl?.Attribute("patternType");
            if (!string.Equals(patternType, "solid", StringComparison.Ordinal))
            {
                result.Add(false);
                continue;
            }

            var fgColorEl = patternFillEl?.Element(Main + "fgColor");
            var indexed = (string?)fgColorEl?.Attribute("indexed");
            var rgb = (string?)fgColorEl?.Attribute("rgb");
            var isShaded = indexed == "55" || string.Equals(rgb, "FFD8D8D8", StringComparison.OrdinalIgnoreCase);
            result.Add(isShaded);
        }

        return result;
    }

    /// <summary>
    /// Reads <c>xl/styles.xml</c> / <c>&lt;cellXfs&gt;</c>: by POSITION (which is the value of the
    /// <c>s</c> attribute of a cell in the sheet XML), the <c>fillId</c> used by that style.
    /// Without a <c>fillId</c> attribute on the <c>&lt;xf&gt;</c> (inherits from the base style), 0
    /// is assumed — the "no fill" by OOXML convention, never the grey.
    /// </summary>
    private static List<int> ReadCellXfFillIds(ZipArchive zip)
    {
        var xml = LoadEntry(zip, "xl/styles.xml");
        var cellXfsEl = xml?.Element(Main + "cellXfs");
        if (cellXfsEl is null)
        {
            return [];
        }

        return cellXfsEl.Elements(Main + "xf")
            .Select(xf => (int?)xf.Attribute("fillId") ?? 0)
            .ToList();
    }

    private static XElement? LoadEntry(ZipArchive zip, string entryName)
    {
        var entry = zip.GetEntry(entryName);
        if (entry is null)
        {
            return null;
        }

        using var stream = entry.Open();
        return XElement.Load(stream);
    }
}
