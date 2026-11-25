using System.IO.Compression;
using System.Text;
using EbaDpm.Converter.Core.Layouts;
using EbaDpm.Converter.Core.Layouts.Xlsx;

namespace EbaDpm.Converter.Core.Tests.Layouts;

/// <summary>
/// Two structural features of the EBA 4.4 workbooks that the 4.2/4.3 ones do not have, reduced to synthetic
/// cases (#67), each with its positive control (the same workbook in the old shape reads fine):
/// <list type="bullet">
/// <item>the relationship <c>Target</c>s are ABSOLUTE part names (<c>/xl/worksheets/sheet1.xml</c>,
/// <c>/xl/comments/comment1.xml</c>), valid OPC, where 4.2/4.3 write them relative to the part;</item>
/// <item>a sheet of a table with a Z axis is named with a space before the Z code
/// (<c>K_74.00.a (0010)</c>), where 4.2/4.3 write <c>C_14.01(0010)</c>.</item>
/// </list>
/// </summary>
[Trait("Tier", "Synthetic")]
public sealed class Layout44StructuralSyntheticTests : IDisposable
{
    private readonly string _dir;
    private int _counter;

    public Layout44StructuralSyntheticTests()
    {
        _dir = Path.Join(Path.GetTempPath(), $"EbaDpm.Layout44Structural_{Environment.ProcessId}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort.
        }
    }

    /// <summary>One sheet "S" with A1 = "hello" and a comment on A1 = "the comment", with the relationship targets either relative or absolute.</summary>
    private string Workbook(bool absoluteSheetTarget, bool absoluteCommentsTarget)
    {
        var path = Path.Join(_dir, $"wb{_counter++}.xlsx");
        var sheetTarget = absoluteSheetTarget ? "/xl/worksheets/sheet1.xml" : "worksheets/sheet1.xml";
        var commentsTarget = absoluteCommentsTarget ? "/xl/comments/comment1.xml" : "../comments/comment1.xml";
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        Write(zip, "xl/workbook.xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"S\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
        Write(zip, "xl/_rels/workbook.xml.rels",
            $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"{sheetTarget}\"/></Relationships>");
        Write(zip, "xl/worksheets/sheet1.xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData><row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>hello</t></is></c></row></sheetData></worksheet>");
        Write(zip, "xl/worksheets/_rels/sheet1.xml.rels",
            $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"comments\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/comments\" Target=\"{commentsTarget}\"/></Relationships>");
        Write(zip, "xl/comments/comment1.xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><comments xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><commentList><comment ref=\"A1\"><text><r><t>the comment</t></r></text></comment></commentList></comments>");
        return path;
    }

    private static void Write(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }

    [Fact]
    public void RelativeTargets_AreRead_ThePositiveControlOfTheAbsoluteCases()
    {
        var sheet = Assert.Single(XlsxReader.Read(Workbook(absoluteSheetTarget: false, absoluteCommentsTarget: false)).Sheets);

        Assert.Equal("hello", sheet.Cells[(1, 1)]);
        Assert.Equal("the comment", sheet.Comments[(1, 1)]);
    }

    [Fact]
    public void AbsolutePartNameTargets_OfTheWorksheets_AreResolved()
    {
        var sheet = Assert.Single(XlsxReader.Read(Workbook(absoluteSheetTarget: true, absoluteCommentsTarget: false)).Sheets);

        Assert.Equal("S", sheet.Name);
        Assert.Equal("hello", sheet.Cells[(1, 1)]);
    }

    [Fact]
    public void AbsolutePartNameTargets_OfTheComments_AreResolved_NotSilentlyDropped()
    {
        // Only the comments target is absolute here, so this isolates the comments resolution from the worksheet one.
        var sheet = Assert.Single(XlsxReader.Read(Workbook(absoluteSheetTarget: false, absoluteCommentsTarget: true)).Sheets);

        Assert.True(sheet.Comments.TryGetValue((1, 1), out var comment), "The comment of A1 was silently dropped.");
        Assert.Equal("the comment", comment);
    }

    [Theory]
    [InlineData("C_14.01(0010)", "C_14.01", "0010")] // 4.2 / 4.3 spelling
    [InlineData("K_74.00.a (0010)", "K_74.00.a", "0010")] // 4.4 spelling: a space before the Z code
    [InlineData("K_26.00.b (0001)", "K_26.00.b", "0001")]
    [InlineData("K_74.00.e", "K_74.00.e", null)]
    public void TheSheetName_IsSplitIntoTheTableCodeAndTheZCode(string sheetName, string tableCode, string? zSuffix)
    {
        var (actualCode, actualZ) = LayoutSheetNameParser.Parse(sheetName);

        Assert.Equal(tableCode, actualCode);
        Assert.Equal(zSuffix, actualZ);
    }
}
