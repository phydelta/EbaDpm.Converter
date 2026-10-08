using EbaDpm.Converter.Tests.Dictionary;

namespace EbaDpm.Converter.Tests.Modules;

/// <summary>
/// "Golden" level for modules against <c>EBA_4.0_ERRATA_5.db</c>, the SECONDARY content
/// reference, over four taxonomies: <c>COREP 4.0</c>, <c>DORA 4.0</c>, <c>IF 4.0</c>,
/// <c>MICA 4.0</c>.
///
/// Unlike 3.2 (<see cref="ModuleGoldenDiff32Tests"/>), this reference does NOT cover the Access
/// database 1:1: the Access v4.1 has 12 modules for these four taxonomies (verified against the
/// run of <see cref="ModulesFixture"/> itself: 6 in <c>corep 4.0</c>, 1 in <c>dora 4.0</c>, 4 in
/// <c>if 4.0</c> and 1 in <c>mica 4.0</c>) and the 4.0 reference only has **5 of those 12**
/// (module coverage is partial by design of the reference). The comparison is therefore one of
/// CONTAINMENT (the generated output contains the reference, never less), not of set equality,
/// unlike 3.2.
///
/// As in <see cref="ModuleGoldenDiff32Tests"/>, <c>Access.Taxonomy.TaxonomyCode</c> is emitted
/// verbatim in lower case (<c>"corep 4.0"</c>); the 4.0 reference uses upper case with a space
/// (<c>"COREP 4.0"</c>). Both sides are normalised to lower case before comparing, and that is why
/// the queries do NOT filter by literal <c>TaxonomyCode</c> in SQL: they fetch all the rows and
/// filter in memory after normalising.
///
/// It reuses a known defect of the reference: the node
/// <c>eba_tgIF_Class2_K-Factor_Requirements_-_Additional_Details</c> is also wrongly attributed to
/// <c>corep 4.0</c> in <c>mModuleBusinessTemplate</c> (row of module <c>COREP_OF</c>), not only in
/// the plain <c>mTemplateOrTable</c> census. It is the SAME object, named the same way, reused in
/// a different comparison context; it is excluded by name and nothing else is.
/// </summary>
[Collection("Modules")]
public sealed class ModuleGoldenDiff40Tests
{
    // The 4.0 reference DOES use the eba_tg prefix (it is from the same post-3.2 era as the
    // generated output), so the prefix normalisation that 3.2 needs is not required here.

    /// <summary>Known reference defect: node wrongly attributed to corep 4.0 in the 4.0 reference.</summary>
    private const string MisattributedNodeCode = "eba_tgIF_Class2_K-Factor_Requirements_-_Additional_Details";
    private const string MisattributedTaxonomyCode = "corep_4.0";
    private const string MisattributedModuleCode = "COREP_OF";

    private static readonly HashSet<string> TargetTaxonomies =
        new(StringComparer.Ordinal) { "corep_4.0", "dora_4.0", "if_4.0", "mica_4.0" };

    private readonly ModulesFixture _fixture;

    public ModuleGoldenDiff40Tests(ModulesFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>Normalises to lower case with <c>_</c> for the space, symmetric on both sides.</summary>
    private static string NormalizeTaxonomyCode(string code) => code.Replace(' ', '_').ToLowerInvariant();

    private static string Q(string identifier) => "\"" + identifier + "\"";

    private static string Col(string table, string column) => table + "." + Q(column);

    private static readonly string ModuleJoin =
        "FROM " + Q("mModule") + " m JOIN " + Q("mTaxonomy") + " t ON t." + Q("TaxonomyID") + " = m." + Q("TaxonomyID");

    private static readonly string BusinessTemplateJoin =
        "FROM " + Q("mModuleBusinessTemplate") + " mbt " +
        "JOIN " + Q("mModule") + " m ON m." + Q("ModuleID") + " = mbt." + Q("ModuleID") + " " +
        "JOIN " + Q("mTaxonomy") + " t ON t." + Q("TaxonomyID") + " = m." + Q("TaxonomyID") + " " +
        "JOIN " + Q("mTemplateOrTable") + " tot ON tot." + Q("TemplateOrTableID") + " = mbt." + Q("BusinessTemplateID");

    private static int ColumnCount(string columns) => columns.Count(c => c == ',') + 1;

    private List<string?[]> GeneratedRows(string columns) => QueryHelpers.Rows(
            _fixture.GeneratedConnection, "SELECT " + columns + " " + ModuleJoin, ColumnCount(columns))
        .Where(r => TargetTaxonomies.Contains(NormalizeTaxonomyCode(r[0]!)))
        .ToList();

    /// <summary>4.0 reference: NOT filtered by taxonomy; it only contains its own (5 modules, 4 taxonomies).</summary>
    private List<string?[]> ReferenceRows(string columns) => QueryHelpers.Rows(
        _fixture.Reference40Connection, "SELECT " + columns + " " + ModuleJoin, ColumnCount(columns));

    private List<string?[]> GeneratedBusinessTemplateRows(string columns) => QueryHelpers.Rows(
            _fixture.GeneratedConnection, "SELECT " + columns + " " + BusinessTemplateJoin, ColumnCount(columns))
        .Where(r => TargetTaxonomies.Contains(NormalizeTaxonomyCode(r[0]!)))
        .ToList();

    private List<string?[]> ReferenceBusinessTemplateRows(string columns) => QueryHelpers.Rows(
        _fixture.Reference40Connection, "SELECT " + columns + " " + BusinessTemplateJoin, ColumnCount(columns));

    // ------------------------------------------------------------------
    // 1) Module census: the 4.0 reference (5) is CONTAINED in the generated output for those
    //    four taxonomies (12).
    // ------------------------------------------------------------------

    [DataFact]
    public void ModuleCensus_GeneratedContainsReference40()
    {
        var columns = string.Join(", ", Col("t", "TaxonomyCode"), Col("m", "ModuleCode"));

        var generated = GeneratedRows(columns)
            .Select(r => (Taxonomy: NormalizeTaxonomyCode(r[0]!), Module: r[1]!))
            .ToHashSet();

        var reference = ReferenceRows(columns)
            .Select(r => (Taxonomy: NormalizeTaxonomyCode(r[0]!), Module: r[1]!))
            .ToHashSet();

        Assert.Equal(5, reference.Count); // guard on the reference itself
        Assert.Equal(12, generated.Count); // guard: 12 Access modules for these 4 taxonomies

        var missing = reference.Except(generated).ToList();
        Assert.True(
            missing.Count == 0,
            $"mModule census: {missing.Count} modules of EBA_4.0_ERRATA_5.db are not in the generated output. " +
            $"Examples: {string.Join(", ", missing.Select(m => $"{m.Taxonomy}/{m.Module}"))}.");
    }

    // ------------------------------------------------------------------
    // 2) ModuleLabel literal 5/5.
    // ------------------------------------------------------------------

    [DataFact]
    public void ModuleLabel_IsLiteral_FiveOfFive()
    {
        var columns = string.Join(", ", Col("t", "TaxonomyCode"), Col("m", "ModuleCode"), Col("m", "ModuleLabel"));

        var generated = GeneratedRows(columns)
            .ToDictionary(r => (Taxonomy: NormalizeTaxonomyCode(r[0]!), Module: r[1]!), r => r[2]!);

        var reference = ReferenceRows(columns)
            .ToDictionary(r => (Taxonomy: NormalizeTaxonomyCode(r[0]!), Module: r[1]!), r => r[2]!);

        Assert.Equal(5, reference.Count);

        var mismatches = new List<string>();
        foreach (var (key, refLabel) in reference)
        {
            if (!generated.TryGetValue(key, out var genLabel))
            {
                mismatches.Add($"{key.Taxonomy}/{key.Module}: missing from the generated output");
            }
            else if (!string.Equals(genLabel, refLabel, StringComparison.Ordinal))
            {
                mismatches.Add($"{key.Taxonomy}/{key.Module}: generated='{genLabel}' reference='{refLabel}'");
            }
        }

        Assert.True(mismatches.Count == 0, "ModuleLabel is not literal 5/5:\n" + string.Join("\n", mismatches));
    }

    // ------------------------------------------------------------------
    // 3) XBRLSchemaRef literal 5/5 (100% match with 4.0, with no declared divergence here: the
    //    declared divergences are specific to 3.2).
    // ------------------------------------------------------------------

    [DataFact]
    public void XBRLSchemaRef_IsLiteral_FiveOfFive()
    {
        var columns = string.Join(", ", Col("t", "TaxonomyCode"), Col("m", "ModuleCode"), Col("m", "XBRLSchemaRef"));

        var generated = GeneratedRows(columns)
            .ToDictionary(r => (Taxonomy: NormalizeTaxonomyCode(r[0]!), Module: r[1]!), r => r[2]!);

        var reference = ReferenceRows(columns)
            .ToDictionary(r => (Taxonomy: NormalizeTaxonomyCode(r[0]!), Module: r[1]!), r => r[2]!);

        Assert.Equal(5, reference.Count);

        var mismatches = new List<string>();
        foreach (var (key, refValue) in reference)
        {
            Assert.True(generated.TryGetValue(key, out var genValue), $"{key.Taxonomy}/{key.Module} missing from the generated output");
            if (!string.Equals(genValue, refValue, StringComparison.Ordinal))
            {
                mismatches.Add($"{key.Taxonomy}/{key.Module}: generated='{genValue}' reference='{refValue}'");
            }
        }

        Assert.True(mismatches.Count == 0, "XBRLSchemaRef is not literal 5/5 against 4.0:\n" + string.Join("\n", mismatches));
    }

    // ------------------------------------------------------------------
    // 4) mModuleBusinessTemplate: relative order (L1 node code, both numeric bases 0) for the 5
    //    modules of the 4.0 reference, EXCLUDING BY NAME the misattributed object (a single row,
    //    in COREP_OF/corep 4.0).
    // ------------------------------------------------------------------

    [DataFact]
    public void ModuleBusinessTemplate_Order_MatchesRelativeSequence_ForTheFiveReference40Modules_ExcludingMisattributedNode()
    {
        var columns = string.Join(
            ", ", Col("t", "TaxonomyCode"), Col("m", "ModuleCode"), Col("mbt", "Order"), Col("tot", "TemplateOrTableCode"));

        var generatedByModule = GeneratedBusinessTemplateRows(columns)
            .GroupBy(r => (Taxonomy: NormalizeTaxonomyCode(r[0]!), Module: r[1]!))
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(r => int.Parse(r[2]!)).Select(r => r[3]!).ToList());

        var referenceByModule = ReferenceBusinessTemplateRows(columns)
            .Where(r => !(NormalizeTaxonomyCode(r[0]!) == MisattributedTaxonomyCode && r[1] == MisattributedModuleCode && r[3] == MisattributedNodeCode)) // excluded by name
            .GroupBy(r => (Taxonomy: NormalizeTaxonomyCode(r[0]!), Module: r[1]!))
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(r => int.Parse(r[2]!)).Select(r => r[3]!).ToList());

        Assert.Equal(5, referenceByModule.Count);

        var failures = new List<string>();
        foreach (var (key, refSequence) in referenceByModule)
        {
            if (!generatedByModule.TryGetValue(key, out var genSequence))
            {
                failures.Add($"{key.Taxonomy}/{key.Module}: module missing from the generated output");
                continue;
            }

            // Ordered containment, not equality: the generated output can have MORE groups than
            // the reference (the Access v4.1 is richer), but the subset that the reference does
            // have (after excluding the misattributed node) must appear in the same relative order.
            var genFiltered = genSequence.Where(refSequence.Contains).ToList();

            if (!genFiltered.SequenceEqual(refSequence, StringComparer.Ordinal))
            {
                failures.Add(
                    $"{key.Taxonomy}/{key.Module}: different relative sequence.\n" +
                    $"  generated (subset): {string.Join(" · ", genFiltered)}\n" +
                    $"  reference:          {string.Join(" · ", refSequence)}");
            }

            var missing = refSequence.Except(genSequence).ToList();
            if (missing.Count > 0)
            {
                failures.Add($"{key.Taxonomy}/{key.Module}: {missing.Count} reference codes missing from the generated output: {string.Join(", ", missing)}");
            }
        }

        Assert.True(
            failures.Count == 0,
            $"{failures.Count} modules with an Order sequence different from the 4.0 reference (after excluding the misattributed node):\n" + string.Join("\n", failures));
    }
}
