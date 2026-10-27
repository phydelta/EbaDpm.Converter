using EbaDpm.Converter.Core.Validation.PlaneC;
using Microsoft.Data.Sqlite;

namespace EbaDpm.Converter.Tests.PlaneC;

/// <summary>
/// Synthetic controls of the per-sheet TableID pairing shared by <see cref="PlaneCComparer"/>
/// (C-COB-01 / C-DPS-01) and <see cref="PlaneCEqualityChecker"/> (C-EQU-01):
/// <see cref="PlaneCReleaseMatcher.SelectTableIds"/> (exact pairings win over the REL.n -> REL
/// tolerance), plus the consequences for both consumers once a code lives in a taxonomy of release
/// REL and another of release REL.n of the same framework (module-start-release derivation, #66).
/// Every "does not pair" / "is not ambiguous" assertion has a twin that proves the instrument can
/// see the opposite outcome.
/// </summary>
public sealed class PlaneCReleasePrecedenceTests
{
    // ---------------------------------------------------------------- SelectTableIds

    private static Dictionary<long, HashSet<(string Framework, string Release)>> Keys(
        params (long TableId, string Framework, string Release)[] entries)
    {
        var result = new Dictionary<long, HashSet<(string Framework, string Release)>>();
        foreach (var (id, fw, rel) in entries)
        {
            if (!result.TryGetValue(id, out var set))
            {
                set = [];
                result[id] = set;
            }

            set.Add((fw, rel));
        }

        return result;
    }

    [Fact]
    public void SelectTableIds_OnlyExactPairings_UsesThemWithMatchExact()
    {
        var keys = Keys((1, "FINREP", "4.2"), (2, "FINREP", "4.3"));

        var selection = PlaneCReleaseMatcher.SelectTableIds([1L, 2L], keys, "FINREP", "4.2");

        Assert.Equal(PlaneCReleaseMatch.Exact, selection.Match);
        Assert.Equal([1L], selection.Used);
    }

    [Fact]
    public void SelectTableIds_ExactAndPointReleaseBothPair_OnlyTheExactOneIsUsed()
    {
        // TableID 1 is release 4.2 (pairs with layout 4.2.1 only through the tolerance), TableID 2
        // is release 4.2.1 (pairs exactly). The tolerance table must NOT be used.
        var keys = Keys((1, "FINREP", "4.2"), (2, "FINREP", "4.2.1"));

        var selection = PlaneCReleaseMatcher.SelectTableIds([1L, 2L], keys, "FINREP", "4.2.1");

        Assert.Equal(PlaneCReleaseMatch.Exact, selection.Match);
        Assert.Equal([2L], selection.Used);
    }

    [Fact]
    public void SelectTableIds_PositiveControl_TheSameLayoutOfRelease42SeesOnlyTheRelease42TableId()
    {
        // Twin of the previous test: the 4.2 layout pairs exactly with TableID 1, and 4.2.1 is not
        // tolerated for a 4.2 layout (the tolerance goes REL.n layout -> REL table only).
        var keys = Keys((1, "FINREP", "4.2"), (2, "FINREP", "4.2.1"));

        var selection = PlaneCReleaseMatcher.SelectTableIds([1L, 2L], keys, "FINREP", "4.2");

        Assert.Equal(PlaneCReleaseMatch.Exact, selection.Match);
        Assert.Equal([1L], selection.Used);
    }

    [Fact]
    public void SelectTableIds_OnlyPointRelease_IsUsedWithMatchPointRelease()
    {
        var keys = Keys((1, "FINREP", "4.2"), (2, "FINREP", "4.3"));

        var selection = PlaneCReleaseMatcher.SelectTableIds([1L, 2L], keys, "FINREP", "4.2.1");

        Assert.Equal(PlaneCReleaseMatch.PointRelease, selection.Match);
        Assert.Equal([1L], selection.Used);
    }

    [Fact]
    public void SelectTableIds_NothingPairs_IsNoneAndUsedIsEmpty()
    {
        var keys = Keys((1, "FINREP", "4.2"), (2, "COREP", "4.2.1"), (3, "FINREP", "4.3"));

        var none = PlaneCReleaseMatcher.SelectTableIds([1L, 2L, 3L], keys, "FINREP", "4.9");
        var otherFramework = PlaneCReleaseMatcher.SelectTableIds([1L, 3L], keys, "COREP", "4.2.1");
        var unknownTableId = PlaneCReleaseMatcher.SelectTableIds([99L], keys, "FINREP", "4.2");

        Assert.Equal(PlaneCReleaseMatch.None, none.Match);
        Assert.Empty(none.Used);
        Assert.Equal(PlaneCReleaseMatch.None, otherFramework.Match);
        Assert.Empty(otherFramework.Used);
        Assert.Equal(PlaneCReleaseMatch.None, unknownTableId.Match);
        Assert.Empty(unknownTableId.Used);

        // Positive control: the same dictionary does pair when the TableID and framework are right.
        var control = PlaneCReleaseMatcher.SelectTableIds([2L], keys, "COREP", "4.2.1");
        Assert.Equal(PlaneCReleaseMatch.Exact, control.Match);
        Assert.Equal([2L], control.Used);
    }

    // ---------------------------------------------------------------- C-EQU-01

    private static void InsertEquTable(
        SqliteConnection generated, long tableId, string tableCode, string release, string signature)
    {
        var isA = tableCode.StartsWith('A');
        generated.InsertTable(tableId, tableCode, release: release);
        generated.InsertAxis(tableId * 10, tableId, "Y", isOpenAxis: false, (tableId * 100, isA ? "0010" : "0030"));
        generated.InsertAxis(tableId * 10 + 1, tableId, "X", isOpenAxis: false, (tableId * 100 + 1, isA ? "0020" : "0040"));
        generated.InsertCell(tableId * 1000, tableId, signature, isShaded: false, tableId * 100, tableId * 100 + 1);
    }

    private static void InsertEquRule(SqliteConnection layouts, string release)
    {
        layouts.InsertRuleTerm(1, 0, "A_01.01, r0010, c0020", "A_01.01", "0010", "0020", null, release: release);
        layouts.InsertRuleTerm(1, 1, "B_02.01, r0030, c0040", "B_02.01", "0030", "0040", null, release: release);
    }

    [Fact]
    public void CEqu01_CodeInBothRelAndRelNTaxonomies_RelNSheetResolvesAndIsNotAmbiguous()
    {
        using var generated = PlaneCEqualitySyntheticDatabase.OpenGenerated();
        using var layouts = PlaneCEqualitySyntheticDatabase.OpenLayouts();

        // A_01.01 lives in 4.2 (signature WRONG) and in 4.2.1 (signature SIG1); B_02.01 in 4.2.1.
        InsertEquTable(generated, 1, "A_01.01", "4.2", "WRONG");
        InsertEquTable(generated, 3, "A_01.01", "4.2.1", "SIG1");
        InsertEquTable(generated, 2, "B_02.01", "4.2.1", "SIG1");
        InsertEquRule(layouts, release: "4.2.1");

        var result = PlaneCEqualityChecker.Check(generated, layouts);

        // Resolved from the exact 4.2.1 TableID only: checkable, identical (WRONG never read),
        // and NOT flagged as a tolerance pairing.
        Assert.Equal(1, result.Coverage.RulesCheckable);
        Assert.Equal(1, result.Coverage.RulesIdentical);
        Assert.Equal(0, result.Coverage.RulesDivergent);
        Assert.False(result.Coverage.NonCheckableByReason.ContainsKey("ambiguous cell"), "ambiguous cell reported");
        Assert.Equal(0, result.Coverage.SheetsMatchedByPointReleaseTolerance);
        Assert.Empty(result.Coverage.PointReleaseToleranceSamples);
    }

    [Fact]
    public void CEqu01_CodeInBothRelAndRelNTaxonomies_ExactTableIdDivergenceIsStillDetected()
    {
        // Positive control of the test above: the exact TableID is genuinely the one read. Giving
        // it a signature different from B's turns the rule into a divergence; the 4.2 table
        // (which carries B's signature) is not used to hide it.
        using var generated = PlaneCEqualitySyntheticDatabase.OpenGenerated();
        using var layouts = PlaneCEqualitySyntheticDatabase.OpenLayouts();

        InsertEquTable(generated, 1, "A_01.01", "4.2", "SIG1");
        InsertEquTable(generated, 3, "A_01.01", "4.2.1", "WRONG");
        InsertEquTable(generated, 2, "B_02.01", "4.2.1", "SIG1");
        InsertEquRule(layouts, release: "4.2.1");

        var result = PlaneCEqualityChecker.Check(generated, layouts);

        Assert.Equal(1, result.Coverage.RulesCheckable);
        Assert.Equal(1, result.Coverage.RulesDivergent);
        Assert.Equal(0, result.Coverage.RulesIdentical);
        Assert.Single(result.Violations);
        Assert.False(result.Coverage.NonCheckableByReason.ContainsKey("ambiguous cell"));
    }

    [Fact]
    public void CEqu01_OnlyTheRelTaxonomyExists_RelNSheetResolvesThroughToleranceAndIsFlagged()
    {
        using var generated = PlaneCEqualitySyntheticDatabase.OpenGenerated();
        using var layouts = PlaneCEqualitySyntheticDatabase.OpenLayouts();

        InsertEquTable(generated, 1, "A_01.01", "4.2", "SIG1");
        InsertEquTable(generated, 2, "B_02.01", "4.2", "SIG1");
        InsertEquRule(layouts, release: "4.2.1");

        var result = PlaneCEqualityChecker.Check(generated, layouts);

        Assert.Equal(1, result.Coverage.RulesCheckable);
        Assert.Equal(1, result.Coverage.RulesIdentical);
        Assert.False(result.Coverage.NonCheckableByReason.ContainsKey("ambiguous cell"));
        Assert.Equal(1, result.Coverage.SheetsMatchedByPointReleaseTolerance);
        Assert.NotEmpty(result.Coverage.PointReleaseToleranceSamples);
    }

    // ---------------------------------------------------------------- PlaneCComparer

    /// <summary>Inserts one layout sheet (file id = sheet id) with one cell per member in
    /// <paramref name="metMembers"/>, each cell carrying signature MET(member).</summary>
    private static void InsertSheet(
        SqliteConnection layouts, long sheetId, string tableCode, string framework, string release, params string[] metMembers)
    {
        layouts.Exec($"""INSERT INTO "LayoutFile" ("FileId","FrameworkCode","ReleaseLabel") VALUES ({sheetId}, '{framework}', '{release}')""");
        layouts.Exec($"""INSERT INTO "LayoutSheet" ("SheetId","FileId","TableCode") VALUES ({sheetId}, {sheetId}, '{tableCode}')""");
        for (var i = 0; i < metMembers.Length; i++)
        {
            var n = sheetId * 100 + i + 1;
            layouts.Exec($"""INSERT INTO "LayoutDeclaration" ("DeclId","SheetId","DimensionCode","DomainCode","IsKey") VALUES ({n}, {sheetId}, NULL, 'MET', 0)""");
            layouts.Exec($"""INSERT INTO "LayoutValue" ("ValueId","SheetId","DeclId","OrdinateId","MemberCode") VALUES ({n}, {sheetId}, {n}, {n}, '{metMembers[i]}')""");
            layouts.Exec($"""INSERT INTO "LayoutCell" ("CellId","SheetId","RowOrdinateId","ColumnOrdinateId","IsShaded") VALUES ({n}, {sheetId}, NULL, {n}, 0)""");
        }
    }

    private static void InsertOutputTable(
        SqliteConnection generated, long tableId, string release, params string[] metMembers)
    {
        generated.InsertTableWithTaxonomy(tableId, "X_01.01", taxonomyId: tableId * 10, frameworkCode: "COREP", release: release);
        foreach (var m in metMembers)
        {
            generated.InsertCellDps(tableId, $"MET({m})");
        }
    }

    [Fact]
    public void Compare_SignatureOnlyInANonPairingTableId_IsNotContained_AndTheSameWorldWithPairingIs()
    {
        // TableID 1: COREP 4.2 {qAA}. TableID 2: COREP 4.3 {qBB}.
        using var generated = PlaneCSyntheticDatabase.OpenGenerated();
        InsertOutputTable(generated, 1, "4.2", "qAA");
        InsertOutputTable(generated, 2, "4.3", "qBB");

        // The layout sheet is COREP 4.2 and asks for qBB: TableID 2 does not pair -> violation.
        using (var layouts = PlaneCSyntheticDatabase.OpenLayouts())
        {
            InsertSheet(layouts, 1, "X_01.01", "COREP", "4.2", "qBB");
            var result = PlaneCComparer.Compare(generated, layouts);

            var violation = Assert.Single(result.Violations);
            Assert.Equal("X_01.01", violation.TableCode);
            Assert.Equal(["COREP 4.2"], violation.FailedSheetReleases);
            Assert.Equal(1, result.Coverage.SignaturesCompared);
            Assert.Equal(0, result.Coverage.SignaturesContained);
        }

        // Positive control: the same world, but the layout sheet is COREP 4.3, so TableID 2 pairs.
        using (var layouts = PlaneCSyntheticDatabase.OpenLayouts())
        {
            InsertSheet(layouts, 1, "X_01.01", "COREP", "4.3", "qBB");
            var result = PlaneCComparer.Compare(generated, layouts);

            Assert.Empty(result.Violations);
            Assert.Equal(1, result.Coverage.SignaturesCompared);
            Assert.Equal(1, result.Coverage.SignaturesContained);
        }
    }

    [Fact]
    public void Compare_PointReleaseSheet_SignatureOnlyInTheToleranceTableId_IsNotContainedWhenAnExactOneExists()
    {
        // TableID 1: COREP 4.2 {qAA} (tolerance-only for a 4.2.1 sheet). TableID 2: COREP 4.2.1 {qBB}.
        using var generated = PlaneCSyntheticDatabase.OpenGenerated();
        InsertOutputTable(generated, 1, "4.2", "qAA");
        InsertOutputTable(generated, 2, "4.2.1", "qBB");

        using (var layouts = PlaneCSyntheticDatabase.OpenLayouts())
        {
            InsertSheet(layouts, 1, "X_01.01", "COREP", "4.2.1", "qAA");
            var result = PlaneCComparer.Compare(generated, layouts);

            var violation = Assert.Single(result.Violations);
            Assert.Equal(["COREP 4.2.1"], violation.FailedSheetReleases);
        }

        // Positive control: without the exact 4.2.1 TableID, the tolerance applies and qAA is contained.
        using var generatedNoExact = PlaneCSyntheticDatabase.OpenGenerated();
        InsertOutputTable(generatedNoExact, 1, "4.2", "qAA");
        using (var layouts = PlaneCSyntheticDatabase.OpenLayouts())
        {
            InsertSheet(layouts, 1, "X_01.01", "COREP", "4.2.1", "qAA");
            var result = PlaneCComparer.Compare(generatedNoExact, layouts);

            Assert.Empty(result.Violations);
            Assert.Equal(1, result.Coverage.SignaturesContained);
            Assert.Equal(1, result.Coverage.SheetsMatchedByPointReleaseTolerance);
        }
    }

    [Fact]
    public void Compare_CodeWithSheetsOfTwoReleases_SignatureContainedInOnlyOneRelease_IsOneViolationNamingTheFailingRelease()
    {
        // 4.2 table carries {qAA, qBB}; 4.2.1 table carries {qAA}. Both sheets ask for qAA and qBB.
        using var generated = PlaneCSyntheticDatabase.OpenGenerated();
        InsertOutputTable(generated, 1, "4.2", "qAA", "qBB");
        InsertOutputTable(generated, 2, "4.2.1", "qAA");
        using var layouts = PlaneCSyntheticDatabase.OpenLayouts();
        InsertSheet(layouts, 1, "X_01.01", "COREP", "4.2", "qAA", "qBB");
        InsertSheet(layouts, 2, "X_01.01", "COREP", "4.2.1", "qAA", "qBB");

        var result = PlaneCComparer.Compare(generated, layouts);

        var violation = Assert.Single(result.Violations);
        Assert.Equal("X_01.01", violation.TableCode);
        Assert.Contains("qBB", violation.CanonicalSignature, StringComparison.Ordinal);
        Assert.Equal(["COREP 4.2.1"], violation.FailedSheetReleases);
        Assert.Contains("COREP 4.2.1", violation.DetailText, StringComparison.Ordinal);
        // Each distinct signature counted ONCE, not once per sheet release.
        Assert.Equal(2, result.Coverage.SignaturesCompared);
        Assert.Equal(1, result.Coverage.SignaturesContained);
        Assert.Equal(1, result.Coverage.TablesCompared);
    }

    [Fact]
    public void Compare_CodeWithSheetsOfTwoReleases_SignatureContainedInBoth_NoViolationAndCountedOnce()
    {
        // Positive control of the previous test: giving the 4.2.1 table qBB too removes the violation.
        using var generated = PlaneCSyntheticDatabase.OpenGenerated();
        InsertOutputTable(generated, 1, "4.2", "qAA", "qBB");
        InsertOutputTable(generated, 2, "4.2.1", "qAA", "qBB");
        using var layouts = PlaneCSyntheticDatabase.OpenLayouts();
        InsertSheet(layouts, 1, "X_01.01", "COREP", "4.2", "qAA", "qBB");
        InsertSheet(layouts, 2, "X_01.01", "COREP", "4.2.1", "qAA", "qBB");

        var result = PlaneCComparer.Compare(generated, layouts);

        Assert.Empty(result.Violations);
        Assert.Equal(2, result.Coverage.SignaturesCompared);
        Assert.Equal(2, result.Coverage.SignaturesContained);
        Assert.Equal(1, result.Coverage.TablesCompared);
    }

    [Fact]
    public void Compare_CodeWithSheetsOfTwoReleases_SignatureFailingInBoth_IsOneViolationNamingBothReleases()
    {
        using var generated = PlaneCSyntheticDatabase.OpenGenerated();
        InsertOutputTable(generated, 1, "4.2", "qAA");
        InsertOutputTable(generated, 2, "4.2.1", "qAA");
        using var layouts = PlaneCSyntheticDatabase.OpenLayouts();
        InsertSheet(layouts, 1, "X_01.01", "COREP", "4.2", "qBB");
        InsertSheet(layouts, 2, "X_01.01", "COREP", "4.2.1", "qBB");

        var result = PlaneCComparer.Compare(generated, layouts);

        var violation = Assert.Single(result.Violations);
        Assert.Equal(2, violation.FailedSheetReleases.Count);
        Assert.Contains("COREP 4.2", violation.FailedSheetReleases);
        Assert.Contains("COREP 4.2.1", violation.FailedSheetReleases);
        Assert.Equal(1, result.Coverage.SignaturesCompared);
        Assert.Equal(0, result.Coverage.SignaturesContained);
    }
}
