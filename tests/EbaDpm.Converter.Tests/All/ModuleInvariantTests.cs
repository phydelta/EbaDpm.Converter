using EbaDpm.Converter.Tests.Dictionary;

namespace EbaDpm.Converter.Tests.All;

/// <summary>Module invariants A-MOD-04 and A-MOD-06, over <c>--all</c>.</summary>
[Collection("All")]
[Trait("Tier", "RealData")]
public sealed class ModuleInvariantTests
{
    private readonly AllFixture _fixture;

    public ModuleInvariantTests(AllFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// A-MOD-04: no <c>XBRLSchemaRef</c> has the form <c>{framework}_{version}</c> (e.g.
    /// <c>if_3.2</c>, <c>gsii_3.2</c>) -- that form is an exception found in the 3.2 reference
    /// compared with our output, not something our own output produces: no converter rule
    /// produces it (0/415 in the Access).
    /// </summary>
    [DataFact]
    public void NoXbrlSchemaRef_HasTheFrameworkUnderscoreVersionForm()
    {
        // GLOB (not LIKE: SQLite LIKE does not support character classes) detects the segment
        // "/fws/<something>_<digit>.<digit>/" -- the exact form found in the 3.2 reference.
        var rows = QueryHelpers.Rows(
            _fixture.GeneratedConnection,
            """
            SELECT "ModuleCode", "XBRLSchemaRef" FROM "mModule"
            WHERE "XBRLSchemaRef" GLOB '*/fws/*_[0-9].[0-9]/*'
            """,
            2);

        Assert.True(
            rows.Count == 0,
            $"{rows.Count} XBRLSchemaRef with the form '{{framework}}_{{version}}' (A-MOD-04). Examples: " +
            string.Join(", ", rows.Take(10).Select(r => $"{r[0]}:{r[1]}")));
    }

    /// <summary>A-MOD-06: the node pointed to by mModuleBusinessTemplate.BusinessTemplateID belongs to the SAME taxonomy as the module.</summary>
    [DataFact]
    public void BusinessTemplate_BelongsToTheSameTaxonomyAsItsModule()
    {
        var violations = QueryHelpers.Scalar(
            _fixture.GeneratedConnection,
            """
            SELECT COUNT(*) FROM "mModuleBusinessTemplate" mbt
            JOIN "mModule" m ON m."ModuleID" = mbt."ModuleID"
            JOIN "mTemplateOrTable" tot ON tot."TemplateOrTableID" = mbt."BusinessTemplateID"
            WHERE tot."TaxonomyID" <> m."TaxonomyID"
            """);

        Assert.True(violations == 0, $"{violations} mModuleBusinessTemplate links whose pointed-to node belongs to a taxonomy other than that of the module (A-MOD-06).");
    }
}
