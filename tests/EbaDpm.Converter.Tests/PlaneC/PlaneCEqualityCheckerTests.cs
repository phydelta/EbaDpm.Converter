using EbaDpm.Converter.Core.Validation.PlaneC;

namespace EbaDpm.Converter.Tests.PlaneC;

/// <summary>
/// <see cref="PlaneCEqualityChecker"/> on hand-built synthetic repositories -- the level where a
/// specific signature can be MUTATED and the two affected cells named exactly, which the real
/// corpus (5,120 rules) does not allow to isolate with the same precision. Same spirit as
/// <c>PlaneCComparerTests</c> for the rest of plane C.
///
/// Limit of the oracle, repeated here because it is the property that is easiest to lose sight of
/// when reading these tests: <c>PlaneCEqualityChecker</c> verifies the INTERNAL CONSISTENCY of the
/// signature between the cells that a rule names -- never that the signature is the correct one.
/// Two cells with the SAME wrong signature pass here just as they do in production; that is not a
/// gap in these tests, it is the documented contract of the class under test.
/// </summary>
public sealed class PlaneCEqualityCheckerTests
{
    [Fact]
    public void Check_TwoCellsOfDifferentTablesWithTheSameSignature_IsCheckableAndIdentical()
    {
        using var generated = PlaneCEqualitySyntheticDatabase.OpenGenerated();
        using var layouts = PlaneCEqualitySyntheticDatabase.OpenLayouts();

        generated.InsertTable(1, "A_01.01");
        generated.InsertAxis(10, 1, "Y", isOpenAxis: false, (100, "0010"));
        generated.InsertAxis(11, 1, "X", isOpenAxis: false, (101, "0020"));
        generated.InsertCell(1000, 1, "SIG1", isShaded: false, 100, 101);

        generated.InsertTable(2, "B_02.01");
        generated.InsertAxis(20, 2, "Y", isOpenAxis: false, (200, "0030"));
        generated.InsertAxis(21, 2, "X", isOpenAxis: false, (201, "0040"));
        generated.InsertCell(2000, 2, "SIG1", isShaded: false, 200, 201);

        layouts.InsertRuleTerm(1, 0, "A_01.01, r0010, c0020", "A_01.01", "0010", "0020", null);
        layouts.InsertRuleTerm(1, 1, "B_02.01, r0030, c0040", "B_02.01", "0030", "0040", null);

        var result = PlaneCEqualityChecker.Check(generated, layouts);

        Assert.Equal(1, result.Coverage.RulesTotal);
        Assert.Equal(1, result.Coverage.RulesCheckable);
        Assert.Equal(1, result.Coverage.RulesIdentical);
        Assert.Equal(0, result.Coverage.RulesDivergent);
        Assert.Empty(result.Violations);
    }

    /// <summary>
    /// Mandatory positive control: altering ONE <c>DatapointSignature</c> of a cell named by a rule
    /// moves the measurement from "2 checkable / 2 identical" to "2 checkable / 1 identical / 1
    /// different", and the report NAMES the two cells of the affected rule -- without touching the
    /// other rule, which stays identical (built-in negative control: if the mutation affected the
    /// wrong rule, this test would catch it).
    /// </summary>
    [Fact]
    public void Check_MutatingOneSignature_MovesExactlyOneRuleFromIdenticalToDifferent_AndNamesTheTwoCells()
    {
        using var generated = PlaneCEqualitySyntheticDatabase.OpenGenerated();
        using var layouts = PlaneCEqualitySyntheticDatabase.OpenLayouts();

        // Rule 1 (the one that will be mutated).
        generated.InsertTable(1, "A_01.01");
        generated.InsertAxis(10, 1, "Y", isOpenAxis: false, (100, "0010"));
        generated.InsertAxis(11, 1, "X", isOpenAxis: false, (101, "0020"));
        generated.InsertCell(1000, 1, "SIG1", isShaded: false, 100, 101);

        generated.InsertTable(2, "B_02.01");
        generated.InsertAxis(20, 2, "Y", isOpenAxis: false, (200, "0030"));
        generated.InsertAxis(21, 2, "X", isOpenAxis: false, (201, "0040"));
        generated.InsertCell(2000, 2, "SIG1", isShaded: false, 200, 201);

        layouts.InsertRuleTerm(1, 0, "A_01.01, r0010, c0020", "A_01.01", "0010", "0020", null);
        layouts.InsertRuleTerm(1, 1, "B_02.01, r0030, c0040", "B_02.01", "0030", "0040", null);

        // Rule 2 (negative control: it must stay identical, unrelated to the mutation of rule 1).
        generated.InsertTable(3, "C_03.01");
        generated.InsertAxis(30, 3, "Y", isOpenAxis: false, (300, "0050"));
        generated.InsertAxis(31, 3, "X", isOpenAxis: false, (301, "0060"));
        generated.InsertCell(3000, 3, "SIG9", isShaded: false, 300, 301);

        generated.InsertTable(4, "D_04.01");
        generated.InsertAxis(40, 4, "Y", isOpenAxis: false, (400, "0070"));
        generated.InsertAxis(41, 4, "X", isOpenAxis: false, (401, "0080"));
        generated.InsertCell(4000, 4, "SIG9", isShaded: false, 400, 401);

        layouts.InsertRuleTerm(2, 0, "C_03.01, r0050, c0060", "C_03.01", "0050", "0060", null);
        layouts.InsertRuleTerm(2, 1, "D_04.01, r0070, c0080", "D_04.01", "0070", "0080", null);

        // Control: BEFORE mutating, 2 checkable, 2 identical.
        var before = PlaneCEqualityChecker.Check(generated, layouts);
        Assert.Equal(2, before.Coverage.RulesCheckable);
        Assert.Equal(2, before.Coverage.RulesIdentical);
        Assert.Equal(0, before.Coverage.RulesDivergent);
        Assert.Empty(before.Violations);

        // The mutation: ONLY cell 2000 (part of rule 1) changes signature.
        generated.UpdateCellSignature(2000, "SIG1-MUTATED");

        var after = PlaneCEqualityChecker.Check(generated, layouts);

        Assert.Equal(2, after.Coverage.RulesCheckable);
        Assert.Equal(1, after.Coverage.RulesIdentical);
        Assert.Equal(1, after.Coverage.RulesDivergent);

        var violation = Assert.Single(after.Violations);
        Assert.Equal(1, violation.RuleId);
        Assert.Equal("{A_01.01, r0010, c0020}=={B_02.01, r0030, c0040}", violation.BusinessKey);
        Assert.Equal(2, violation.Resolved.Count);
        Assert.Contains(violation.Resolved, r => r.Term == "A_01.01, r0010, c0020" && r.Signature == "SIG1");
        Assert.Contains(violation.Resolved, r => r.Term == "B_02.01, r0030, c0040" && r.Signature == "SIG1-MUTATED");
    }

    /// <summary>
    /// The <c>9999</c> SENTINEL is not a literal <c>OrdinateCode</c> -- it means "the SINGLE
    /// ordinate of the OPEN axis of that orientation in that table". A decoy is built, in the SAME
    /// table: an ordinate with LITERAL code <c>"9999"</c> on a CLOSED axis, and an ordinate with a
    /// real code on the OPEN axis of the same orientation -- if the resolution of <c>c9999</c>
    /// fell on the decoy (the literal), this rule would come out DIVERGENT because the decoy
    /// carries a signature different from that of the control table; if it resolves to the ordinate
    /// of the open axis (the required behaviour), it comes out IDENTICAL.
    /// </summary>
    [Fact]
    public void Check_Sentinel9999_ResolvesToTheOpenAxisOrdinate_NotToTheOrdinateWithLiteralCode9999()
    {
        using var generated = PlaneCEqualitySyntheticDatabase.OpenGenerated();
        using var layouts = PlaneCEqualitySyntheticDatabase.OpenLayouts();

        generated.InsertTable(1, "C_01.01");
        generated.InsertAxis(10, 1, "Y", isOpenAxis: false, (100, "0001"));
        // CLOSED X axis with an ordinate whose code is, coincidentally, the literal "9999" -- the decoy.
        generated.InsertAxis(11, 1, "X", isOpenAxis: false, (101, "9999"));
        // OPEN X axis (second axis of the SAME orientation in the SAME table) with the real ordinate.
        generated.InsertAxis(12, 1, "X", isOpenAxis: true, (102, "0010"));

        generated.InsertCell(1000, 1, "SIG-DECOY", isShaded: false, 100, 101); // (Y=0001, X=literal 9999)
        generated.InsertCell(1001, 1, "SIG-OPEN", isShaded: false, 100, 102); // (Y=0001, X=open axis)

        // Control table, nothing special, with the signature expected if the resolution is CORRECT.
        generated.InsertTable(2, "E_01.01");
        generated.InsertAxis(20, 2, "Y", isOpenAxis: false, (200, "0001"));
        generated.InsertAxis(21, 2, "X", isOpenAxis: false, (201, "0001"));
        generated.InsertCell(2000, 2, "SIG-OPEN", isShaded: false, 200, 201);

        // The term asks for c9999 -- the sentinel, on a table WITH an open axis in X.
        layouts.InsertRuleTerm(1, 0, "C_01.01, r0001, c9999", "C_01.01", "0001", "9999", null);
        layouts.InsertRuleTerm(1, 1, "E_01.01, r0001, c0001", "E_01.01", "0001", "0001", null);

        var result = PlaneCEqualityChecker.Check(generated, layouts);

        Assert.Equal(1, result.Coverage.RulesCheckable);
        Assert.Equal(1, result.Coverage.RulesIdentical);
        Assert.Equal(0, result.Coverage.RulesDivergent);
        Assert.Empty(result.Violations);

        // Positive control that the test KNOWS how to tell the two cases apart: if this same table
        // had no open axis (see the next test), the sentinel should not exist.
    }

    /// <summary>
    /// Negative control of the previous one: WITHOUT any <c>IsOpenAxis=1</c> axis in that
    /// orientation, the <c>9999</c> sentinel is not synthesized -- <c>c9999</c> can only resolve
    /// against a LITERAL <c>OrdinateCode</c>. Here one does exist (the same decoy), so the rule
    /// resolves against it -- it shows that the previous test goes through the sentinel logic, not
    /// because "9999" matches anything.
    /// </summary>
    [Fact]
    public void Check_WithoutA9999SynthesizedByAnOpenAxis_c9999ResolvesAgainstTheLiteralOrdinate()
    {
        using var generated = PlaneCEqualitySyntheticDatabase.OpenGenerated();
        using var layouts = PlaneCEqualitySyntheticDatabase.OpenLayouts();

        generated.InsertTable(1, "C_01.01");
        generated.InsertAxis(10, 1, "Y", isOpenAxis: false, (100, "0001"));
        generated.InsertAxis(11, 1, "X", isOpenAxis: false, (101, "9999")); // only candidate, LITERAL, no open axis.
        generated.InsertCell(1000, 1, "SIG-DECOY", isShaded: false, 100, 101);

        generated.InsertTable(2, "E_01.01");
        generated.InsertAxis(20, 2, "Y", isOpenAxis: false, (200, "0001"));
        generated.InsertAxis(21, 2, "X", isOpenAxis: false, (201, "0001"));
        generated.InsertCell(2000, 2, "SIG-OPEN", isShaded: false, 200, 201);

        layouts.InsertRuleTerm(1, 0, "C_01.01, r0001, c9999", "C_01.01", "0001", "9999", null);
        layouts.InsertRuleTerm(1, 1, "E_01.01, r0001, c0001", "E_01.01", "0001", "0001", null);

        var result = PlaneCEqualityChecker.Check(generated, layouts);

        // Without the sentinel, c9999 can only resolve to the literal -- SIG-DECOY != SIG-OPEN -> divergent.
        Assert.Equal(1, result.Coverage.RulesCheckable);
        Assert.Equal(0, result.Coverage.RulesIdentical);
        Assert.Equal(1, result.Coverage.RulesDivergent);
    }

    [Fact]
    public void Check_TableNamedByTheLayoutDoesNotExistInTheOutput_IsNotCheckable_MissingTable()
    {
        using var generated = PlaneCEqualitySyntheticDatabase.OpenGenerated();
        using var layouts = PlaneCEqualitySyntheticDatabase.OpenLayouts();

        generated.InsertTable(1, "A_01.01");
        generated.InsertAxis(10, 1, "Y", isOpenAxis: false, (100, "0010"));
        generated.InsertAxis(11, 1, "X", isOpenAxis: false, (101, "0020"));
        generated.InsertCell(1000, 1, "SIG1", isShaded: false, 100, 101);

        layouts.InsertRuleTerm(1, 0, "A_01.01, r0010, c0020", "A_01.01", "0010", "0020", null);
        layouts.InsertRuleTerm(1, 1, "Z_99.99, r0001, c0001", "Z_99.99", "0001", "0001", null); // does not exist in generated.

        var result = PlaneCEqualityChecker.Check(generated, layouts);

        Assert.Equal(1, result.Coverage.RulesTotal);
        Assert.Equal(0, result.Coverage.RulesCheckable);
        Assert.Equal(1L, result.Coverage.NonCheckableByReason.GetValueOrDefault("table absent"));
        Assert.Empty(result.Violations);
    }

    /// <summary>
    /// Complementary control -- the heart of the open-axis bracket relaxation: altering ONLY the
    /// open-axis restriction bracket of a signature -- <c>qEEA(*[841])</c> -&gt;
    /// <c>qEEA(*[999])</c> -- must NOT make <c>C-EQU-01</c> fail. The <c>==</c> of the layout
    /// asserts identity of the VARIABLE, and that bracket belongs to the TABLE, never to the
    /// Variable -- exactly what the rule does NOT assert. The counter
    /// <c>RulesRelaxedByOpenAxisBracket</c> has to go up by 1 (the relaxation is named, never
    /// silent).
    /// </summary>
    [Fact]
    public void Check_TwoCellsDifferingOnlyInTheOpenAxisBracket_IsNotDivergent_AndCountsAsRelaxed()
    {
        using var generated = PlaneCEqualitySyntheticDatabase.OpenGenerated();
        using var layouts = PlaneCEqualitySyntheticDatabase.OpenLayouts();

        generated.InsertTable(1, "K_26.00.a");
        generated.InsertAxis(10, 1, "Y", isOpenAxis: false, (100, "0010"));
        generated.InsertAxis(11, 1, "X", isOpenAxis: false, (101, "0050"));
        generated.InsertCell(1000, 1, "MET(qBCU)|qEEA(*[841])", isShaded: false, 100, 101);

        generated.InsertTable(2, "K_29.00");
        generated.InsertAxis(20, 2, "Y", isOpenAxis: false, (200, "0010"));
        generated.InsertAxis(21, 2, "X", isOpenAxis: false, (201, "0040"));
        // SAME signature except for the restriction bracket: qAE2 (841) versus qAE1 (999),
        // genuinely DIFFERENT subcategories of the table, not of the Variable.
        generated.InsertCell(2000, 2, "MET(qBCU)|qEEA(*[999])", isShaded: false, 200, 201);

        layouts.InsertRuleTerm(1, 0, "K_26.00.a, r0010, c0050", "K_26.00.a", "0010", "0050", null);
        layouts.InsertRuleTerm(1, 1, "K_29.00, r0010, c0040", "K_29.00", "0010", "0040", null);

        var result = PlaneCEqualityChecker.Check(generated, layouts);

        Assert.Equal(1, result.Coverage.RulesCheckable);
        Assert.Equal(1, result.Coverage.RulesIdentical);
        Assert.Equal(0, result.Coverage.RulesDivergent);
        Assert.Empty(result.Violations);
        Assert.Equal(1, result.Coverage.RulesRelaxedByOpenAxisBracket);
    }

    /// <summary>
    /// Positive control complementary to the previous one -- the relaxation does NOT blind the
    /// check: a GENUINE difference (outside the bracket -- here, the metric itself) between the two
    /// cells of a rule still produces a real violation, even if both carry the SAME open-axis
    /// bracket (nothing to relax). The relaxation only stops looking at the bracket, nothing else
    /// in the signature.
    /// </summary>
    [Fact]
    public void Check_TwoCellsWithTheSameOpenAxisRestrictionButADifferentMetric_IsStillDivergent()
    {
        using var generated = PlaneCEqualitySyntheticDatabase.OpenGenerated();
        using var layouts = PlaneCEqualitySyntheticDatabase.OpenLayouts();

        generated.InsertTable(1, "K_26.00.a");
        generated.InsertAxis(10, 1, "Y", isOpenAxis: false, (100, "0010"));
        generated.InsertAxis(11, 1, "X", isOpenAxis: false, (101, "0050"));
        generated.InsertCell(1000, 1, "MET(qBCU)|qEEA(*[841])", isShaded: false, 100, 101);

        generated.InsertTable(2, "K_29.00");
        generated.InsertAxis(20, 2, "Y", isOpenAxis: false, (200, "0010"));
        generated.InsertAxis(21, 2, "X", isOpenAxis: false, (201, "0040"));
        // SAME bracket (841), but the METRIC differs -- a genuine difference, not of the table.
        generated.InsertCell(2000, 2, "MET(qDIFFERENT)|qEEA(*[841])", isShaded: false, 200, 201);

        layouts.InsertRuleTerm(1, 0, "K_26.00.a, r0010, c0050", "K_26.00.a", "0010", "0050", null);
        layouts.InsertRuleTerm(1, 1, "K_29.00, r0010, c0040", "K_29.00", "0010", "0040", null);

        var result = PlaneCEqualityChecker.Check(generated, layouts);

        Assert.Equal(1, result.Coverage.RulesCheckable);
        Assert.Equal(0, result.Coverage.RulesIdentical);
        Assert.Equal(1, result.Coverage.RulesDivergent);
        Assert.Single(result.Violations);
        // With no bracket to relax (it did not diverge because of that), the counter does NOT go up.
        Assert.Equal(0, result.Coverage.RulesRelaxedByOpenAxisBracket);
    }

    [Fact]
    public void Check_ShadedOrUnsignedCell_IsNotCheckable_AndCountsAsNeitherIdenticalNorDivergent()
    {
        using var generated = PlaneCEqualitySyntheticDatabase.OpenGenerated();
        using var layouts = PlaneCEqualitySyntheticDatabase.OpenLayouts();

        generated.InsertTable(1, "A_01.01");
        generated.InsertAxis(10, 1, "Y", isOpenAxis: false, (100, "0010"));
        generated.InsertAxis(11, 1, "X", isOpenAxis: false, (101, "0020"));
        generated.InsertCell(1000, 1, "SIG1", isShaded: true, 100, 101); // shaded -- no comparable signature.

        generated.InsertTable(2, "B_02.01");
        generated.InsertAxis(20, 2, "Y", isOpenAxis: false, (200, "0030"));
        generated.InsertAxis(21, 2, "X", isOpenAxis: false, (201, "0040"));
        generated.InsertCell(2000, 2, "SIG1", isShaded: false, 200, 201);

        layouts.InsertRuleTerm(1, 0, "A_01.01, r0010, c0020", "A_01.01", "0010", "0020", null);
        layouts.InsertRuleTerm(1, 1, "B_02.01, r0030, c0040", "B_02.01", "0030", "0040", null);

        var result = PlaneCEqualityChecker.Check(generated, layouts);

        Assert.Equal(0, result.Coverage.RulesCheckable);
        Assert.Equal(1L, result.Coverage.NonCheckableByReason.GetValueOrDefault("shaded or without signature"));
        Assert.Empty(result.Violations);
    }

    /// <summary>
    /// Negative control of the release guard: the table of the FIRST term EXISTS (never "missing
    /// table") but its own release (<c>mTaxonomyTable</c>/<c>mTaxonomy</c>/
    /// <c>mReportingFramework</c>) is <c>4.3</c> while the rule's source sheet is <c>4.2</c> (not
    /// even <c>REL.n</c> of it -- "4.3" does not start with "4.2.") -- the whole rule is NOT
    /// checkable with the reason <c>"different release"</c>, never "ambiguous cell" nor any other
    /// structural accident.
    /// </summary>
    [Fact]
    public void Check_TableOfADifferentReleaseThanTheSourceSheet_IsNotCheckable_ReasonDifferentRelease()
    {
        using var generated = PlaneCEqualitySyntheticDatabase.OpenGenerated();
        using var layouts = PlaneCEqualitySyntheticDatabase.OpenLayouts();

        generated.InsertTable(1, "A_01.01", release: "4.3"); // DIFFERENT from the sheet (4.2 by default).
        generated.InsertAxis(10, 1, "Y", isOpenAxis: false, (100, "0010"));
        generated.InsertAxis(11, 1, "X", isOpenAxis: false, (101, "0020"));
        generated.InsertCell(1000, 1, "SIG1", isShaded: false, 100, 101);

        generated.InsertTable(2, "B_02.01"); // default release (4.2) -- DOES match the sheet.
        generated.InsertAxis(20, 2, "Y", isOpenAxis: false, (200, "0030"));
        generated.InsertAxis(21, 2, "X", isOpenAxis: false, (201, "0040"));
        generated.InsertCell(2000, 2, "SIG1", isShaded: false, 200, 201);

        layouts.InsertRuleTerm(1, 0, "A_01.01, r0010, c0020", "A_01.01", "0010", "0020", null);
        layouts.InsertRuleTerm(1, 1, "B_02.01, r0030, c0040", "B_02.01", "0030", "0040", null);

        var result = PlaneCEqualityChecker.Check(generated, layouts);

        Assert.Equal(0, result.Coverage.RulesCheckable);
        Assert.Equal(1L, result.Coverage.NonCheckableByReason.GetValueOrDefault("different release"));
        Assert.Empty(result.Violations);
    }

    /// <summary>
    /// Positive control complementary to the previous one: the SAME shape as the previous test,
    /// but with both tables AND the source sheet at the SAME NON-default release (<c>4.3</c>, not
    /// <c>4.2</c>) -- it proves that the matching works for any release, and not just because the
    /// fixture default happens to coincide with the sheet's.
    /// </summary>
    [Fact]
    public void Check_TableAndSheetWithTheSameNonDefaultRelease_IsCheckable()
    {
        using var generated = PlaneCEqualitySyntheticDatabase.OpenGenerated();
        using var layouts = PlaneCEqualitySyntheticDatabase.OpenLayouts();

        generated.InsertTable(1, "A_01.01", frameworkCode: "COREP", release: "4.3");
        generated.InsertAxis(10, 1, "Y", isOpenAxis: false, (100, "0010"));
        generated.InsertAxis(11, 1, "X", isOpenAxis: false, (101, "0020"));
        generated.InsertCell(1000, 1, "SIG1", isShaded: false, 100, 101);

        generated.InsertTable(2, "B_02.01", frameworkCode: "COREP", release: "4.3");
        generated.InsertAxis(20, 2, "Y", isOpenAxis: false, (200, "0030"));
        generated.InsertAxis(21, 2, "X", isOpenAxis: false, (201, "0040"));
        generated.InsertCell(2000, 2, "SIG1", isShaded: false, 200, 201);

        layouts.InsertRuleTerm(1, 0, "A_01.01, r0010, c0020", "A_01.01", "0010", "0020", null, frameworkCode: "COREP", release: "4.3");
        layouts.InsertRuleTerm(1, 1, "B_02.01, r0030, c0040", "B_02.01", "0030", "0040", null, frameworkCode: "COREP", release: "4.3");

        var result = PlaneCEqualityChecker.Check(generated, layouts);

        Assert.Equal(1, result.Coverage.RulesCheckable);
        Assert.Equal(1, result.Coverage.RulesIdentical);
        Assert.Equal(0, result.Coverage.RulesDivergent);
        Assert.Empty(result.Violations);
    }

    /// <summary>
    /// The <c>REL.n -> REL</c> tolerance (hotfix / point release) applies to <c>C-EQU-01</c> with
    /// the SAME mechanism as <c>PlaneCComparer</c> -- a sheet published as <c>4.2.1</c> IS the
    /// table's own release <c>4.2</c>, the rule comes out checkable, and the tolerance is COUNTED
    /// and NAMED, never silent.
    /// </summary>
    [Fact]
    public void Check_SheetWithAPointReleaseOfTheTableRelease_IsCheckableByTolerance_AndIsCounted()
    {
        using var generated = PlaneCEqualitySyntheticDatabase.OpenGenerated();
        using var layouts = PlaneCEqualitySyntheticDatabase.OpenLayouts();

        generated.InsertTable(1, "A_01.01"); // default release: 4.2.
        generated.InsertAxis(10, 1, "Y", isOpenAxis: false, (100, "0010"));
        generated.InsertAxis(11, 1, "X", isOpenAxis: false, (101, "0020"));
        generated.InsertCell(1000, 1, "SIG1", isShaded: false, 100, 101);

        generated.InsertTable(2, "B_02.01");
        generated.InsertAxis(20, 2, "Y", isOpenAxis: false, (200, "0030"));
        generated.InsertAxis(21, 2, "X", isOpenAxis: false, (201, "0040"));
        generated.InsertCell(2000, 2, "SIG1", isShaded: false, 200, 201);

        // The source sheet was published as the point release "4.2.1" -- it IS the table's 4.2.
        layouts.InsertRuleTerm(1, 0, "A_01.01, r0010, c0020", "A_01.01", "0010", "0020", null, release: "4.2.1");
        layouts.InsertRuleTerm(1, 1, "B_02.01, r0030, c0040", "B_02.01", "0030", "0040", null, release: "4.2.1");

        var result = PlaneCEqualityChecker.Check(generated, layouts);

        Assert.Equal(1, result.Coverage.RulesCheckable);
        Assert.Equal(1, result.Coverage.RulesIdentical);
        Assert.Equal(1, result.Coverage.SheetsMatchedByPointReleaseTolerance);
        Assert.NotEmpty(result.Coverage.PointReleaseToleranceSamples);
    }
}
