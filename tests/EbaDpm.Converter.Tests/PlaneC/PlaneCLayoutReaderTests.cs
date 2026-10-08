using EbaDpm.Converter.Core.Validation.PlaneC;

namespace EbaDpm.Converter.Tests.PlaneC;

/// <summary>
/// The reconstruction of the signature FROM the layout, without going through
/// <see cref="PlaneCComparer"/> -- exercises <see cref="PlaneCLayoutReader"/> in isolation on
/// minimal synthetic repositories, one per rule. The INSERTs ALWAYS use an explicit column list
/// (never positional): the synthetic schema of <see cref="PlaneCSyntheticDatabase"/> is
/// deliberately narrower than the real repository -- only the columns that
/// <see cref="PlaneCLayoutReader"/> really queries -- and a positional list silently goes out of
/// sync as soon as the order changes.
/// </summary>
public sealed class PlaneCLayoutReaderTests
{
    /// <summary>
    /// Open-axis key rule: an <c>IsKey=1</c> declaration produces NO <c>LayoutValue</c> (the open
    /// axis is not enumerated, which is what makes it open); the term lives in the DECLARATION and
    /// is added as <c>&lt;dimension&gt;(*)</c> to ALL the cells of the sheet.
    /// </summary>
    [Fact]
    public void Read_KeyDeclarationWithoutValues_AddsAsteriskTermToAllCells()
    {
        using var layouts = PlaneCSyntheticDatabase.OpenLayouts();
        layouts.Exec("""INSERT INTO "LayoutFile" ("FileId","FrameworkCode","ReleaseLabel") VALUES (1, 'COREP', '4.2')""");
        layouts.Exec("""INSERT INTO "LayoutSheet" ("SheetId","FileId","TableCode") VALUES (1, 1, 'X_01.01')""");

        // Key declaration: DimensionCode='OPENDIM', WITHOUT any row in LayoutValue.
        layouts.Exec(
            """INSERT INTO "LayoutDeclaration" ("DeclId","SheetId","DimensionCode","DomainCode","IsKey") VALUES (1, 1, 'OPENDIM', NULL, 1)""");

        // Normal declaration (MET) with a value on the column ordinate.
        layouts.Exec(
            """INSERT INTO "LayoutDeclaration" ("DeclId","SheetId","DimensionCode","DomainCode","IsKey") VALUES (2, 1, NULL, 'MET', 0)""");
        layouts.Exec(
            """INSERT INTO "LayoutValue" ("ValueId","SheetId","DeclId","OrdinateId","MemberCode") VALUES (1, 1, 2, 10, 'qXX')""");

        layouts.Exec(
            """INSERT INTO "LayoutCell" ("CellId","SheetId","RowOrdinateId","ColumnOrdinateId","IsShaded") VALUES (1, 1, NULL, 10, 0)""");

        var data = PlaneCLayoutReader.Read(layouts);

        var signature = Assert.Single(data.Signatures);
        Assert.Equal(["MET(qXX)", "OPENDIM(*)"], signature.NormalizedTerms.OrderBy(t => t, StringComparer.Ordinal));
        Assert.Equal(1, data.NonShadedCellsExamined);
        Assert.Equal(0, data.CellsWithEmptySignature);
    }

    /// <summary>
    /// Control in the right direction of that rule: WITHOUT the key declaration, the same layout
    /// loses the term -- the signature is left incomplete, never the other way round. It confirms
    /// that the rule is what adds the term, not a side effect of another rule.
    /// </summary>
    [Fact]
    public void Read_WithoutKeyDeclaration_DoesNotAddTheAsteriskTerm()
    {
        using var layouts = PlaneCSyntheticDatabase.OpenLayouts();
        layouts.Exec("""INSERT INTO "LayoutFile" ("FileId","FrameworkCode","ReleaseLabel") VALUES (1, 'COREP', '4.2')""");
        layouts.Exec("""INSERT INTO "LayoutSheet" ("SheetId","FileId","TableCode") VALUES (1, 1, 'X_01.01')""");
        layouts.Exec(
            """INSERT INTO "LayoutDeclaration" ("DeclId","SheetId","DimensionCode","DomainCode","IsKey") VALUES (2, 1, NULL, 'MET', 0)""");
        layouts.Exec(
            """INSERT INTO "LayoutValue" ("ValueId","SheetId","DeclId","OrdinateId","MemberCode") VALUES (1, 1, 2, 10, 'qXX')""");
        layouts.Exec(
            """INSERT INTO "LayoutCell" ("CellId","SheetId","RowOrdinateId","ColumnOrdinateId","IsShaded") VALUES (1, 1, NULL, 10, 0)""");

        var data = PlaneCLayoutReader.Read(layouts);

        var signature = Assert.Single(data.Signatures);
        Assert.Equal(["MET(qXX)"], signature.NormalizedTerms);
    }

    /// <summary>
    /// SHEET values (<c>LayoutValue.OrdinateId IS NULL</c>) are the Z axis and apply to ALL the
    /// cells of the sheet.
    /// </summary>
    [Fact]
    public void Read_SheetValue_AppliesToAllTheCellsOfTheSheet()
    {
        using var layouts = PlaneCSyntheticDatabase.OpenLayouts();
        layouts.Exec("""INSERT INTO "LayoutFile" ("FileId","FrameworkCode","ReleaseLabel") VALUES (1, 'COREP', '4.2')""");
        layouts.Exec("""INSERT INTO "LayoutSheet" ("SheetId","FileId","TableCode") VALUES (1, 1, 'X_01.01')""");

        layouts.Exec(
            """INSERT INTO "LayoutDeclaration" ("DeclId","SheetId","DimensionCode","DomainCode","IsKey") VALUES (1, 1, 'ZDIM', NULL, 0)""");
        layouts.Exec(
            """INSERT INTO "LayoutValue" ("ValueId","SheetId","DeclId","OrdinateId","MemberCode") VALUES (1, 1, 1, NULL, 'zval')""");

        layouts.Exec(
            """INSERT INTO "LayoutDeclaration" ("DeclId","SheetId","DimensionCode","DomainCode","IsKey") VALUES (2, 1, NULL, 'MET', 0)""");
        layouts.Exec(
            """INSERT INTO "LayoutValue" ("ValueId","SheetId","DeclId","OrdinateId","MemberCode") VALUES (2, 1, 2, 10, 'qAA')""");
        layouts.Exec(
            """INSERT INTO "LayoutValue" ("ValueId","SheetId","DeclId","OrdinateId","MemberCode") VALUES (3, 1, 2, 11, 'qBB')""");

        layouts.Exec(
            """INSERT INTO "LayoutCell" ("CellId","SheetId","RowOrdinateId","ColumnOrdinateId","IsShaded") VALUES (1, 1, NULL, 10, 0)""");
        layouts.Exec(
            """INSERT INTO "LayoutCell" ("CellId","SheetId","RowOrdinateId","ColumnOrdinateId","IsShaded") VALUES (2, 1, NULL, 11, 0)""");

        var data = PlaneCLayoutReader.Read(layouts);

        Assert.Equal(2, data.Signatures.Count);
        Assert.All(data.Signatures, s => Assert.Contains("ZDIM(zval)", s.NormalizedTerms));
    }

    /// <summary>A shaded cell never enters the comparison: neither examined nor with a signature.</summary>
    [Fact]
    public void Read_ShadedCell_IsNotExamined()
    {
        using var layouts = PlaneCSyntheticDatabase.OpenLayouts();
        layouts.Exec("""INSERT INTO "LayoutFile" ("FileId","FrameworkCode","ReleaseLabel") VALUES (1, 'COREP', '4.2')""");
        layouts.Exec("""INSERT INTO "LayoutSheet" ("SheetId","FileId","TableCode") VALUES (1, 1, 'X_01.01')""");
        layouts.Exec(
            """INSERT INTO "LayoutDeclaration" ("DeclId","SheetId","DimensionCode","DomainCode","IsKey") VALUES (2, 1, NULL, 'MET', 0)""");
        layouts.Exec(
            """INSERT INTO "LayoutValue" ("ValueId","SheetId","DeclId","OrdinateId","MemberCode") VALUES (1, 1, 2, 10, 'qAA')""");
        layouts.Exec(
            """INSERT INTO "LayoutCell" ("CellId","SheetId","RowOrdinateId","ColumnOrdinateId","IsShaded") VALUES (1, 1, NULL, 10, 1)""");

        var data = PlaneCLayoutReader.Read(layouts);

        Assert.Empty(data.Signatures);
        Assert.Equal(0, data.NonShadedCellsExamined);
    }

    /// <summary>A non-shaded cell without any term (no row, no column, no sheet, no key) counts as an empty signature and is not compared.</summary>
    [Fact]
    public void Read_CellWithoutAnyTerm_CountsAsEmptySignature()
    {
        using var layouts = PlaneCSyntheticDatabase.OpenLayouts();
        layouts.Exec("""INSERT INTO "LayoutFile" ("FileId","FrameworkCode","ReleaseLabel") VALUES (1, 'COREP', '4.2')""");
        layouts.Exec("""INSERT INTO "LayoutSheet" ("SheetId","FileId","TableCode") VALUES (1, 1, 'X_01.01')""");
        layouts.Exec(
            """INSERT INTO "LayoutCell" ("CellId","SheetId","RowOrdinateId","ColumnOrdinateId","IsShaded") VALUES (1, 1, NULL, 10, 0)""");

        var data = PlaneCLayoutReader.Read(layouts);

        Assert.Empty(data.Signatures);
        Assert.Equal(1, data.NonShadedCellsExamined);
        Assert.Equal(1, data.CellsWithEmptySignature);
    }

    /// <summary>Declaration with a NULL <c>DimensionCode</c>: the dimension token falls back to the <c>DomainCode</c> (merged single-cell form).</summary>
    [Fact]
    public void Read_DeclarationWithoutDimensionCode_UsesDomainCodeAsTheDimensionToken()
    {
        using var layouts = PlaneCSyntheticDatabase.OpenLayouts();
        layouts.Exec("""INSERT INTO "LayoutFile" ("FileId","FrameworkCode","ReleaseLabel") VALUES (1, 'COREP', '4.2')""");
        layouts.Exec("""INSERT INTO "LayoutSheet" ("SheetId","FileId","TableCode") VALUES (1, 1, 'X_01.01')""");
        layouts.Exec(
            """INSERT INTO "LayoutDeclaration" ("DeclId","SheetId","DimensionCode","DomainCode","IsKey") VALUES (1, 1, NULL, 'qCAA', 0)""");
        layouts.Exec(
            """INSERT INTO "LayoutValue" ("ValueId","SheetId","DeclId","OrdinateId","MemberCode") VALUES (1, 1, 1, 10, 'qx2022')""");
        layouts.Exec(
            """INSERT INTO "LayoutCell" ("CellId","SheetId","RowOrdinateId","ColumnOrdinateId","IsShaded") VALUES (1, 1, NULL, 10, 0)""");

        var data = PlaneCLayoutReader.Read(layouts);

        var signature = Assert.Single(data.Signatures);
        Assert.Equal(["qCAA(qx2022)"], signature.NormalizedTerms);
    }
}
