namespace EbaDpm.Converter.Tests.Dictionary;

/// <summary>
/// "Golden" level -- semantic diff of the template/table tree against
/// <c>EBA_3.2_phase_1.db</c>, the CONTENT reference (4.2 does not decide content, only schema).
/// Critical layer: containment, never less.
///
/// The reference run only converts <c>COREP 3.2</c> (<see cref="DictionaryFixture"/>), so every
/// comparison is scoped to the <c>COREP_3.2</c> taxonomy of the 3.2 reference (which also has
/// three others: <c>IF_3.2</c>, <c>AE_3.2</c>, <c>GSII_3.2</c>, not converted here and therefore
/// out of comparison scope, not a deficit).
///
/// The 3.2 reference puts the 217 <c>BusinessTable</c> tables at <c>Level = 3</c> (a third level
/// deliberately NOT emitted; the output uses 2 levels); <c>TableGroup</c> L1/L2 do match at
/// <c>Level</c> 1/2 in both databases. That is why the `Level` of `BusinessTable` is NEVER
/// compared literally against 3.2 (only its type/code), and row counts of
/// <c>mTemplateOrTable</c> are NOT compared in bulk, only by business key with containment.
///
/// It is not compared against <c>EBA_4.0_ERRATA_5.db</c> (where a known exception would be
/// available: the node <c>eba_tgIF_Class2_K-Factor_Requirements_-_Additional_Details</c>
/// misattributed to <c>corep 4.0</c>): that reference does not have the <c>COREP 3.2</c>
/// taxonomy, so there is no content comparable with this fixture's run -- comparing between
/// different releases is not valid.
/// </summary>
[Collection("Dictionary")]
public sealed class TemplateOrTableGoldenDiffTests
{
    private const string ReferenceTaxonomyCode = "COREP_3.2";
    private const string GeneratedTableGroupPrefix = "eba_tg";
    private const string ReferenceTableGroupPrefix = "tg";

    private readonly DictionaryFixture _fixture;

    public TemplateOrTableGoldenDiffTests(DictionaryFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// Symmetric normalisation of the prefix convention: the 4.x destination prepends
    /// <c>eba_tg</c>, the 3.2 reference prepends <c>tg</c>, to the same base code (normalised the
    /// same on both sides: spaces -> <c>_</c>, short hyphen kept, long hyphen removed). It is not
    /// a relaxation: it is applied to both sides and to all rows.
    /// </summary>
    private static string StripTableGroupPrefix(string code, string prefix) =>
        code.StartsWith(prefix, StringComparison.Ordinal) ? code[prefix.Length..] : code;

    private HashSet<string> GeneratedLevel1Codes() =>
        QueryHelpers.Rows(
                _fixture.GeneratedConnection,
                "SELECT \"TemplateOrTableCode\" FROM \"mTemplateOrTable\" WHERE \"TemplateOrTableType\" = 'TableGroup' AND \"Level\" = 1",
                1)
            .Select(r => StripTableGroupPrefix(r[0]!, GeneratedTableGroupPrefix))
            .ToHashSet(StringComparer.Ordinal);

    private HashSet<string> Reference32Level1Codes() =>
        QueryHelpers.Rows(
                _fixture.Reference32Connection,
                """
                SELECT tot."TemplateOrTableCode" FROM "mTemplateOrTable" tot
                JOIN "mTaxonomy" t ON t."TaxonomyID" = tot."TaxonomyID"
                WHERE t."TaxonomyCode" = @tax AND tot."TemplateOrTableType" = 'TableGroup' AND tot."Level" = 1
                """.Replace("@tax", $"'{ReferenceTaxonomyCode}'"),
                1)
            .Select(r => StripTableGroupPrefix(r[0]!, ReferenceTableGroupPrefix))
            .ToHashSet(StringComparer.Ordinal);

    [DataFact]
    public void Level1Codes_GeneratedContainsReference32_ForCorep32()
    {
        var generated = GeneratedLevel1Codes();
        var reference = Reference32Level1Codes();

        // The compared census must be checked, not just printed -- if the reference query broke
        // and returned 0 rows, "missing.Count == 0" would pass green without having compared
        // anything.
        Assert.True(reference.Count > 0, "Reference32Level1Codes() returned 0 rows: the comparison would be empty.");

        var missing = reference.Except(generated, StringComparer.Ordinal).OrderBy(c => c, StringComparer.Ordinal).ToList();
        var extra = generated.Except(reference, StringComparer.Ordinal).Count();

        Assert.True(
            missing.Count == 0,
            $"mTemplateOrTable L1 (TableGroup): {missing.Count} COREP_3.2 codes are not in " +
            $"the generated output. Examples: {string.Join(", ", missing.Take(15))}. (Generated: {generated.Count}, " +
            $"reference: {reference.Count}, expected and legitimate excess: {extra}.)");
    }

    [DataFact]
    public void Level1Codes_SetIsExactlyEqual_NotJustContained()
    {
        // In the primary reference (which covers the Access 1:1), set equality is TOTAL for the 4
        // comparable taxonomies, not just containment. Since only COREP 3.2 is converted here,
        // exact equality is required (containment is not enough).
        var generated = GeneratedLevel1Codes();
        var reference = Reference32Level1Codes();

        // Without this guard, two empty sets "match" and the test would pass without comparing anything.
        Assert.True(reference.Count > 0, "Reference32Level1Codes() returned 0 rows: the comparison would be empty.");

        Assert.True(
            generated.SetEquals(reference),
            $"mTemplateOrTable L1 of COREP 3.2 is not EXACTLY equal to the Access TableGroup " +
            $"(via the 3.2 reference). Generated ({generated.Count}): {string.Join(",", generated.OrderBy(c => c, StringComparer.Ordinal))}. " +
            $"Reference ({reference.Count}): {string.Join(",", reference.OrderBy(c => c, StringComparer.Ordinal))}.");
    }

    [DataFact]
    public void Level2Pairs_ChildParent_GeneratedContainsReference32_ForCorep32()
    {
        var generated = QueryHelpers.Rows(
                _fixture.GeneratedConnection,
                """
                SELECT c."TemplateOrTableCode", p."TemplateOrTableCode"
                FROM "mTemplateOrTable" c
                JOIN "mTemplateOrTable" p ON p."TemplateOrTableID" = c."ParentTemplateOrTableID"
                WHERE c."TemplateOrTableType" = 'TableGroup' AND c."Level" = 2
                """,
                2)
            .Select(r => (
                Child: StripTableGroupPrefix(r[0]!, GeneratedTableGroupPrefix),
                Parent: StripTableGroupPrefix(r[1]!, GeneratedTableGroupPrefix)))
            .ToHashSet();

        var reference = QueryHelpers.Rows(
                _fixture.Reference32Connection,
                """
                SELECT c."TemplateOrTableCode", p."TemplateOrTableCode"
                FROM "mTemplateOrTable" c
                JOIN "mTaxonomy" t ON t."TaxonomyID" = c."TaxonomyID"
                JOIN "mTemplateOrTable" p ON p."TemplateOrTableID" = c."ParentTemplateOrTableID"
                WHERE t."TaxonomyCode" = 'COREP_3.2' AND c."TemplateOrTableType" = 'TableGroup' AND c."Level" = 2
                """,
                2)
            .Select(r => (
                Child: StripTableGroupPrefix(r[0]!, ReferenceTableGroupPrefix),
                Parent: StripTableGroupPrefix(r[1]!, ReferenceTableGroupPrefix)))
            .ToHashSet();

        // Checked census, not just printed.
        Assert.True(reference.Count > 0, "The L2->L1 pair query of the 3.2 reference returned 0 rows: the comparison would be empty.");

        var missing = reference.Except(generated).ToList();
        var extra = generated.Except(reference).Count();

        Assert.True(
            missing.Count == 0,
            $"mTemplateOrTable L2->L1 (child-parent pairs): {missing.Count} COREP_3.2 pairs are not " +
            $"in the generated output. Examples: {string.Join("; ", missing.Take(15).Select(p => $"{p.Child}<-{p.Parent}"))}. " +
            $"(Generated: {generated.Count}, reference: {reference.Count}, legitimate excess: {extra}.)");

        // In the primary reference, 144/144 -- here, exact equality.
        Assert.True(
            generated.SetEquals(reference),
            "The L2->L1 pairs of COREP 3.2 are not EXACTLY equal to those of the 3.2 reference. " +
            $"Generated: {generated.Count}, reference: {reference.Count}.");
    }

    [DataFact]
    public void BusinessTableCodes_GeneratedContainsReference32_ForCorep32()
    {
        // BusinessTable codes carry no eba_tg/tg prefix (they are the XbrlTableCode verbatim,
        // e.g. "C_17.01.b"): no normalisation is needed.
        var generated = QueryHelpers.Rows(
                _fixture.GeneratedConnection,
                "SELECT \"TemplateOrTableCode\" FROM \"mTemplateOrTable\" WHERE \"TemplateOrTableType\" = 'BusinessTable'",
                1)
            .Select(r => r[0]!)
            .ToHashSet(StringComparer.Ordinal);

        var reference = QueryHelpers.Rows(
                _fixture.Reference32Connection,
                """
                SELECT tot."TemplateOrTableCode" FROM "mTemplateOrTable" tot
                JOIN "mTaxonomy" t ON t."TaxonomyID" = tot."TaxonomyID"
                WHERE t."TaxonomyCode" = 'COREP_3.2' AND tot."TemplateOrTableType" = 'BusinessTable'
                """,
                1)
            .Select(r => r[0]!)
            .ToHashSet(StringComparer.Ordinal);

        // Checked census, not just printed.
        Assert.True(reference.Count > 0, "The BusinessTable code query of the 3.2 reference returned 0 rows: the comparison would be empty.");

        var missing = reference.Except(generated, StringComparer.Ordinal).OrderBy(c => c, StringComparer.Ordinal).ToList();
        var extra = generated.Except(reference, StringComparer.Ordinal).Count();

        Assert.True(
            missing.Count == 0,
            $"mTemplateOrTable BusinessTable: {missing.Count} COREP_3.2 codes are not in " +
            $"the generated output. Examples: {string.Join(", ", missing.Take(15))}. (Generated: {generated.Count}, " +
            $"reference: {reference.Count}, legitimate excess: {extra}.) Remember: the " +
            "Level of these nodes is NOT compared (3 in 3.2, 1 here), only the code.");

        Assert.True(
            generated.SetEquals(reference),
            $"The BusinessTable codes of COREP 3.2 are not EXACTLY equal to the 217->147 of " +
            $"the 3.2 reference. Generated: {generated.Count}, reference: {reference.Count}.");
    }

    [DataFact]
    public void MTableCodes_GeneratedContainsReference32_ForCorep32()
    {
        var generated = QueryHelpers.Rows(_fixture.GeneratedConnection, "SELECT \"TableCode\" FROM \"mTable\"", 1)
            .Select(r => r[0]!)
            .ToHashSet(StringComparer.Ordinal);

        var reference = QueryHelpers.Rows(
                _fixture.Reference32Connection,
                """
                SELECT DISTINCT m."TableCode" FROM "mTable" m
                JOIN "mTaxonomyTable" tt ON tt."TableID" = m."TableID"
                JOIN "mTaxonomy" t ON t."TaxonomyID" = tt."TaxonomyID"
                WHERE t."TaxonomyCode" = 'COREP_3.2'
                """,
                1)
            .Select(r => r[0]!)
            .ToHashSet(StringComparer.Ordinal);

        // Checked census, not just printed.
        Assert.True(reference.Count > 0, "The mTable.TableCode query of the 3.2 reference returned 0 rows: the comparison would be empty.");

        var missing = reference.Except(generated, StringComparer.Ordinal).OrderBy(c => c, StringComparer.Ordinal).ToList();
        var extra = generated.Except(reference, StringComparer.Ordinal).Count();

        Assert.True(
            missing.Count == 0,
            $"mTable.TableCode: {missing.Count} COREP_3.2 codes are not in the generated output. " +
            $"Examples: {string.Join(", ", missing.Take(15))}. (Generated: {generated.Count}, " +
            $"reference: {reference.Count}, legitimate excess: {extra}.)");

        Assert.True(
            generated.SetEquals(reference),
            $"The mTable codes of COREP 3.2 are not EXACTLY equal to those of the 3.2 reference. " +
            $"Generated: {generated.Count}, reference: {reference.Count}.");
    }
}
