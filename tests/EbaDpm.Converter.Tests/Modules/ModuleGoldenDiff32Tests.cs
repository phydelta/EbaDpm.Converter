using EbaDpm.Converter.Tests.Dictionary;

namespace EbaDpm.Converter.Tests.Modules;

/// <summary>
/// "Golden" level for modules against <c>EBA_3.2_phase_1.db</c>, the PRIMARY content reference,
/// over the complete working universe: the four taxonomies <c>AE 3.2</c>, <c>COREP 3.2</c>,
/// <c>GSII 3.2</c> and <c>IF 3.2</c> (13 modules, 35 <c>mModuleBusinessTemplate</c> rows).
///
/// Unlike <see cref="Dictionary.DictionaryFixture"/> (which only converts COREP 3.2),
/// <see cref="ModulesFixture"/> converts the four complete taxonomies, so EXACT SET EQUALITY is
/// required here (not mere containment): the 3.2 reference covers the Access database 1:1 and the
/// selection filter is exactly those four taxonomies, no more.
///
/// <c>Access.Taxonomy.TaxonomyCode</c> is emitted VERBATIM and is lower case in the source
/// (<c>"ae 3.2"</c>, <c>"corep 3.2"</c>...); the 3.2 reference uses upper case with an underscore
/// (<c>"AE_3.2"</c>). The business key comparison ALWAYS normalises both sides to lower case with
/// <c>_</c> for the space. This does not relax any content criterion: it is the same convention
/// normalisation applied to other pairs.
///
/// CRITICAL layer: module census, <c>ModuleLabel</c>, relative <c>Order</c> (template
/// coordinates). The only place where the test does NOT require literal equality is
/// <c>XBRLSchemaRef</c>: there the EXACT divergence pattern is required (8 literal matches + 5
/// named divergences), which in practice is as demanding as comparing everything: a sixth module
/// that diverged, or one of the five that stopped diverging, makes the test fail.
/// </summary>
[Collection("Modules")]
public sealed class ModuleGoldenDiff32Tests
{
    private const string GeneratedPrefix = "eba_tg";
    private const string ReferencePrefix = "tg";

    private static readonly HashSet<string> TargetTaxonomies =
        new(StringComparer.Ordinal) { "ae_3.2", "corep_3.2", "gsii_3.2", "if_3.2" };

    /// <summary>Known divergences of the reference database: the five rows, named one by one.</summary>
    private static readonly HashSet<(string Taxonomy, string Module)> DeclaredXbrlSchemaRefDivergences = new()
    {
        ("if_3.2", "IF_CLASS2"),
        ("if_3.2", "IF_CLASS3"),
        ("if_3.2", "IF_GROUPTEST"),
        ("if_3.2", "IF_TM"),
        ("gsii_3.2", "GSII"),
    };

    private readonly ModulesFixture _fixture;

    public ModuleGoldenDiff32Tests(ModulesFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// Normalises the taxonomy business key to lower case with <c>_</c> for the space, symmetric
    /// on both sides (generated: <c>"ae 3.2"</c> verbatim from the Access database; reference:
    /// <c>"AE_3.2"</c>).
    /// </summary>
    private static string NormalizeTaxonomyCode(string code) => code.Replace(' ', '_').ToLowerInvariant();

    private static string StripPrefix(string code, string prefix) =>
        code.StartsWith(prefix, StringComparison.Ordinal) ? code[prefix.Length..] : code;

    private static int ColumnCount(string columns) => columns.Count(c => c == ',') + 1;

    private static readonly string ModuleJoin =
        "FROM " + Q("mModule") + " m JOIN " + Q("mTaxonomy") + " t ON t." + Q("TaxonomyID") + " = m." + Q("TaxonomyID");

    private static readonly string BusinessTemplateJoin =
        "FROM " + Q("mModuleBusinessTemplate") + " mbt " +
        "JOIN " + Q("mModule") + " m ON m." + Q("ModuleID") + " = mbt." + Q("ModuleID") + " " +
        "JOIN " + Q("mTaxonomy") + " t ON t." + Q("TaxonomyID") + " = m." + Q("TaxonomyID") + " " +
        "JOIN " + Q("mTemplateOrTable") + " tot ON tot." + Q("TemplateOrTableID") + " = mbt." + Q("BusinessTemplateID");

    private static string Q(string identifier) => "\"" + identifier + "\"";

    private static string Col(string table, string column) => table + "." + Q(column);

    private List<string?[]> GeneratedRows(string columns) => QueryHelpers.Rows(
            _fixture.GeneratedConnection,
            "SELECT " + columns + " " + ModuleJoin,
            ColumnCount(columns))
        .Where(r => TargetTaxonomies.Contains(NormalizeTaxonomyCode(r[0]!)))
        .ToList();

    private List<string?[]> ReferenceRows(string columns) => QueryHelpers.Rows(
            _fixture.Reference32Connection,
            "SELECT " + columns + " " + ModuleJoin,
            ColumnCount(columns))
        .Where(r => TargetTaxonomies.Contains(NormalizeTaxonomyCode(r[0]!)))
        .ToList();

    private List<string?[]> GeneratedBusinessTemplateRows(string columns) => QueryHelpers.Rows(
            _fixture.GeneratedConnection,
            "SELECT " + columns + " " + BusinessTemplateJoin,
            ColumnCount(columns))
        .Where(r => TargetTaxonomies.Contains(NormalizeTaxonomyCode(r[0]!)))
        .ToList();

    private List<string?[]> ReferenceBusinessTemplateRows(string columns) => QueryHelpers.Rows(
            _fixture.Reference32Connection,
            "SELECT " + columns + " " + BusinessTemplateJoin,
            ColumnCount(columns))
        .Where(r => TargetTaxonomies.Contains(NormalizeTaxonomyCode(r[0]!)))
        .ToList();

    // ------------------------------------------------------------------
    // 1) Exact census: 13 modules.
    // ------------------------------------------------------------------

    [DataFact]
    public void ModuleCensus_IsExactlyThirteen_MatchingReference32ByBusinessKey()
    {
        var columns = string.Join(", ", Col("t", "TaxonomyCode"), Col("m", "ModuleCode"));

        var generated = GeneratedRows(columns)
            .Select(r => (Taxonomy: NormalizeTaxonomyCode(r[0]!), Module: r[1]!))
            .ToHashSet();

        var reference = ReferenceRows(columns)
            .Select(r => (Taxonomy: NormalizeTaxonomyCode(r[0]!), Module: r[1]!))
            .ToHashSet();

        Assert.Equal(13, reference.Count); // guard: if the reference changes, it must be noticed here first
        Assert.Equal(13, generated.Count);

        var missing = reference.Except(generated).ToList();
        var extra = generated.Except(reference).ToList();

        Assert.True(
            missing.Count == 0 && extra.Count == 0,
            $"mModule census against EBA_3.2_phase_1.db: not EXACTLY equal. " +
            $"Missing from the generated output: {string.Join(", ", missing.Select(m => $"{m.Taxonomy}/{m.Module}"))}. " +
            $"Extra in the generated output: {string.Join(", ", extra.Select(m => $"{m.Taxonomy}/{m.Module}"))}.");
    }

    // ------------------------------------------------------------------
    // 2) ModuleLabel literal 13/13.
    // ------------------------------------------------------------------

    [DataFact]
    public void ModuleLabel_IsLiteral_ThirteenOfThirteen()
    {
        var columns = string.Join(", ", Col("t", "TaxonomyCode"), Col("m", "ModuleCode"), Col("m", "ModuleLabel"));

        var generated = GeneratedRows(columns)
            .ToDictionary(r => (Taxonomy: NormalizeTaxonomyCode(r[0]!), Module: r[1]!), r => r[2]!);

        var reference = ReferenceRows(columns)
            .ToDictionary(r => (Taxonomy: NormalizeTaxonomyCode(r[0]!), Module: r[1]!), r => r[2]!);

        Assert.Equal(13, reference.Count);

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

        Assert.True(mismatches.Count == 0, $"ModuleLabel is not literal 13/13:\n" + string.Join("\n", mismatches));
    }

    // ------------------------------------------------------------------
    // 3) mModuleBusinessTemplate: exact census of 35 rows and exact RELATIVE ORDER per module:
    //    sequence of L1 node codes, normalising the eba_tg/tg prefix, ordinal comparison. The
    //    numeric base (0 generated, 1 in 3.2) is NOT compared literally, only the relative
    //    position.
    // ------------------------------------------------------------------

    [DataFact]
    public void ModuleBusinessTemplate_RowCount_IsExactlyThirtyFive()
    {
        var generatedCount = GeneratedBusinessTemplateRows(Col("t", "TaxonomyCode")).Count;
        var referenceCount = ReferenceBusinessTemplateRows(Col("t", "TaxonomyCode")).Count;

        Assert.Equal(35, referenceCount); // guard on the reference itself
        Assert.Equal(referenceCount, generatedCount);
    }

    [DataFact]
    public void ModuleBusinessTemplate_Order_MatchesRelativeSequence_ForAllThirteenModules()
    {
        var columns = string.Join(
            ", ", Col("t", "TaxonomyCode"), Col("m", "ModuleCode"), Col("mbt", "Order"), Col("tot", "TemplateOrTableCode"));

        var generatedByModule = GeneratedBusinessTemplateRows(columns)
            .GroupBy(r => (Taxonomy: NormalizeTaxonomyCode(r[0]!), Module: r[1]!))
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(r => int.Parse(r[2]!)).Select(r => StripPrefix(r[3]!, GeneratedPrefix)).ToList());

        var referenceByModule = ReferenceBusinessTemplateRows(columns)
            .GroupBy(r => (Taxonomy: NormalizeTaxonomyCode(r[0]!), Module: r[1]!))
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(r => int.Parse(r[2]!)).Select(r => StripPrefix(r[3]!, ReferencePrefix)).ToList());

        Assert.Equal(13, referenceByModule.Count);

        var failures = new List<string>();
        foreach (var (key, refSequence) in referenceByModule)
        {
            if (!generatedByModule.TryGetValue(key, out var genSequence))
            {
                failures.Add($"{key.Taxonomy}/{key.Module}: module missing from the generated output");
                continue;
            }

            if (!genSequence.SequenceEqual(refSequence, StringComparer.Ordinal))
            {
                failures.Add(
                    $"{key.Taxonomy}/{key.Module}: different sequence.\n" +
                    $"  generated: {string.Join(" · ", genSequence)}\n" +
                    $"  reference: {string.Join(" · ", refSequence)}");
            }
        }

        Assert.True(
            failures.Count == 0,
            $"{failures.Count}/13 modules with an Order sequence different from the 3.2 reference:\n" + string.Join("\n", failures));
    }

    // ------------------------------------------------------------------
    // 4) XBRLSchemaRef: 8/13 literal, 5/13 declared divergence. Full strictness: if any of the 8
    //    "good" ones diverged, or if the pattern of 5 divergent ones grew or changed identity,
    //    the test fails. The "IF/GSII" category is not excluded; the five objects are named one
    //    by one.
    // ------------------------------------------------------------------

    [DataFact]
    public void XBRLSchemaRef_MatchesLiteralOnEightOfThirteen_AndDivergesOnlyOnTheFiveDeclaredModules()
    {
        var columns = string.Join(", ", Col("t", "TaxonomyCode"), Col("m", "ModuleCode"), Col("m", "XBRLSchemaRef"));

        var generated = GeneratedRows(columns)
            .ToDictionary(r => (Taxonomy: NormalizeTaxonomyCode(r[0]!), Module: r[1]!), r => r[2]!);

        var reference = ReferenceRows(columns)
            .ToDictionary(r => (Taxonomy: NormalizeTaxonomyCode(r[0]!), Module: r[1]!), r => r[2]!);

        Assert.Equal(13, reference.Count);

        var unexpectedMatches = new List<string>();   // one of the 5 declared that now DOES match (stale exception)
        var unexpectedDivergences = new List<string>(); // one of the 8 "good" ones that now diverges (bug or changed reference)
        var literalMatches = 0;
        var declaredDivergences = 0;

        foreach (var (key, refValue) in reference)
        {
            Assert.True(generated.TryGetValue(key, out var genValue), $"{key.Taxonomy}/{key.Module} missing from the generated output");

            var isDeclaredDivergence = DeclaredXbrlSchemaRefDivergences.Contains(key);
            var literalMatch = string.Equals(genValue, refValue, StringComparison.Ordinal);

            if (isDeclaredDivergence)
            {
                if (literalMatch)
                {
                    unexpectedMatches.Add($"{key.Taxonomy}/{key.Module}: declared divergence but it now matches ('{genValue}'): stale exception");
                }
                else
                {
                    declaredDivergences++;
                }
            }
            else
            {
                if (literalMatch)
                {
                    literalMatches++;
                }
                else
                {
                    unexpectedDivergences.Add($"{key.Taxonomy}/{key.Module}: generated='{genValue}' reference='{refValue}' (NOT among the five declared divergences)");
                }
            }
        }

        Assert.True(
            unexpectedDivergences.Count == 0,
            $"{unexpectedDivergences.Count} modules diverge in XBRLSchemaRef WITHOUT being among the five declared divergences " +
            $"(a sixth/seventh divergent object, not declared):\n" + string.Join("\n", unexpectedDivergences));

        Assert.True(
            unexpectedMatches.Count == 0,
            $"{unexpectedMatches.Count} of the five declared divergences no longer diverge: " +
            $"the exception is stale and must be removed:\n" + string.Join("\n", unexpectedMatches));

        Assert.Equal(8, literalMatches);
        Assert.Equal(5, declaredDivergences);
    }
}
