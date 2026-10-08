using System.Text.RegularExpressions;
using EbaDpm.Converter.Tests.Dictionary;

namespace EbaDpm.Converter.Tests.Modules;

/// <summary>
/// "Integrity" level for modules: FKs without orphans, density of the <c>Order</c> of
/// <c>mModuleBusinessTemplate</c>, the 1:1 synthetic copy of <c>mConceptualModule</c>, the
/// absence of <c>ConceptType = 'ConceptualModule'</c>, the constants of <c>mModule</c>, and the
/// rule (contrary to the EIOPA documentation) that <c>BusinessTemplateID</c> ALWAYS points to a
/// <c>Level</c> = 1 node.
///
/// All of it belongs to the CRITICAL layer: referential integrity, dictionary and template
/// coordinates.
/// </summary>
[Collection("Modules")]
public sealed class ModuleIntegrityTests
{
    private readonly ModulesFixture _fixture;

    public ModuleIntegrityTests(ModulesFixture fixture)
    {
        _fixture = fixture;
    }

    [DataFact]
    public void Sanity_ModulesWereActuallyLoaded()
    {
        // Minimal guard: if this is 0, all the other tests in this file would pass "by
        // vacuity" and hide a complete regression of ModuleLoader.
        Assert.True(_fixture.ModuleResult.ModuleRows > 0, "ModuleLoader did not load any mModule row.");
        Assert.True(_fixture.ModuleResult.ModuleBusinessTemplateRows > 0, "ModuleLoader did not load any mModuleBusinessTemplate row.");
    }

    // ------------------------------------------------------------------
    // FKs without orphans
    // ------------------------------------------------------------------

    [DataFact]
    public void MModule_TaxonomyID_HasNoOrphans() =>
        QueryHelpers.AssertNoOrphans(_fixture.GeneratedConnection, "mModule", "TaxonomyID", "mTaxonomy", "TaxonomyID");

    [DataFact]
    public void MModule_ConceptualModuleID_HasNoOrphans() =>
        QueryHelpers.AssertNoOrphans(_fixture.GeneratedConnection, "mModule", "ConceptualModuleID", "mConceptualModule", "ConceptualModuleID");

    [DataFact]
    public void MModule_ConceptID_HasNoOrphans() =>
        QueryHelpers.AssertNoOrphans(_fixture.GeneratedConnection, "mModule", "ConceptID", "mConcept", "ConceptID");

    [DataFact]
    public void MModuleBusinessTemplate_ModuleID_HasNoOrphans() =>
        QueryHelpers.AssertNoOrphans(_fixture.GeneratedConnection, "mModuleBusinessTemplate", "ModuleID", "mModule", "ModuleID");

    [DataFact]
    public void MModuleBusinessTemplate_BusinessTemplateID_HasNoOrphans() =>
        QueryHelpers.AssertNoOrphans(_fixture.GeneratedConnection, "mModuleBusinessTemplate", "BusinessTemplateID", "mTemplateOrTable", "TemplateOrTableID");

    // ------------------------------------------------------------------
    // mModuleBusinessTemplate: PK (ModuleID, Order) without duplicates (SQLite already guarantees
    // it on write), Order dense and 0-based per module.
    // ------------------------------------------------------------------

    [DataFact]
    public void Order_IsDenseAndZeroBased_PerModule()
    {
        var rows = QueryHelpers.Rows(
            _fixture.GeneratedConnection,
            "SELECT \"ModuleID\", \"Order\" FROM \"mModuleBusinessTemplate\" ORDER BY \"ModuleID\", \"Order\"",
            2);

        Assert.True(rows.Count > 0, "mModuleBusinessTemplate is empty: the density of Order cannot be checked.");

        var byModule = rows
            .Select(r => (ModuleId: int.Parse(r[0]!), Order: int.Parse(r[1]!)))
            .GroupBy(r => r.ModuleId);

        var failures = new List<string>();
        foreach (var group in byModule)
        {
            var orders = group.Select(g => g.Order).OrderBy(o => o).ToList();
            var expected = Enumerable.Range(0, orders.Count).ToList();
            if (!orders.SequenceEqual(expected))
            {
                failures.Add($"ModuleID={group.Key}: observed Order [{string.Join(",", orders)}], expected dense 0-based [{string.Join(",", expected)}]");
            }
        }

        Assert.True(failures.Count == 0, $"{failures.Count} modules with a non-dense / non 0-based Order:\n" + string.Join("\n", failures.Take(20)));
    }

    // ------------------------------------------------------------------
    // mConceptualModule: synthetic 1:1 copy of mModule, without orphans.
    // ------------------------------------------------------------------

    [DataFact]
    public void ConceptualModule_IsExactlyOneRowPerModule_WithSameIdCodeAndLabel()
    {
        var moduleCount = QueryHelpers.Scalar(_fixture.GeneratedConnection, "SELECT COUNT(*) FROM \"mModule\"");
        var conceptualModuleCount = QueryHelpers.Scalar(_fixture.GeneratedConnection, "SELECT COUNT(*) FROM \"mConceptualModule\"");

        Assert.True(moduleCount > 0, "mModule is empty.");
        Assert.Equal(moduleCount, conceptualModuleCount);

        var mismatches = QueryHelpers.Rows(
            _fixture.GeneratedConnection,
            """
            SELECT m."ModuleID", m."ModuleCode", m."ModuleLabel",
                   cm."ConceptualModuleID", cm."ConceptualModuleCode", cm."ConceptualModuleLabel"
            FROM "mModule" m
            LEFT JOIN "mConceptualModule" cm ON cm."ConceptualModuleID" = m."ModuleID"
            WHERE cm."ConceptualModuleID" IS NULL
               OR cm."ConceptualModuleCode" IS NOT m."ModuleCode"
               OR cm."ConceptualModuleLabel" IS NOT m."ModuleLabel"
            """,
            6);

        Assert.True(
            mismatches.Count == 0,
            $"{mismatches.Count} mModule rows without their 1:1 mConceptualModule (same ID/code/label). " +
            $"Examples: {string.Join("; ", mismatches.Take(10).Select(r => string.Join("|", r)))}");

        // And without orphans in the other direction: no ConceptualModuleID without its mModule.
        QueryHelpers.AssertNoOrphans(_fixture.GeneratedConnection, "mConceptualModule", "ConceptualModuleID", "mModule", "ModuleID");
    }

    // ------------------------------------------------------------------
    // mConcept: ConceptType = 'ConceptualModule' NEVER appears; ConceptType = 'Module' has exactly
    // |mModule| rows.
    // ------------------------------------------------------------------

    [DataFact]
    public void MConcept_NeverHasConceptualModuleType()
    {
        var count = QueryHelpers.Scalar(
            _fixture.GeneratedConnection,
            "SELECT COUNT(*) FROM \"mConcept\" WHERE \"ConceptType\" = 'ConceptualModule'");

        Assert.True(count == 0, $"mConcept has {count} rows with ConceptType='ConceptualModule': they should not exist.");
    }

    [DataFact]
    public void MConcept_ModuleTypeCount_EqualsModuleRowCount()
    {
        var moduleCount = QueryHelpers.Scalar(_fixture.GeneratedConnection, "SELECT COUNT(*) FROM \"mModule\"");
        var conceptModuleCount = QueryHelpers.Scalar(
            _fixture.GeneratedConnection,
            "SELECT COUNT(*) FROM \"mConcept\" WHERE \"ConceptType\" = 'Module'");

        Assert.Equal(moduleCount, conceptModuleCount);
    }

    // ------------------------------------------------------------------
    // Constants of mModule: DefaultFrequency and JSONSchemaRef NULL; AutogenerateRefs = 1;
    // JsonBlob = zero-length BLOB, NOT NULL. On 100% of the rows.
    // ------------------------------------------------------------------

    [DataFact]
    public void DefaultFrequency_IsNull_ForAllRows()
    {
        var total = QueryHelpers.Scalar(_fixture.GeneratedConnection, "SELECT COUNT(*) FROM \"mModule\"");
        var nonNull = QueryHelpers.Scalar(_fixture.GeneratedConnection, "SELECT COUNT(*) FROM \"mModule\" WHERE \"DefaultFrequency\" IS NOT NULL");
        Assert.True(total > 0);
        Assert.Equal(0, nonNull);
    }

    [DataFact]
    public void JSONSchemaRef_IsNull_ForAllRows()
    {
        var total = QueryHelpers.Scalar(_fixture.GeneratedConnection, "SELECT COUNT(*) FROM \"mModule\"");
        var nonNull = QueryHelpers.Scalar(_fixture.GeneratedConnection, "SELECT COUNT(*) FROM \"mModule\" WHERE \"JSONSchemaRef\" IS NOT NULL");
        Assert.True(total > 0);
        Assert.Equal(0, nonNull);
    }

    [DataFact]
    public void AutogenerateRefs_IsOne_ForAllRows()
    {
        var total = QueryHelpers.Scalar(_fixture.GeneratedConnection, "SELECT COUNT(*) FROM \"mModule\"");
        var notOne = QueryHelpers.Scalar(_fixture.GeneratedConnection, "SELECT COUNT(*) FROM \"mModule\" WHERE \"AutogenerateRefs\" IS NOT 1");
        Assert.True(total > 0);
        Assert.Equal(0, notOne);
    }

    [DataFact]
    public void JsonBlob_IsEmptyBlob_NotNull_ForAllRows()
    {
        var total = QueryHelpers.Scalar(_fixture.GeneratedConnection, "SELECT COUNT(*) FROM \"mModule\"");
        var nullCount = QueryHelpers.Scalar(_fixture.GeneratedConnection, "SELECT COUNT(*) FROM \"mModule\" WHERE \"JsonBlob\" IS NULL");
        var nonEmptyCount = QueryHelpers.Scalar(_fixture.GeneratedConnection, "SELECT COUNT(*) FROM \"mModule\" WHERE length(\"JsonBlob\") <> 0");

        Assert.True(total > 0);
        Assert.Equal(0, nullCount);
        Assert.Equal(0, nonEmptyCount);
    }

    // ------------------------------------------------------------------
    // XBRLSchemaRef: never NULL, and matches
    // http://www.eba.europa.eu/eu/fr/xbrl/crr/fws/{framework}/{standard}[/{date}]/mod/{module code
    // in lower case}.xsd
    // ------------------------------------------------------------------

    private static readonly Regex XbrlSchemaRefPattern = new(
        @"^http://www\.eba\.europa\.eu/eu/fr/xbrl/crr/fws/[^/]+/[^/]+(/[0-9]{4}-[0-9]{2}-[0-9]{2})?/mod/(?<code>[^/]+)\.xsd$",
        RegexOptions.Compiled);

    [DataFact]
    public void XBRLSchemaRef_IsNeverNull()
    {
        var total = QueryHelpers.Scalar(_fixture.GeneratedConnection, "SELECT COUNT(*) FROM \"mModule\"");
        var nullCount = QueryHelpers.Scalar(_fixture.GeneratedConnection, "SELECT COUNT(*) FROM \"mModule\" WHERE \"XBRLSchemaRef\" IS NULL");
        Assert.True(total > 0);
        Assert.Equal(0, nullCount);
    }

    [DataFact]
    public void XBRLSchemaRef_MatchesExpectedFormat_AndEndsWithModuleCodeLowercase()
    {
        var rows = QueryHelpers.Rows(
            _fixture.GeneratedConnection,
            "SELECT \"ModuleCode\", \"XBRLSchemaRef\" FROM \"mModule\"",
            2);

        Assert.True(rows.Count > 0);

        var failures = new List<string>();
        foreach (var row in rows)
        {
            var moduleCode = row[0]!;
            var schemaRef = row[1]!;
            var match = XbrlSchemaRefPattern.Match(schemaRef);
            if (!match.Success)
            {
                failures.Add($"{moduleCode}: '{schemaRef}' does not match the expected pattern");
                continue;
            }

            var lastSegment = match.Groups["code"].Value;
            if (!string.Equals(lastSegment, moduleCode, StringComparison.OrdinalIgnoreCase))
            {
                failures.Add($"{moduleCode}: last segment '{lastSegment}' does not match the lower-case ModuleCode");
            }
        }

        Assert.True(failures.Count == 0, $"{failures.Count} XBRLSchemaRef values with the wrong shape:\n" + string.Join("\n", failures.Take(20)));
    }

    // ------------------------------------------------------------------
    // BusinessTemplateID ALWAYS points to a Level = 1 node: the EIOPA documentation says
    // otherwise (Level 2 or 3) and is wrong; the measured data prevails.
    // ------------------------------------------------------------------

    [DataFact]
    public void BusinessTemplateID_AlwaysPointsToLevel1Node_NeverLevel2()
    {
        var rows = QueryHelpers.Rows(
            _fixture.GeneratedConnection,
            """
            SELECT mbt."ModuleID", mbt."Order", tot."TemplateOrTableID", tot."TemplateOrTableCode", tot."Level", tot."TemplateOrTableType"
            FROM "mModuleBusinessTemplate" mbt
            JOIN "mTemplateOrTable" tot ON tot."TemplateOrTableID" = mbt."BusinessTemplateID"
            WHERE tot."Level" <> 1
            """,
            6);

        Assert.True(
            rows.Count == 0,
            $"{rows.Count} mModuleBusinessTemplate rows point to an mTemplateOrTable node with Level <> 1 " +
            $"(Level 1 is always required). Examples: {string.Join("; ", rows.Take(10).Select(r => string.Join("|", r)))}");

        var typeRows = QueryHelpers.Rows(
            _fixture.GeneratedConnection,
            """
            SELECT DISTINCT tot."TemplateOrTableType"
            FROM "mModuleBusinessTemplate" mbt
            JOIN "mTemplateOrTable" tot ON tot."TemplateOrTableID" = mbt."BusinessTemplateID"
            """,
            1);

        Assert.True(
            typeRows.Count == 1 && typeRows[0][0] == "TableGroup",
            $"BusinessTemplateID should always point to nodes with TemplateOrTableType='TableGroup' (the L1 of the tree). " +
            $"Observed types: {string.Join(",", typeRows.Select(r => r[0]))}");
    }
}
