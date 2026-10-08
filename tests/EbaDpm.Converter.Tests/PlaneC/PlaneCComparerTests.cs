using EbaDpm.Converter.Core.Validation.PlaneC;

namespace EbaDpm.Converter.Tests.PlaneC;

/// <summary>
/// Controls of <see cref="PlaneCComparer"/> on hand-built synthetic repositories -- the level where
/// a specific signature can be MUTATED and the exact effect measured, which the real corpus (787
/// tables) does not allow to isolate.
/// </summary>
public sealed class PlaneCComparerTests
{
    /// <summary>
    /// Negative-control baseline: layout and output match exactly -> 0 violations, 0 extra terms.
    /// Without this, the positive controls that follow would prove nothing (an instrument that
    /// always finds something is no better than one that never finds anything).
    /// </summary>
    [Fact]
    public void Compare_IdenticalSignatures_ZeroViolationsZeroExtraTerms()
    {
        using var generated = PlaneCSyntheticDatabase.OpenGenerated();
        using var layouts = PlaneCSyntheticDatabase.OpenLayouts();

        generated.InsertTableWithTaxonomy(1, "X_01.01", taxonomyId: 100, frameworkCode: "COREP", release: "4.2");
        generated.InsertCellDps(1, "MET(qAA)|DIM(m1)");

        BuildMatchingLayoutSheet(layouts, sheetId: 1, tableCode: "X_01.01", frameworkCode: "COREP", release: "4.2", metMember: "qAA", dimMember: "m1");

        var result = PlaneCComparer.Compare(generated, layouts);

        Assert.Empty(result.Violations);
        Assert.Empty(result.ExtraTerms);
        Assert.Equal(1, result.Coverage.SignaturesCompared);
        Assert.Equal(1, result.Coverage.SignaturesContained);
    }

    /// <summary>
    /// Positive control: mutating ONE member of ONE layout signature produces EXACTLY one new
    /// violation, of type <c>Contradicts</c> -- never <c>NoMatch</c>,
    /// because the mutated dimension STILL exists in our signatures (only the member changes).
    /// </summary>
    [Fact]
    public void Compare_MutatingOneMemberOfOneLayoutSignature_ProducesExactlyOneContradictsViolation()
    {
        using var generated = PlaneCSyntheticDatabase.OpenGenerated();
        using var layouts = PlaneCSyntheticDatabase.OpenLayouts();

        generated.InsertTableWithTaxonomy(1, "X_01.01", taxonomyId: 100, frameworkCode: "COREP", release: "4.2");
        generated.InsertCellDps(1, "MET(qAA)|DIM(m1)");
        generated.InsertCellDps(1, "MET(qBB)|DIM(m1)");

        // MUTATED layout: asks for DIM(m2), which neither of our two signatures carries -- but the
        // DIM dimension does exist in both, so there is a STRUCTURAL candidate -> Contradicts.
        BuildMatchingLayoutSheet(layouts, sheetId: 1, tableCode: "X_01.01", frameworkCode: "COREP", release: "4.2", metMember: "qAA", dimMember: "m2");

        var result = PlaneCComparer.Compare(generated, layouts);

        var violation = Assert.Single(result.Violations);
        Assert.Equal(PlaneCViolationType.Contradicts, violation.Type);
        Assert.Equal("X_01.01", violation.TableCode);
        Assert.Equal(0, result.Coverage.SignaturesContained);
        Assert.Equal(1, result.Coverage.SignaturesCompared);
    }

    /// <summary>
    /// The other kind of violation: if the dimension the layout mentions does NOT exist in ANY of
    /// our signatures (not even with another member), there is no structural candidate -> NoMatch.
    /// </summary>
    [Fact]
    public void Compare_DimensionAbsentFromAllOurSignatures_IsNoEqual()
    {
        using var generated = PlaneCSyntheticDatabase.OpenGenerated();
        using var layouts = PlaneCSyntheticDatabase.OpenLayouts();

        generated.InsertTableWithTaxonomy(1, "X_01.01", taxonomyId: 100, frameworkCode: "COREP", release: "4.2");
        generated.InsertCellDps(1, "MET(qAA)|DIM(m1)");

        layouts.Exec("""INSERT INTO "LayoutFile" ("FileId","FrameworkCode","ReleaseLabel") VALUES (1, 'COREP', '4.2')""");
        layouts.Exec("""INSERT INTO "LayoutSheet" ("SheetId","FileId","TableCode") VALUES (1, 1, 'X_01.01')""");
        layouts.Exec(
            """INSERT INTO "LayoutDeclaration" ("DeclId","SheetId","DimensionCode","DomainCode","IsKey") VALUES (1, 1, NULL, 'MET', 0)""");
        layouts.Exec(
            """INSERT INTO "LayoutValue" ("ValueId","SheetId","DeclId","OrdinateId","MemberCode") VALUES (1, 1, 1, 10, 'qAA')""");
        // ZZZ does not exist in ANY of our signatures in any form.
        layouts.Exec(
            """INSERT INTO "LayoutDeclaration" ("DeclId","SheetId","DimensionCode","DomainCode","IsKey") VALUES (2, 1, 'ZZZ', NULL, 0)""");
        layouts.Exec(
            """INSERT INTO "LayoutValue" ("ValueId","SheetId","DeclId","OrdinateId","MemberCode") VALUES (2, 1, 2, 10, 'zval')""");
        layouts.Exec(
            """INSERT INTO "LayoutCell" ("CellId","SheetId","RowOrdinateId","ColumnOrdinateId","IsShaded") VALUES (1, 1, NULL, 10, 0)""");

        var result = PlaneCComparer.Compare(generated, layouts);

        var violation = Assert.Single(result.Violations);
        Assert.Equal(PlaneCViolationType.NoMatch, violation.Type);
    }

    /// <summary>
    /// The valid control for the open-axis key rule, in the correct direction. Disabling that rule
    /// must NOT sink CONTAINMENT to 0%: under the containment criterion, removing a term from the
    /// layout only makes the signature EASIER to contain (subset monotonicity) -- never harder.
    ///
    /// The same table, compared against TWO layouts that differ only in whether they declare the
    /// open-axis key: WITH the declaration, the <c>OPENDIM(*)</c> term that the rule adds covers
    /// exactly the one our output already emits -> contained, 0 extra terms. WITHOUT it -- the
    /// phenomenon the rule exists to capture -- containment does not get worse (still 1/1), but
    /// <c>C-DPS-02</c> gains EXACTLY the sentinel term <c>OPENDIM(*)</c> for that table: the same
    /// effect that, on the real corpus, raises that informational check from 11 to 382 (371 new
    /// ones, one per (table, open-axis dimension)).
    /// </summary>
    [Fact]
    public void Compare_WithoutTheKeyDeclaration_ContainmentDoesNotWorsen_ButSentinelTermAppearsInDPS02()
    {
        using var generated = PlaneCSyntheticDatabase.OpenGenerated();
        // Our output ALWAYS carries the open-axis term: it is what Dpm20AxisAndCellLoader emits for
        // every cell of an IsOpenAxis=1 axis, regardless of what the layout shows.
        generated.InsertTableWithTaxonomy(1, "X_01.01", taxonomyId: 100, frameworkCode: "COREP", release: "4.2");
        generated.InsertCellDps(1, "MET(qAA)|OPENDIM(*)");

        using (var layoutsWithRule = PlaneCSyntheticDatabase.OpenLayouts())
        {
            BuildSheetWithOptionalKeyDeclaration(layoutsWithRule, metMember: "qAA", includeKeyDeclaration: true);

            var withRule = PlaneCComparer.Compare(generated, layoutsWithRule);

            Assert.Empty(withRule.Violations);
            Assert.Equal(1, withRule.Coverage.SignaturesContained);
            Assert.Empty(withRule.ExtraTerms);
        }

        using (var layoutsWithoutRule = PlaneCSyntheticDatabase.OpenLayouts())
        {
            BuildSheetWithOptionalKeyDeclaration(layoutsWithoutRule, metMember: "qAA", includeKeyDeclaration: false);

            var withoutRule = PlaneCComparer.Compare(generated, layoutsWithoutRule);

            // Containment does NOT get worse (still 1/1).
            Assert.Empty(withoutRule.Violations);
            Assert.Equal(1, withoutRule.Coverage.SignaturesContained);

            // And EXACTLY the sentinel appears: one term, that table, that dimension.
            var extra = Assert.Single(withoutRule.ExtraTerms);
            Assert.Equal("X_01.01", extra.TableCode);
            Assert.Equal("OPENDIM(*)", extra.Term);
        }
    }

    /// <summary>
    /// The unit of comparison is the table CODE, never the <c>TableID</c> -- a code with two
    /// <c>TableID</c>s (the same table reused in two taxonomies) is compared against the UNION of
    /// the signatures of both, not each one separately. Here the two halves of the code carry
    /// DIFFERENT signatures on purpose, and both must come out contained.
    /// </summary>
    [Fact]
    public void Compare_SameCodeInTwoTableIds_UnitesTheSignaturesOfBoth()
    {
        using var generated = PlaneCSyntheticDatabase.OpenGenerated();
        using var layouts = PlaneCSyntheticDatabase.OpenLayouts();

        generated.InsertTableWithTaxonomy(1, "Y_01.01", taxonomyId: 100, frameworkCode: "COREP", release: "4.2");
        generated.InsertTableWithTaxonomy(2, "Y_01.01", taxonomyId: 200, frameworkCode: "COREP", release: "4.2");
        generated.InsertCellDps(1, "MET(qAA)|DIM(m1)");
        generated.InsertCellDps(2, "MET(qBB)|DIM(m1)");

        layouts.Exec("""INSERT INTO "LayoutFile" ("FileId","FrameworkCode","ReleaseLabel") VALUES (1, 'COREP', '4.2')""");
        layouts.Exec("""INSERT INTO "LayoutSheet" ("SheetId","FileId","TableCode") VALUES (1, 1, 'Y_01.01')""");
        layouts.Exec(
            """INSERT INTO "LayoutDeclaration" ("DeclId","SheetId","DimensionCode","DomainCode","IsKey") VALUES (1, 1, NULL, 'MET', 0)""");
        layouts.Exec(
            """INSERT INTO "LayoutDeclaration" ("DeclId","SheetId","DimensionCode","DomainCode","IsKey") VALUES (2, 1, 'DIM', NULL, 0)""");
        // Cell 1 asks for the signature of TableID=1 (qAA); cell 2 asks for that of TableID=2
        // (qBB). Both can only be contained if the union of signatures covers both TableIDs.
        layouts.Exec(
            """INSERT INTO "LayoutValue" ("ValueId","SheetId","DeclId","OrdinateId","MemberCode") VALUES (1, 1, 1, 10, 'qAA')""");
        layouts.Exec(
            """INSERT INTO "LayoutValue" ("ValueId","SheetId","DeclId","OrdinateId","MemberCode") VALUES (2, 1, 2, 10, 'm1')""");
        layouts.Exec(
            """INSERT INTO "LayoutValue" ("ValueId","SheetId","DeclId","OrdinateId","MemberCode") VALUES (3, 1, 1, 11, 'qBB')""");
        layouts.Exec(
            """INSERT INTO "LayoutValue" ("ValueId","SheetId","DeclId","OrdinateId","MemberCode") VALUES (4, 1, 2, 11, 'm1')""");
        layouts.Exec(
            """INSERT INTO "LayoutCell" ("CellId","SheetId","RowOrdinateId","ColumnOrdinateId","IsShaded") VALUES (1, 1, NULL, 10, 0)""");
        layouts.Exec(
            """INSERT INTO "LayoutCell" ("CellId","SheetId","RowOrdinateId","ColumnOrdinateId","IsShaded") VALUES (2, 1, NULL, 11, 0)""");

        var result = PlaneCComparer.Compare(generated, layouts);

        Assert.Empty(result.Violations);
        Assert.Equal(2, result.Coverage.SignaturesCompared);
        Assert.Equal(2, result.Coverage.SignaturesContained);
        // It is compared ONCE as a table (by code), not twice -- no repeated "Y_01.01" entry.
        Assert.Equal(1, result.Coverage.TablesCompared);
        Assert.Equal(1, result.Coverage.TableCodesWithMultipleTableIds);
        // The signatures of the two TableIDs are DIFFERENT on purpose -> they do not count as identical.
        Assert.Equal(0, result.Coverage.TableCodesWithMultipleTableIdsAndIdenticalSignatures);
    }

    /// <summary>
    /// A layout hotfix/point release (<c>REL.n</c>, e.g. <c>4.2.1</c>) counts as the table's own
    /// release in <c>REL</c> (<c>4.2</c>) -- and it is reported BY NAME, never silently
    /// (<see cref="EbaDpm.Converter.Core.Validation.PlaneC.PlaneCCoverage.SheetsMatchedByPointReleaseTolerance"/>).
    /// </summary>
    [Fact]
    public void Compare_LayoutSheetWithPointRelease_CountsAsOwnAndIsReportedByName()
    {
        using var generated = PlaneCSyntheticDatabase.OpenGenerated();
        using var layouts = PlaneCSyntheticDatabase.OpenLayouts();

        generated.InsertTableWithTaxonomy(1, "Z_01.01", taxonomyId: 100, frameworkCode: "FINREP", release: "4.2");
        generated.InsertCellDps(1, "MET(qAA)");

        layouts.Exec("""INSERT INTO "LayoutFile" ("FileId","FrameworkCode","ReleaseLabel") VALUES (1, 'FINREP', '4.2.1')""");
        layouts.Exec("""INSERT INTO "LayoutSheet" ("SheetId","FileId","TableCode") VALUES (1, 1, 'Z_01.01')""");
        layouts.Exec(
            """INSERT INTO "LayoutDeclaration" ("DeclId","SheetId","DimensionCode","DomainCode","IsKey") VALUES (1, 1, NULL, 'MET', 0)""");
        layouts.Exec(
            """INSERT INTO "LayoutValue" ("ValueId","SheetId","DeclId","OrdinateId","MemberCode") VALUES (1, 1, 1, 10, 'qAA')""");
        layouts.Exec(
            """INSERT INTO "LayoutCell" ("CellId","SheetId","RowOrdinateId","ColumnOrdinateId","IsShaded") VALUES (1, 1, NULL, 10, 0)""");

        var result = PlaneCComparer.Compare(generated, layouts);

        Assert.Empty(result.Violations);
        Assert.Equal(1, result.Coverage.TablesCompared);
        Assert.Equal(1, result.Coverage.SheetsMatchedByPointReleaseTolerance);
        Assert.Contains(result.Coverage.PointReleaseToleranceSamples, s => s.Contains("Z_01.01", StringComparison.Ordinal));
    }

    /// <summary>Contrast with the previous test: an EXACT release (not a point release) does NOT count towards the tolerance.</summary>
    [Fact]
    public void Compare_LayoutSheetWithExactRelease_DoesNotCountTowardsTheTolerance()
    {
        using var generated = PlaneCSyntheticDatabase.OpenGenerated();
        using var layouts = PlaneCSyntheticDatabase.OpenLayouts();

        generated.InsertTableWithTaxonomy(1, "Z_01.01", taxonomyId: 100, frameworkCode: "FINREP", release: "4.2");
        generated.InsertCellDps(1, "MET(qAA)");

        BuildMatchingLayoutSheet(layouts, sheetId: 1, tableCode: "Z_01.01", frameworkCode: "FINREP", release: "4.2", metMember: "qAA", dimMember: null);

        var result = PlaneCComparer.Compare(generated, layouts);

        Assert.Equal(0, result.Coverage.SheetsMatchedByPointReleaseTolerance);
        Assert.Empty(result.Coverage.PointReleaseToleranceSamples);
    }

    /// <summary>A table of a release/framework that the layout does not cover at all is counted as excluded, not as a violation.</summary>
    [Fact]
    public void Compare_NoSheetOfTheSameRelease_IsCountedAsExcludedNotAsViolation()
    {
        using var generated = PlaneCSyntheticDatabase.OpenGenerated();
        using var layouts = PlaneCSyntheticDatabase.OpenLayouts();

        generated.InsertTableWithTaxonomy(1, "Z_01.01", taxonomyId: 100, frameworkCode: "FINREP", release: "4.3");
        generated.InsertCellDps(1, "MET(qAA)");

        // The layout only has the sheet of release 4.2 -- it cannot be matched with a 4.3 table.
        BuildMatchingLayoutSheet(layouts, sheetId: 1, tableCode: "Z_01.01", frameworkCode: "FINREP", release: "4.2", metMember: "qAA", dimMember: null);

        var result = PlaneCComparer.Compare(generated, layouts);

        Assert.Empty(result.Violations);
        Assert.Equal(0, result.Coverage.TablesCompared);
        Assert.Equal(1, result.Coverage.TablesSkippedNoMatchingLayoutSheet);
    }

    /// <summary>
    /// Positive control: a signature whose ONLY metric term <c>MET(qFAB)</c> names a dimension that
    /// the signature itself carries as the open axis <c>qFAB(*)</c> -- exactly the shape of
    /// <c>N_02.00</c> in the real layout -- is recognized as a key column, is EXCLUDED from the
    /// compared population (it counts neither as contained nor as a violation) and is NAMED in
    /// <c>C-KEY-01</c>.
    /// </summary>
    [Fact]
    public void Compare_SignatureWithMetAndItsOwnDimensionAsOpenAxis_IsKeyColumn_AndIsExcluded()
    {
        using var generated = PlaneCSyntheticDatabase.OpenGenerated();
        using var layouts = PlaneCSyntheticDatabase.OpenLayouts();

        // The real output NEVER emits "MET(qFAB)" (qFAB is the key of the open axis, not a real
        // metric) -- it is left without any cell that could match, like the real case.
        generated.InsertTableWithTaxonomy(1, "N_TEST", taxonomyId: 100, frameworkCode: "COREP", release: "4.2");
        generated.InsertCellDps(1, "MET(qBBU)|qFAB(*)|DIM(m1)"); // a normal datapoint of that table.

        BuildKeyColumnSheet(layouts, sheetId: 1, tableCode: "N_TEST", frameworkCode: "COREP", release: "4.2", keyDimension: "qFAB");

        var result = PlaneCComparer.Compare(generated, layouts);

        Assert.Equal(1, result.Coverage.SignaturesExcludedKeyColumn);
        Assert.Contains(result.Coverage.SignaturesExcludedKeyColumnSamples, s => s.StartsWith("N_TEST", StringComparison.Ordinal));
        Assert.Empty(result.Violations);
        // The key signature is NOT counted as compared nor as contained -- it is left out entirely.
        Assert.Equal(0, result.Coverage.SignaturesCompared);
        Assert.Equal(0, result.Coverage.SignaturesContained);
    }

    /// <summary>
    /// Negative control (half 1 of 2): the "open axis" half is missing -- there is
    /// <c>MET(qFAB)</c> but the signature does NOT carry <c>qFAB(*)</c> -- the rule must NOT apply.
    /// The signature goes into NORMAL comparison (and, since our output DOES carry the MET
    /// dimension -- with ANOTHER member, <c>qBBU</c> -- but never exactly "MET(qFAB)", it comes out
    /// <c>Contradicts</c>: there is a structural candidate in that dimension, only with another
    /// member). What matters here is NOT the type -- it is that <c>SignaturesExcludedKeyColumn</c>
    /// stays at 0, never silently excluded.
    /// </summary>
    [Fact]
    public void Compare_OnlyMetWithoutTheOpenAxisTerm_IsNotKeyColumn()
    {
        using var generated = PlaneCSyntheticDatabase.OpenGenerated();
        using var layouts = PlaneCSyntheticDatabase.OpenLayouts();

        generated.InsertTableWithTaxonomy(1, "N_TEST", taxonomyId: 100, frameworkCode: "COREP", release: "4.2");
        generated.InsertCellDps(1, "MET(qBBU)|DIM(m1)");

        layouts.Exec("""INSERT INTO "LayoutFile" ("FileId","FrameworkCode","ReleaseLabel") VALUES (1, 'COREP', '4.2')""");
        layouts.Exec("""INSERT INTO "LayoutSheet" ("SheetId","FileId","TableCode") VALUES (1, 1, 'N_TEST')""");
        layouts.Exec(
            """INSERT INTO "LayoutDeclaration" ("DeclId","SheetId","DimensionCode","DomainCode","IsKey") VALUES (1, 1, NULL, 'MET', 0)""");
        layouts.Exec(
            """INSERT INTO "LayoutValue" ("ValueId","SheetId","DeclId","OrdinateId","MemberCode") VALUES (1, 1, 1, 10, 'qFAB')""");
        // WITHOUT an IsKey=1 declaration of qFAB -- there is no "qFAB(*)" term for the key rule to add.
        layouts.Exec(
            """INSERT INTO "LayoutCell" ("CellId","SheetId","RowOrdinateId","ColumnOrdinateId","IsShaded") VALUES (1, 1, NULL, 10, 0)""");

        var result = PlaneCComparer.Compare(generated, layouts);

        Assert.Equal(0, result.Coverage.SignaturesExcludedKeyColumn);
        Assert.Equal(1, result.Coverage.SignaturesCompared);
        var violation = Assert.Single(result.Violations);
        Assert.Equal(PlaneCViolationType.Contradicts, violation.Type);
    }

    /// <summary>
    /// Negative control (half 2 of 2): the "MET" half is missing -- the signature carries
    /// <c>qFAB(*)</c> (IsKey=1 declaration) but NO <c>MET(...)</c> term -- the rule must NOT apply
    /// (0 metric terms, defensive).
    /// </summary>
    [Fact]
    public void Compare_OnlyOpenAxisWithoutAnyMetTerm_IsNotKeyColumn()
    {
        using var generated = PlaneCSyntheticDatabase.OpenGenerated();
        using var layouts = PlaneCSyntheticDatabase.OpenLayouts();

        generated.InsertTableWithTaxonomy(1, "N_TEST", taxonomyId: 100, frameworkCode: "COREP", release: "4.2");
        generated.InsertCellDps(1, "DIM(m1)|qFAB(*)"); // It does exist in the output -- so that "contained" is reachable.

        layouts.Exec("""INSERT INTO "LayoutFile" ("FileId","FrameworkCode","ReleaseLabel") VALUES (1, 'COREP', '4.2')""");
        layouts.Exec("""INSERT INTO "LayoutSheet" ("SheetId","FileId","TableCode") VALUES (1, 1, 'N_TEST')""");
        layouts.Exec(
            """INSERT INTO "LayoutDeclaration" ("DeclId","SheetId","DimensionCode","DomainCode","IsKey") VALUES (1, 1, NULL, 'DIM', 0)""");
        layouts.Exec(
            """INSERT INTO "LayoutValue" ("ValueId","SheetId","DeclId","OrdinateId","MemberCode") VALUES (1, 1, 1, 10, 'm1')""");
        layouts.Exec(
            """INSERT INTO "LayoutDeclaration" ("DeclId","SheetId","DimensionCode","DomainCode","IsKey") VALUES (2, 1, 'qFAB', NULL, 1)""");
        layouts.Exec(
            """INSERT INTO "LayoutCell" ("CellId","SheetId","RowOrdinateId","ColumnOrdinateId","IsShaded") VALUES (1, 1, NULL, 10, 0)""");

        var result = PlaneCComparer.Compare(generated, layouts);

        // 0 metric terms -> the rule does NOT apply -- it goes to normal comparison, and it IS
        // contained (our cell carries exactly DIM(m1)|qFAB(*)).
        Assert.Equal(0, result.Coverage.SignaturesExcludedKeyColumn);
        Assert.Equal(1, result.Coverage.SignaturesCompared);
        Assert.Equal(1, result.Coverage.SignaturesContained);
        Assert.Empty(result.Violations);
    }

    /// <summary>
    /// Negative control (shape): TWO <c>MET(...)</c> terms in the same signature -- the rule
    /// requires a "single metric term" and with more than one it must NOT apply, even if one of
    /// the two matches the dimension of the open axis.
    /// </summary>
    [Fact]
    public void Compare_TwoMetTerms_IsNotKeyColumn_EvenIfOneMatchesTheOpenAxis()
    {
        using var generated = PlaneCSyntheticDatabase.OpenGenerated();
        using var layouts = PlaneCSyntheticDatabase.OpenLayouts();

        generated.InsertTableWithTaxonomy(1, "N_TEST", taxonomyId: 100, frameworkCode: "COREP", release: "4.2");
        generated.InsertCellDps(1, "MET(qFAB)|MET(other)|qFAB(*)"); // exists in the output -- so it shows that it IS compared.

        layouts.Exec("""INSERT INTO "LayoutFile" ("FileId","FrameworkCode","ReleaseLabel") VALUES (1, 'COREP', '4.2')""");
        layouts.Exec("""INSERT INTO "LayoutSheet" ("SheetId","FileId","TableCode") VALUES (1, 1, 'N_TEST')""");
        // Two MET declarations, one per axis (row and column), both with DomainCode='MET'.
        layouts.Exec(
            """INSERT INTO "LayoutDeclaration" ("DeclId","SheetId","DimensionCode","DomainCode","IsKey") VALUES (1, 1, NULL, 'MET', 0)""");
        layouts.Exec(
            """INSERT INTO "LayoutValue" ("ValueId","SheetId","DeclId","OrdinateId","MemberCode") VALUES (1, 1, 1, 5, 'qFAB')""");
        layouts.Exec(
            """INSERT INTO "LayoutDeclaration" ("DeclId","SheetId","DimensionCode","DomainCode","IsKey") VALUES (2, 1, NULL, 'MET', 0)""");
        layouts.Exec(
            """INSERT INTO "LayoutValue" ("ValueId","SheetId","DeclId","OrdinateId","MemberCode") VALUES (2, 1, 2, 10, 'other')""");
        layouts.Exec(
            """INSERT INTO "LayoutDeclaration" ("DeclId","SheetId","DimensionCode","DomainCode","IsKey") VALUES (3, 1, 'qFAB', NULL, 1)""");
        layouts.Exec(
            """INSERT INTO "LayoutCell" ("CellId","SheetId","RowOrdinateId","ColumnOrdinateId","IsShaded") VALUES (1, 1, 5, 10, 0)""");

        var result = PlaneCComparer.Compare(generated, layouts);

        Assert.Equal(0, result.Coverage.SignaturesExcludedKeyColumn);
        Assert.Equal(1, result.Coverage.SignaturesCompared);
        Assert.Equal(1, result.Coverage.SignaturesContained);
        Assert.Empty(result.Violations);
    }

    /// <summary>A sheet with MET(qFAB) + an IsKey=1 declaration of the same dimension qFAB, on ONE column ordinate -- the exact key-column shape.</summary>
    private static void BuildKeyColumnSheet(
        Microsoft.Data.Sqlite.SqliteConnection layouts, long sheetId, string tableCode, string frameworkCode, string release, string keyDimension)
    {
        layouts.Exec($"""INSERT INTO "LayoutFile" ("FileId","FrameworkCode","ReleaseLabel") VALUES ({sheetId}, '{frameworkCode}', '{release}')""");
        layouts.Exec($"""INSERT INTO "LayoutSheet" ("SheetId","FileId","TableCode") VALUES ({sheetId}, {sheetId}, '{tableCode}')""");
        layouts.Exec(
            $"""INSERT INTO "LayoutDeclaration" ("DeclId","SheetId","DimensionCode","DomainCode","IsKey") VALUES ({sheetId * 10 + 1}, {sheetId}, NULL, 'MET', 0)""");
        layouts.Exec(
            $"""INSERT INTO "LayoutValue" ("ValueId","SheetId","DeclId","OrdinateId","MemberCode") VALUES ({sheetId * 10 + 1}, {sheetId}, {sheetId * 10 + 1}, {sheetId * 100 + 10}, '{keyDimension}')""");
        layouts.Exec(
            $"""INSERT INTO "LayoutDeclaration" ("DeclId","SheetId","DimensionCode","DomainCode","IsKey") VALUES ({sheetId * 10 + 2}, {sheetId}, '{keyDimension}', NULL, 1)""");
        layouts.Exec(
            $"""INSERT INTO "LayoutCell" ("CellId","SheetId","RowOrdinateId","ColumnOrdinateId","IsShaded") VALUES ({sheetId}, {sheetId}, NULL, {sheetId * 100 + 10}, 0)""");
    }

    private static void BuildMatchingLayoutSheet(
        Microsoft.Data.Sqlite.SqliteConnection layouts, long sheetId, string tableCode, string frameworkCode, string release,
        string metMember, string? dimMember)
    {
        layouts.Exec($"""INSERT INTO "LayoutFile" ("FileId","FrameworkCode","ReleaseLabel") VALUES ({sheetId}, '{frameworkCode}', '{release}')""");
        layouts.Exec($"""INSERT INTO "LayoutSheet" ("SheetId","FileId","TableCode") VALUES ({sheetId}, {sheetId}, '{tableCode}')""");
        layouts.Exec(
            $"""INSERT INTO "LayoutDeclaration" ("DeclId","SheetId","DimensionCode","DomainCode","IsKey") VALUES ({sheetId * 10 + 1}, {sheetId}, NULL, 'MET', 0)""");
        layouts.Exec(
            $"""INSERT INTO "LayoutValue" ("ValueId","SheetId","DeclId","OrdinateId","MemberCode") VALUES ({sheetId * 10 + 1}, {sheetId}, {sheetId * 10 + 1}, {sheetId * 100 + 10}, '{metMember}')""");

        if (dimMember is not null)
        {
            layouts.Exec(
                $"""INSERT INTO "LayoutDeclaration" ("DeclId","SheetId","DimensionCode","DomainCode","IsKey") VALUES ({sheetId * 10 + 2}, {sheetId}, 'DIM', NULL, 0)""");
            layouts.Exec(
                $"""INSERT INTO "LayoutValue" ("ValueId","SheetId","DeclId","OrdinateId","MemberCode") VALUES ({sheetId * 10 + 2}, {sheetId}, {sheetId * 10 + 2}, {sheetId * 100 + 10}, '{dimMember}')""");
        }

        layouts.Exec(
            $"""INSERT INTO "LayoutCell" ("CellId","SheetId","RowOrdinateId","ColumnOrdinateId","IsShaded") VALUES ({sheetId}, {sheetId}, NULL, {sheetId * 100 + 10}, 0)""");
    }

    private static void BuildSheetWithOptionalKeyDeclaration(
        Microsoft.Data.Sqlite.SqliteConnection layouts, string metMember, bool includeKeyDeclaration)
    {
        layouts.Exec("""INSERT INTO "LayoutFile" ("FileId","FrameworkCode","ReleaseLabel") VALUES (1, 'COREP', '4.2')""");
        layouts.Exec("""INSERT INTO "LayoutSheet" ("SheetId","FileId","TableCode") VALUES (1, 1, 'X_01.01')""");
        layouts.Exec(
            """INSERT INTO "LayoutDeclaration" ("DeclId","SheetId","DimensionCode","DomainCode","IsKey") VALUES (1, 1, NULL, 'MET', 0)""");
        layouts.Exec(
            $"""INSERT INTO "LayoutValue" ("ValueId","SheetId","DeclId","OrdinateId","MemberCode") VALUES (1, 1, 1, 10, '{metMember}')""");

        if (includeKeyDeclaration)
        {
            layouts.Exec(
                """INSERT INTO "LayoutDeclaration" ("DeclId","SheetId","DimensionCode","DomainCode","IsKey") VALUES (2, 1, 'OPENDIM', NULL, 1)""");
        }

        layouts.Exec(
            """INSERT INTO "LayoutCell" ("CellId","SheetId","RowOrdinateId","ColumnOrdinateId","IsShaded") VALUES (1, 1, NULL, 10, 0)""");
    }
}
